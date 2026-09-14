using System;
using BepInEx.Configuration;
using Dissonance;
using Dissonance.Audio.Capture;
using Dissonance.Config;
using UnityEngine;

namespace BigDJ;

/// <summary>
/// Music mode for voice chat. Two switches, both restored on disable:
/// 1. The local broadcast trigger is forced to `CommActivationMode.Open` (transmit
///    continuously) instead of voice-activation gating, which is what makes music pump.
///    Range still applies — only nearby players (overlapping grid cells) hear it.
/// 2. The speech-cleanup DSP in `VoiceSettings` (denoise, background-sound removal)
///    is set to the menu preset (default: untouched audio).
/// Writes run every LateUpdate so the game's own voice logic can't flip them back,
/// but only when the value actually differs. F9 is gated on the master switch.
/// Transmit start/stop edges always log; transitions log their restored values.
/// </summary>
public class Dj : MonoBehaviour
{
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> MusicMode;
    internal static ConfigEntry<DspPreset> Preset;
    internal static ConfigEntry<NoiseSuppressionLevels> Denoise;
    internal static ConfigEntry<bool> BgRemoval;
    internal static ConfigEntry<float> BgAmount;
    internal static ConfigEntry<bool> Diagnostics;
    internal static ConfigEntry<KeyCode> ToggleKey;
    internal static ConfigEntry<bool> Overlay;

    PlayerCharacter _player;
    VoiceBroadcastTrigger[] _roomTriggers = new VoiceBroadcastTrigger[0];
    VoiceProximityBroadcastTrigger[] _proxTriggers = new VoiceProximityBroadcastTrigger[0];
    bool _scopedToPlayer;
    bool _loggedTriggers;
    CommActivationMode _prevMode;
    bool _havePrevMode;
    VoiceSettings _settings;
    NoiseSuppressionLevels _origDenoise;
    bool _origBgEnabled;
    float _origBgAmount;
    bool _haveOrigSettings;
    bool _applied;
    float _retry;
    float _probeNext;
    float _revalidateNext;
    bool _wasTransmitting;
    bool _haveTransmitState;

    internal static void Bind(ConfigEntry<bool> enabled, ConfigEntry<bool> musicMode,
        ConfigEntry<DspPreset> preset,
        ConfigEntry<NoiseSuppressionLevels> denoise, ConfigEntry<bool> bgRemoval, ConfigEntry<float> bgAmount,
        ConfigEntry<bool> diagnostics, ConfigEntry<KeyCode> toggleKey, ConfigEntry<bool> overlay)
    {
        Enabled = enabled;
        MusicMode = musicMode;
        Preset = preset;
        Denoise = denoise;
        BgRemoval = bgRemoval;
        BgAmount = bgAmount;
        Diagnostics = diagnostics;
        ToggleKey = toggleKey;
        Overlay = overlay;
    }

    void Update()
    {
        if (Enabled.Value && Input.GetKeyDown(ToggleKey.Value))
        {
            MusicMode.Value = !MusicMode.Value;
            Plugin.Log.LogInfo($"Music mode {(MusicMode.Value ? "on" : "off")}");
        }
    }

