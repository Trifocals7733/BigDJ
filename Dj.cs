using System;
using BepInEx.Configuration;
using Dissonance;
using Dissonance.Audio.Capture;
using Dissonance.Config;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Mirror;
using UnityEngine;

namespace BigDJ;

/// <summary>
/// Studio-grade music mode for voice chat.
/// 1. Broadcast Mode:
///    - Proximity3D: Continuous 3D spatialized transmission with untouched DSP.
///    - IslandRadio2D: Native Mirror 2D voice broadcast (CmdSet2DVoice) to all players,
///      bypassing distance attenuation, directional panning, and low-pass muffled angles.
/// 2. Dissonance High-Priority Channel: Elevates priority to ChannelPriority.High
///    so music packets are never ducked or dropped during multi-speaker talk.
/// 3. Dry Acoustics: Suppresses cave/building reverberation echoes (SelfEcho / echoAmount).
/// 4. Real-time Live Audio VU Meter: Real-time RMS and peak meter on the overlay HUD.
/// 5. Zero-Allocation Core: Uses WorldManager and local hierarchy scoping.
/// All states are tracked and safely restored on disable or toggle off.
/// </summary>
public class Dj : MonoBehaviour
{
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> MusicMode;
    internal static ConfigEntry<BroadcastMode> Mode;
    internal static ConfigEntry<DspPreset> Preset;
    internal static ConfigEntry<NoiseSuppressionLevels> Denoise;
    internal static ConfigEntry<bool> BgRemoval;
    internal static ConfigEntry<float> BgAmount;
    internal static ConfigEntry<bool> HighPriority;
    internal static ConfigEntry<bool> DryAcoustics;
    internal static ConfigEntry<bool> Diagnostics;
    internal static ConfigEntry<KeyCode> ToggleKey;
    internal static ConfigEntry<bool> Overlay;

    PlayerCharacter _player;
    VoiceBroadcastTrigger[] _roomTriggers = Array.Empty<VoiceBroadcastTrigger>();
    VoiceProximityBroadcastTrigger[] _proxTriggers = Array.Empty<VoiceProximityBroadcastTrigger>();
    bool _scopedToPlayer;
    bool _loggedTriggers;
    CommActivationMode _prevMode;
    bool _havePrevMode;

    VoiceSettings _settings;
    NoiseSuppressionLevels _origDenoise;
    bool _origBgEnabled;
    float _origBgAmount;
    bool _haveOrigSettings;

    bool _orig2DVoice;
    bool _haveOrig2DVoice;

    DissonanceComms _comms;
    ChannelPriority _origPriority;
    bool _haveOrigPriority;

    float _origEchoAmount;
    bool _haveOrigEchoAmount;
    bool _origSelfEchoOn;
    bool _haveOrigSelfEcho;

    LocalVoiceProvider _voiceProvider;
    float _livePeak;
    float _liveRms;
    float _liveDb = -99f;
    float _nextVuSample;

    bool _applied;
    float _retry;
    float _probeNext;
    float _revalidateNext;
    bool _wasTransmitting;
    bool _haveTransmitState;

