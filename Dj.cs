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
/// 2. The speech-cleanup DSP in `VoiceSettings` (denoise, background-sound removal)
///    is set to the menu values (default: untouched audio).
/// Writes run every LateUpdate so the game's own voice logic can't flip them back.
/// </summary>
public class Dj : MonoBehaviour
{
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> MusicMode;
    internal static ConfigEntry<NoiseSuppressionLevels> Denoise;
    internal static ConfigEntry<bool> BgRemoval;
    internal static ConfigEntry<float> BgAmount;
    internal static ConfigEntry<bool> Diagnostics;
    internal static ConfigEntry<KeyCode> ToggleKey;

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

    internal static void Bind(ConfigEntry<bool> enabled, ConfigEntry<bool> musicMode,
        ConfigEntry<NoiseSuppressionLevels> denoise, ConfigEntry<bool> bgRemoval, ConfigEntry<float> bgAmount,
        ConfigEntry<bool> diagnostics, ConfigEntry<KeyCode> toggleKey)
    {
        Enabled = enabled;
        MusicMode = musicMode;
        Denoise = denoise;
        BgRemoval = bgRemoval;
        BgAmount = bgAmount;
        Diagnostics = diagnostics;
        ToggleKey = toggleKey;
    }

    void Update()
    {
        if (Input.GetKeyDown(ToggleKey.Value))
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
            if (_settings == null) FindSettings();
            if (_roomTriggers.Length + _proxTriggers.Length == 0)
            {
                if (Time.time < _retry) return;
                _retry = Time.time + 2f;
                CollectTriggers();
            }

            Apply();
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
            // No triggers under the local player: the game must use global triggers,
            // so take the scene-wide set (logged loudly; diagnostics shows the effect).
            _roomTriggers = room;
            _proxTriggers = prox;
            _scopedToPlayer = false;
            Plugin.Log.LogWarning($"DJ: no triggers under local player, using {_roomTriggers.Length} room + {_proxTriggers.Length} proximity triggers scene-wide");
        }

        if (!_loggedTriggers)
        {
            _loggedTriggers = true;
            Plugin.Log.LogInfo($"DJ triggers: room={_roomTriggers.Length} prox={_proxTriggers.Length} scopedToPlayer={_scopedToPlayer}");
        }
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

        foreach (var t in _roomTriggers) { try { t.Mode = CommActivationMode.Open; } catch (Exception e) { Plugin.Log.LogWarning($"DJ trigger write failed: {e.Message}"); } }
        foreach (var t in _proxTriggers) { try { t.Mode = CommActivationMode.Open; } catch (Exception e) { Plugin.Log.LogWarning($"DJ trigger write failed: {e.Message}"); } }

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
            _settings.DenoiseAmount = Denoise.Value;
            _settings.BackgroundSoundRemovalEnabled = BgRemoval.Value;
            _settings.BackgroundSoundRemovalAmount = BgAmount.Value;
        }

        _applied = true;
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
            Plugin.Log.LogInfo("DJ: voice pipeline restored");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"DJ restore failed: {e.Message}"); }
    }

    void OnDestroy()
    {
        try { if (_applied) Restore(); } catch { }
    }
}