    void LateUpdate()
    {
        try
        {
            if (!Enabled.Value || !MusicMode.Value)
            {
                if (_applied) Restore();
                return;
            }

            if (_player == null)
            {
                if (Time.time < _retry) return;
                _retry = Time.time + 2f;
                if (!FindPlayer()) return;
            }
            if (_settings == null)
            {
                if (Time.time < _retry) return;
                _retry = Time.time + 2f;
                FindSettings();
            }
            if (_roomTriggers.Length + _proxTriggers.Length == 0)
            {
                if (Time.time < _retry) return;
                _retry = Time.time + 2f;
                CollectTriggers();
            }

            Revalidate();
            if (_player == null) return;

            Apply();
            CheckTransmitEdge();
            if (Diagnostics.Value) Probe();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"DJ failed: {e.Message}");
            _player = null;
            _settings = null;
            _roomTriggers = new VoiceBroadcastTrigger[0];
            _proxTriggers = new VoiceProximityBroadcastTrigger[0];
            _retry = Time.time + 2f;
        }
    }

    bool FindPlayer()
    {
        try
        {
            var me = WorldManager.localPlayerCharacter;
            if (me != null && me.playerNetworking != null && me.playerNetworking.isLocalPlayer)
            {
                _player = me;
                _loggedTriggers = false;
                Plugin.Log.LogInfo($"DJ bound to '{_player.name}'");
                return true;
            }
        }
        catch { }
        foreach (var pc in UnityEngine.Object.FindObjectsOfType<PlayerCharacter>())
        {
            var net = pc?.playerNetworking;
            if (net == null || !net.isLocalPlayer) continue;
            _player = pc;
            _loggedTriggers = false;
            Plugin.Log.LogInfo($"DJ bound to '{_player.name}'");
            return true;
        }
        return false;
    }

    /// <summary>Scene changes can destroy the player/triggers out from under us.
    /// Re-check on a slow timer instead of waiting for an exception.</summary>
    void Revalidate()
    {
        if (Time.time < _revalidateNext) return;
        _revalidateNext = Time.time + 5f;
        try
        {
            if (_player == null || _player.gameObject == null
                || _roomTriggers.Length + _proxTriggers.Length == 0)
            {
                if (_player != null || _roomTriggers.Length + _proxTriggers.Length > 0)
                    Plugin.Log.LogInfo("DJ: scene changed, re-acquiring player and triggers");
                _player = null;
                _roomTriggers = new VoiceBroadcastTrigger[0];
                _proxTriggers = new VoiceProximityBroadcastTrigger[0];
                _havePrevMode = false;
                _loggedTriggers = false;
            }
        }
        catch { _player = null; }
    }

    void FindSettings()
    {
        foreach (var s in Resources.FindObjectsOfTypeAll<VoiceSettings>())
        {
            if (s == null) continue;
            _settings = s;
            Plugin.Log.LogInfo($"DJ using VoiceSettings '{s.name}'");
            return;
        }
    }

    bool Mine(Component c)
    {
        if (_player == null || c == null) return false;
        if (c.gameObject == _player.gameObject) return true;
        var t = c.transform;
        return t != null && t.IsChildOf(_player.transform);
    }

    /// <summary>True when the trigger lives under a NON-local player avatar —
    /// a dormant component we must not touch.</summary>
    bool OwnedByRemote(Component c)
    {
        try
        {
            var p = c?.transform;
            while (p != null)
            {
                var pc = p.gameObject.GetComponent<PlayerCharacter>();
                if (pc != null)
                {
                    var net = pc.playerNetworking;
                    return net == null || !net.isLocalPlayer;
                }
                p = p.parent;
            }
        }
        catch { }
        return false;
    }

    void CollectTriggers()
    {
        var room = UnityEngine.Object.FindObjectsOfType<VoiceBroadcastTrigger>();
        var prox = UnityEngine.Object.FindObjectsOfType<VoiceProximityBroadcastTrigger>();

        var myRoom = new System.Collections.Generic.List<VoiceBroadcastTrigger>();
        var myProx = new System.Collections.Generic.List<VoiceProximityBroadcastTrigger>();
        foreach (var t in room) if (Mine(t)) myRoom.Add(t);
        foreach (var t in prox) if (Mine(t)) myProx.Add(t);

        if (myRoom.Count + myProx.Count > 0)
        {
            _roomTriggers = myRoom.ToArray();
            _proxTriggers = myProx.ToArray();
            _scopedToPlayer = true;
        }
        else
        {
            // No triggers under the local player: fall back to scene-wide triggers,
            // but skip ones owned by remote avatars (dormant components we must not touch).
            var allRoom = new System.Collections.Generic.List<VoiceBroadcastTrigger>();
            var allProx = new System.Collections.Generic.List<VoiceProximityBroadcastTrigger>();
            foreach (var t in room) if (!OwnedByRemote(t)) allRoom.Add(t);
            foreach (var t in prox) if (!OwnedByRemote(t)) allProx.Add(t);
            _roomTriggers = allRoom.ToArray();
            _proxTriggers = allProx.ToArray();
            _scopedToPlayer = false;
            Plugin.Log.LogWarning($"DJ: no triggers under local player, using {_roomTriggers.Length} room + {_proxTriggers.Length} proximity triggers scene-wide (remote-owned skipped)");
            foreach (var t in _roomTriggers) Plugin.Log.LogInfo($"DJ fallback trigger: room '{t.gameObject.name}'");
            foreach (var t in _proxTriggers) Plugin.Log.LogInfo($"DJ fallback trigger: prox '{t.gameObject.name}'");
        }

        if (!_loggedTriggers)
        {
            _loggedTriggers = true;
            Plugin.Log.LogInfo($"DJ triggers: room={_roomTriggers.Length} prox={_proxTriggers.Length} scopedToPlayer={_scopedToPlayer}");
        }
    }

    /// <summary>Named DSP combos so one selector covers the common cases;
    /// Custom falls through to the three knobs.</summary>
    void ResolveDsp(out NoiseSuppressionLevels denoise, out bool bgRemoval, out float bgAmount)
    {
        var preset = Preset != null ? Preset.Value : DspPreset.Custom;
        if (preset == DspPreset.LightCleanup)
        {
            denoise = NoiseSuppressionLevels.Disabled;
            bgRemoval = true;
            bgAmount = 0.3f;
            return;
        }
        if (preset == DspPreset.Untouched)
        {
            denoise = NoiseSuppressionLevels.Disabled;
            bgRemoval = false;
            bgAmount = 0f;
            return;
        }
        denoise = Denoise.Value;
        bgRemoval = BgRemoval.Value;
        bgAmount = BgAmount.Value;
    }

    void Apply()
    {
        if (!_havePrevMode)
        {
            if (_roomTriggers.Length > 0) _prevMode = _roomTriggers[0].Mode;
            else if (_proxTriggers.Length > 0) _prevMode = _proxTriggers[0].Mode;
            else Plugin.Log.LogWarning("DJ: no broadcast triggers found — DSP only, transmission stays VAD-gated");
            _havePrevMode = true;
            Plugin.Log.LogInfo($"DJ previous trigger mode: {_prevMode}");
        }

        foreach (var t in _roomTriggers) { try { if (t.Mode != CommActivationMode.Open) t.Mode = CommActivationMode.Open; } catch (Exception e) { Plugin.Log.LogWarning($"DJ trigger write failed: {e.Message}"); } }
        foreach (var t in _proxTriggers) { try { if (t.Mode != CommActivationMode.Open) t.Mode = CommActivationMode.Open; } catch (Exception e) { Plugin.Log.LogWarning($"DJ trigger write failed: {e.Message}"); } }

        if (_settings != null)
        {
            if (!_haveOrigSettings)
            {
                _origDenoise = _settings.DenoiseAmount;
                _origBgEnabled = _settings.BackgroundSoundRemovalEnabled;
                _origBgAmount = _settings.BackgroundSoundRemovalAmount;
                _haveOrigSettings = true;
                Plugin.Log.LogInfo($"DJ previous DSP: denoise={_origDenoise} bgRemoval={_origBgEnabled}/{_origBgAmount}");
            }
            ResolveDsp(out var denoise, out var bgRemoval, out var bgAmount);
            if (_settings.DenoiseAmount != denoise) _settings.DenoiseAmount = denoise;
            if (_settings.BackgroundSoundRemovalEnabled != bgRemoval) _settings.BackgroundSoundRemovalEnabled = bgRemoval;
            if (_settings.BackgroundSoundRemovalAmount != bgAmount) _settings.BackgroundSoundRemovalAmount = bgAmount;
        }

        _applied = true;
    }

    /// <summary>Edge-triggered transmit log — rare lines, so always on, not gated on Diagnostics.</summary>
    void CheckTransmitEdge()
    {
        try
        {
            bool any = false;
            foreach (var t in _roomTriggers) { if (t != null && t.IsTransmitting) { any = true; break; } }
            if (!any) foreach (var t in _proxTriggers) { if (t != null && t.IsTransmitting) { any = true; break; } }
            if (!_haveTransmitState || any != _wasTransmitting)
            {
                _haveTransmitState = true;
                _wasTransmitting = any;
                Plugin.Log.LogInfo(any ? "DJ transmitting started" : "DJ transmitting stopped");
            }
        }
        catch (Exception e) { Plugin.Log.LogDebug($"DJ edge check failed: {e.Message}"); }
    }

    void OnGUI()
    {
        try
        {
            if (Overlay == null || !Overlay.Value || !Enabled.Value) return;
            GUI.Box(new Rect(10f, 10f, 300f, 122f), "BigDJ");
            GUI.Label(new Rect(20f, 34f, 280f, 20f), "music: " + (MusicMode.Value ? "on" : "off"));
            GUI.Label(new Rect(20f, 54f, 280f, 20f), "transmitting: " + (_haveTransmitState && _wasTransmitting ? "yes" : "no"));
            GUI.Label(new Rect(20f, 74f, 280f, 20f), "triggers: room=" + _roomTriggers.Length + " prox=" + _proxTriggers.Length);
            GUI.Label(new Rect(20f, 94f, 280f, 20f), _settings != null
                ? "dsp: denoise=" + _settings.DenoiseAmount + " bg=" + _settings.BackgroundSoundRemovalEnabled + "/" + _settings.BackgroundSoundRemovalAmount
                : "dsp: n/a");
        }
        catch { }
    }

    void Probe()
    {
        if (Time.time < _probeNext) return;
        _probeNext = Time.time + 1f;
        try
        {
            foreach (var t in _roomTriggers)
                Plugin.Log.LogInfo($"DJ room '{t.gameObject.name}' mode={t.Mode} transmitting={t.IsTransmitting} muted={t.IsMuted}");
            foreach (var t in _proxTriggers)
                Plugin.Log.LogInfo($"DJ prox '{t.gameObject.name}' mode={t.Mode} transmitting={t.IsTransmitting} muted={t.IsMuted}");
            if (_settings != null)
                Plugin.Log.LogInfo($"DJ dsp denoise={_settings.DenoiseAmount} bgRemoval={_settings.BackgroundSoundRemovalEnabled}/{_settings.BackgroundSoundRemovalAmount}");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"DJ probe failed: {e.Message}"); }
    }

    void Restore()
    {
        _applied = false;
        try
        {
            if (_havePrevMode)
            {
                foreach (var t in _roomTriggers) { try { t.Mode = _prevMode; } catch { } }
                foreach (var t in _proxTriggers) { try { t.Mode = _prevMode; } catch { } }
                _havePrevMode = false;
            }
            if (_settings != null && _haveOrigSettings)
            {
                _settings.DenoiseAmount = _origDenoise;
                _settings.BackgroundSoundRemovalEnabled = _origBgEnabled;
                _settings.BackgroundSoundRemovalAmount = _origBgAmount;
                _haveOrigSettings = false;
            }
            foreach (var t in _roomTriggers) { try { Plugin.Log.LogInfo($"DJ restored room '{t.gameObject.name}' mode={t.Mode}"); } catch { } }
            foreach (var t in _proxTriggers) { try { Plugin.Log.LogInfo($"DJ restored prox '{t.gameObject.name}' mode={t.Mode}"); } catch { } }
            if (_settings != null) { try { Plugin.Log.LogInfo($"DJ restored dsp denoise={_settings.DenoiseAmount} bgRemoval={_settings.BackgroundSoundRemovalEnabled}/{_settings.BackgroundSoundRemovalAmount}"); } catch { } }
            _haveTransmitState = false;
            Plugin.Log.LogInfo("DJ: voice pipeline restored");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"DJ restore failed: {e.Message}"); }
    }

    void OnDestroy()
    {
        try { if (_applied) Restore(); } catch { }
    }
}