    internal static void Bind(ConfigEntry<bool> enabled, ConfigEntry<bool> musicMode,
        ConfigEntry<BroadcastMode> mode, ConfigEntry<DspPreset> preset,
        ConfigEntry<NoiseSuppressionLevels> denoise, ConfigEntry<bool> bgRemoval, ConfigEntry<float> bgAmount,
        ConfigEntry<bool> highPriority, ConfigEntry<bool> dryAcoustics,
        ConfigEntry<bool> diagnostics, ConfigEntry<KeyCode> toggleKey, ConfigEntry<bool> overlay)
    {
        Enabled = enabled;
        MusicMode = musicMode;
        Mode = mode;
        Preset = preset;
        Denoise = denoise;
        BgRemoval = bgRemoval;
        BgAmount = bgAmount;
        HighPriority = highPriority;
        DryAcoustics = dryAcoustics;
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
            if (_comms == null || _voiceProvider == null)
            {
                FindComms();
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
            SampleVuMeter();
            CheckTransmitEdge();
            if (Diagnostics.Value) Probe();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"DJ failed: {e.Message}");
            _player = null;
            _settings = null;
            _comms = null;
            _voiceProvider = null;
            _roomTriggers = Array.Empty<VoiceBroadcastTrigger>();
            _proxTriggers = Array.Empty<VoiceProximityBroadcastTrigger>();
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
                Plugin.Log.LogInfo($"DJ bound to '{_player.name}' (WorldManager)");
                return true;
            }
        }
        catch { }

        try
        {
            var list = PlayerCharacter.allPlayerCharacters;
            if (list != null && list.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var pc = list[i];
                    if (pc == null) continue;
                    var net = pc.playerNetworking;
                    if (net != null && net.isLocalPlayer)
                    {
                        _player = pc;
                        _loggedTriggers = false;
                        Plugin.Log.LogInfo($"DJ bound to '{_player.name}' (allPlayerCharacters)");
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

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
                _roomTriggers = Array.Empty<VoiceBroadcastTrigger>();
                _proxTriggers = Array.Empty<VoiceProximityBroadcastTrigger>();
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

    void FindComms()
    {
        try
        {
            var wm = WorldManager.instance;
            if (wm != null)
            {
                if (wm.dissonanceComms != null) _comms = wm.dissonanceComms;
                if (wm.localVoiceProvider != null) _voiceProvider = wm.localVoiceProvider;
            }
        }
        catch { }

        if (_comms == null)
        {
            try { _comms = UnityEngine.Object.FindObjectOfType<DissonanceComms>(); }
            catch { }
        }
        if (_voiceProvider == null)
        {
            try { _voiceProvider = UnityEngine.Object.FindObjectOfType<LocalVoiceProvider>(); }
            catch { }
        }
    }

    bool Mine(Component c)
    {
        if (_player == null || c == null) return false;
        if (c.gameObject == _player.gameObject) return true;
        var t = c.transform;
        return t != null && t.IsChildOf(_player.transform);
    }

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
        // First fast-path: check local player hierarchy directly (zero scene-wide walking)
        if (_player != null)
        {
            try
            {
                var myRoom = _player.GetComponentsInChildren<VoiceBroadcastTrigger>(true);
                var myProx = _player.GetComponentsInChildren<VoiceProximityBroadcastTrigger>(true);
                if (myRoom != null && myProx != null && myRoom.Length + myProx.Length > 0)
                {
                    _roomTriggers = myRoom;
                    _proxTriggers = myProx;
                    _scopedToPlayer = true;
                    if (!_loggedTriggers)
                    {
                        _loggedTriggers = true;
                        Plugin.Log.LogInfo($"DJ triggers: room={_roomTriggers.Length} prox={_proxTriggers.Length} scopedToPlayer=true (local hierarchy)");
                    }
                    return;
                }
            }
            catch { }
        }

        // Fallback: scene-wide search (skipping remote avatars)
        var room = UnityEngine.Object.FindObjectsOfType<VoiceBroadcastTrigger>();
        var prox = UnityEngine.Object.FindObjectsOfType<VoiceProximityBroadcastTrigger>();

        var allRoom = new System.Collections.Generic.List<VoiceBroadcastTrigger>();
        var allProx = new System.Collections.Generic.List<VoiceProximityBroadcastTrigger>();
        foreach (var t in room) if (!OwnedByRemote(t)) allRoom.Add(t);
        foreach (var t in prox) if (!OwnedByRemote(t)) allProx.Add(t);
        _roomTriggers = allRoom.ToArray();
        _proxTriggers = allProx.ToArray();
        _scopedToPlayer = false;

        Plugin.Log.LogWarning($"DJ: no triggers under local player, using {_roomTriggers.Length} room + {_proxTriggers.Length} proximity triggers scene-wide (remote-owned skipped)");

        if (!_loggedTriggers)
        {
            _loggedTriggers = true;
            Plugin.Log.LogInfo($"DJ triggers: room={_roomTriggers.Length} prox={_proxTriggers.Length} scopedToPlayer={_scopedToPlayer}");
        }
    }

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
        // 1. Broadcast triggers: force CommActivationMode.Open
        if (!_havePrevMode)
        {
            if (_roomTriggers.Length > 0) _prevMode = _roomTriggers[0].Mode;
            else if (_proxTriggers.Length > 0) _prevMode = _proxTriggers[0].Mode;
            _havePrevMode = true;
            Plugin.Log.LogInfo($"DJ previous trigger mode: {_prevMode}");
        }

        foreach (var t in _roomTriggers) { try { if (t.Mode != CommActivationMode.Open) t.Mode = CommActivationMode.Open; } catch { } }
        foreach (var t in _proxTriggers) { try { if (t.Mode != CommActivationMode.Open) t.Mode = CommActivationMode.Open; } catch { } }

        // 2. DSP Settings
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

        // 3. Broadcast Mode: Island Radio 2D Voice
        if (_player != null && _player.playerNetworking != null)
        {
            var net = _player.playerNetworking;
            if (!_haveOrig2DVoice)
            {
                _orig2DVoice = net.Networkis2DVoice;
                _haveOrig2DVoice = true;
                Plugin.Log.LogInfo($"DJ previous 2D voice mode: {_orig2DVoice}");
            }

            bool want2D = Mode != null && Mode.Value == BroadcastMode.IslandRadio2D;
            if (net.Networkis2DVoice != want2D)
            {
                if (NetworkServer.active) net.Networkis2DVoice = want2D;
                try { net.CmdSet2DVoice(want2D); } catch { }
                Plugin.Log.LogInfo($"DJ switched broadcast mode: {(want2D ? "IslandRadio2D" : "Proximity3D")}");
            }
        }

        // 4. Dissonance Priority
        if (HighPriority != null && HighPriority.Value && _comms != null)
        {
            if (!_haveOrigPriority)
            {
                _origPriority = _comms.PlayerPriority;
                _haveOrigPriority = true;
                Plugin.Log.LogInfo($"DJ previous Dissonance priority: {_origPriority}");
            }
            if (_comms.PlayerPriority != ChannelPriority.High)
            {
                _comms.PlayerPriority = ChannelPriority.High;
            }
        }

        // 5. Dry Acoustics (suppress cave/island reverb)
        if (DryAcoustics != null && DryAcoustics.Value)
        {
            if (_player != null && _player.playerNetworking != null)
            {
                var net = _player.playerNetworking;
                if (!_haveOrigEchoAmount)
                {
                    _origEchoAmount = net.NetworkechoAmount;
                    _haveOrigEchoAmount = true;
                }
                if (net.NetworkechoAmount > 0.001f)
                {
                    if (NetworkServer.active) net.NetworkechoAmount = 0f;
                    try { net.CmdSetEchoAmount(0f); } catch { }
                }
            }

            try
            {
                var se = SelfEcho.Instance;
                if (se != null)
                {
                    if (!_haveOrigSelfEcho)
                    {
                        _origSelfEchoOn = se.EchoOn;
                        _haveOrigSelfEcho = true;
                    }
                    if (se.EchoOn) se.EchoOn = false;
                }
            }
            catch { }
        }

        _applied = true;
    }

    void SampleVuMeter()
    {
        if (Time.time < _nextVuSample) return;
        _nextVuSample = Time.time + 0.05f; // 20 Hz update rate

        if (_voiceProvider == null)
        {
            try { _voiceProvider = WorldManager.instance?.localVoiceProvider; } catch { }
        }
        if (_voiceProvider == null) return;

        try
        {
            var data = _voiceProvider.CachedVoiceData;
            if (data == null || data.Length == 0) return;

            int head = _voiceProvider.CachedVoiceWriteHead;
            int count = Math.Min(256, data.Length);
            float sumSq = 0f;
            float peak = 0f;

            for (int i = 0; i < count; i++)
            {
                int idx = (head - 1 - i + data.Length) % data.Length;
                float s = data[idx];
                float abs = s < 0f ? -s : s;
                if (abs > peak) peak = abs;
                sumSq += s * s;
            }

            _livePeak = peak;
            _liveRms = Mathf.Sqrt(sumSq / count);
            _liveDb = _liveRms > 0.0001f ? 20f * Mathf.Log10(_liveRms) : -99f;
        }
        catch { }
    }

    static string FormatVuBar(float peak, float db, out string status)
    {
        if (db < -55f)
        {
            status = "SILENT";
            return "[................]";
        }

        float norm = Mathf.Clamp01((db + 45f) / 45f);
        int bars = Mathf.RoundToInt(norm * 16f);
        char[] buf = new char[18];
        buf[0] = '[';
        for (int i = 0; i < 16; i++) buf[i + 1] = i < bars ? '|' : '.';
        buf[17] = ']';

        if (peak >= 0.95f) status = "PEAKING!";
        else if (db >= -24f) status = "OPTIMAL";
        else status = "ACTIVE";

        return new string(buf);
    }

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

            GUI.Box(new Rect(10f, 10f, 320f, 135f), "BigDJ v" + Plugin.PLUGIN_VERSION);

            string modeStr = Mode != null && Mode.Value == BroadcastMode.IslandRadio2D ? "Island Radio 2D" : "Proximity 3D";
            GUI.Label(new Rect(20f, 30f, 300f, 20f), "mode: " + modeStr + "  (" + (MusicMode.Value ? "ON" : "OFF") + ")");

            string priStr = _comms != null ? _comms.PlayerPriority.ToString().ToUpper() : "DEFAULT";
            string txStr = _haveTransmitState && _wasTransmitting ? "YES" : "NO";
            GUI.Label(new Rect(20f, 50f, 300f, 20f), "transmitting: " + txStr + "  |  priority: " + priStr);

            string vuBar = FormatVuBar(_livePeak, _liveDb, out string vuStatus);
            string dbStr = _liveDb > -90f ? _liveDb.ToString("F1") + " dB" : "-inf dB";
            GUI.Label(new Rect(20f, 70f, 300f, 20f), "VU: " + vuBar + " " + dbStr + " (" + vuStatus + ")");

            string dryStr = DryAcoustics != null && DryAcoustics.Value ? "DRY" : "NATURAL";
            GUI.Label(new Rect(20f, 90f, 300f, 20f), "triggers: room=" + _roomTriggers.Length + " prox=" + _proxTriggers.Length + "  |  echo: " + dryStr);

            GUI.Label(new Rect(20f, 110f, 300f, 20f), _settings != null
                ? "dsp: " + _settings.DenoiseAmount + " | bg=" + _settings.BackgroundSoundRemovalEnabled + "/" + _settings.BackgroundSoundRemovalAmount.ToString("F2")
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
                Plugin.Log.LogInfo($"DJ room '{t.gameObject.name}' mode={t.Mode} transmitting={t.IsTransmitting}");
            foreach (var t in _proxTriggers)
                Plugin.Log.LogInfo($"DJ prox '{t.gameObject.name}' mode={t.Mode} transmitting={t.IsTransmitting}");
            if (_settings != null)
                Plugin.Log.LogInfo($"DJ dsp denoise={_settings.DenoiseAmount} bg={_settings.BackgroundSoundRemovalEnabled}/{_settings.BackgroundSoundRemovalAmount} mode={Mode?.Value}");
            Plugin.Log.LogInfo($"DJ audio: peak={_livePeak:F2} rms={_liveRms:F3} db={_liveDb:F1}");
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
                Plugin.Log.LogInfo($"DJ restored trigger mode to: {_prevMode}");
            }

            if (_settings != null && _haveOrigSettings)
            {
                _settings.DenoiseAmount = _origDenoise;
                _settings.BackgroundSoundRemovalEnabled = _origBgEnabled;
                _settings.BackgroundSoundRemovalAmount = _origBgAmount;
                _haveOrigSettings = false;
                Plugin.Log.LogInfo($"DJ restored dsp denoise={_origDenoise} bgRemoval={_origBgEnabled}/{_origBgAmount}");
            }

            if (_haveOrig2DVoice && _player != null && _player.playerNetworking != null)
            {
                var net = _player.playerNetworking;
                if (NetworkServer.active) net.Networkis2DVoice = _orig2DVoice;
                try { net.CmdSet2DVoice(_orig2DVoice); } catch { }
                Plugin.Log.LogInfo($"DJ restored 2D voice mode: {_orig2DVoice}");
                _haveOrig2DVoice = false;
            }

            if (_haveOrigPriority && _comms != null)
            {
                _comms.PlayerPriority = _origPriority;
                Plugin.Log.LogInfo($"DJ restored Dissonance priority: {_origPriority}");
                _haveOrigPriority = false;
            }

            if (_haveOrigEchoAmount && _player != null && _player.playerNetworking != null)
            {
                var net = _player.playerNetworking;
                if (NetworkServer.active) net.NetworkechoAmount = _origEchoAmount;
                try { net.CmdSetEchoAmount(_origEchoAmount); } catch { }
                _haveOrigEchoAmount = false;
            }

            if (_haveOrigSelfEcho)
            {
                try { if (SelfEcho.Instance != null) SelfEcho.Instance.EchoOn = _origSelfEchoOn; } catch { }
                _haveOrigSelfEcho = false;
            }

            _haveTransmitState = false;
            _livePeak = 0f;
            _liveRms = 0f;
            _liveDb = -99f;
            Plugin.Log.LogInfo("DJ: voice pipeline fully restored");
        }
        catch (Exception e) { Plugin.Log.LogWarning($"DJ restore failed: {e.Message}"); }
    }

    void OnDisable()
    {
        try { if (_applied) Restore(); } catch { }
    }

    void OnDestroy()
    {
        try { if (_applied) Restore(); } catch { }
    }
}
