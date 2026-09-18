<p align="center">
  <img src="https://repository-images.githubusercontent.com/1370149594/1cc97433-95cb-4339-bf84-609ab0a89f6c" alt="BigDJ banner">
</p>

# 🎧 BigDJ

[![License](https://img.shields.io/github/license/Trifocals7733/BigDJ?style=flat-square)](https://github.com/Trifocals7733/BigDJ/blob/main/LICENSE)
[![Latest release](https://img.shields.io/github/v/release/Trifocals7733/BigDJ?style=flat-square&label=latest%20release)](https://github.com/Trifocals7733/BigDJ/releases/latest)
[![Supported game](https://img.shields.io/badge/Big%20Walk%20%7C%20BepInEx%206-supported-6f42c1?style=flat-square)](https://store.steampowered.com/app/1478500/Big_Walk/)

**Music mode for Big Walk voice chat.** Play music through proximity voice as a steady,
unfiltered stream — no more volume pumping while the game treats your song like talking.

Big Walk's voice chat is tuned for speech: it only transmits when it hears a voice, and it
scrubs the audio with noise suppression. Great for talking, terrible for music. BigDJ flips
both behaviors with one hotkey and restores everything when you toggle back off.

## ✨ Features

- **Island Radio 2D Broadcast Mode**: Switch between traditional 3D proximity and global 2D radio broadcast via native `CmdSet2DVoice` (no distance falloff, no directional muffling).
- **Continuous Transmission**: Keeps voice transmission open (`CommActivationMode.Open`) while Music Mode is active.
- **Dissonance High Priority**: Elevates packet priority to `ChannelPriority.High` so your music stream never ducks or drops packets during multi-speaker talk.
- **Dry Acoustics**: Suppresses cave reverb and island echo for clean, studio-grade sound.
- **Real-Time Graphical VU Meter**: Dynamic audio level bar and dB readout on the in-game HUD overlay.
- **Speech Cleanup Bypass**: Relaxes speech-focused denoise and background removal that damage instruments and harmonics.
- **Zero-Allocation Core**: Uses native `WorldManager` and local hierarchy scoping for zero GC stutter.
- **Leak-Proof Restoration**: Snapshots and fully restores all original voice, trigger, priority, and acoustic states on toggle off.

---

## ✨ What it does

| | Normal voice | Music mode (F9) |
|---|---|---|
| Transmission | Only while speech is detected (volume pumps with the music) | **Always transmitting** — a constant stream |
| Audio Mode | 3D proximity only (muffled by distance & head-turning) | **Proximity 3D** or **Island Radio 2D** (direct unattenuated broadcast) |
| Priority | Default priority (ducks when multiple people talk) | **High Priority** — stream stays solid over conversation |
| Noise cleanup | Denoise + background removal on (eats cymbals, tails, quiet parts) | **Turned down** (configurable, defaults to untouched) |
| Acoustics | Cavernous reverb and echo applied in terrain | **Dry Acoustics** — suppresses environmental reverberation |
| Monitoring | None | **Live VU Meter** with real-time dB readout & peak alerts |
| Your settings | — | Snapshotted and **restored** when you toggle off |

## 📦 Requirements

- **Big Walk** ([Steam](https://store.steampowered.com/app/1478500/Big_Walk/)) with **[BepInEx 6 (IL2CPP)](https://builds.bepinex.dev/projects/bepinex_be)** installed
- [**ModSettingsMenu**](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/) ≥ 1.1.0 (hard dependency — the settings UI lives there)

## 🚀 Install

1. Close the game. (The game locks plugin DLLs while running.)
2. Drop the built `BigDJ.dll` into `BepInEx/plugins/BigDJ/`.
3. Launch the game, open **Mod Settings** (main or pause menu) → **Big DJ**.
4. Get your music into your mic — a virtual audio cable, Voicemeeter output, or speakers — then press **F9**.

> 🎵 One thing the mod can't do for you: it keeps the stream steady and unfiltered, but the
> *music itself* still has to reach your microphone somehow. A virtual cable is the clean way.
> Programs such as [Logitech Mixline](https://www.logitechg.com/software/mixline) or
> [VB-Audio](https://vb-audio.com/) are recommended for routing music into the microphone input.

## 🩺 Troubleshooting

### BigDJ does not appear in Mod Settings

- Confirm that the game is using **BepInEx 6 (IL2CPP)**, not a different BepInEx build.
- Confirm `BigDJ.dll` is in `BepInEx/plugins/BigDJ/`.
- Confirm [ModSettingsMenu](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/) ≥ 1.1.0 is installed in `BepInEx/plugins/ModSettingsMenu/`.
- Check the BepInEx log for a missing dependency or load error.

### Music still cuts in and out

- Make sure both **Enabled** and **MusicMode** are on.
- Press the configured toggle key (F9 by default) after entering the game.
- Keep **DspPreset** on **Untouched** (or **Denoise** Disabled and **BackgroundRemoval** off with a Custom preset).
- Turn on **Diagnostics** to verify that Music Mode remains active.

### Music is not audible to other players

- BigDJ changes the voice-processing behavior; it does not route audio into the microphone.
- Send your music to the input selected by Big Walk using a virtual audio cable, Voicemeeter, or speakers.
- Check the in-game overlay (`Overlay = on`): the live VU meter will display your input signal in dB. If it says `(SILENT)`, check your Windows microphone routing.

### The build fails with MSB3027 or a locked DLL

- Close Big Walk before running `dotnet build`.
- If the game is already closed, check for another process still holding `BigDJ.dll`, then build again.

## ⚙️ Settings

**General**
| Setting | Default | What it does |
|---|---|---|
| Enabled | on | Master switch. Off = mod fully idle, normal voice chat. |

**Music**
| Setting | Default | What it does |
|---|---|---|
| MusicMode | off | Continuous transmit + relaxed speech cleanup. Turn on to start DJing. |
| BroadcastMode | Proximity3D | Proximity3D = local 3D positional audio. IslandRadio2D = direct 2D broadcast to all players via native 2D voice. |
| DspPreset | Untouched | One-switch cleanup combo: Untouched (nothing), Light Cleanup (mild background removal), or Custom (the three knobs below). |
| HighPriority | on | Elevates Dissonance packet priority to High so music never ducks during conversation. |
| DryAcoustics | on | Suppresses cave reverb and island echo for clean studio sound. |
| Denoise | Disabled | Background-hiss removal during music mode. Keep Disabled for music — higher settings dull the sound. |
| BackgroundRemoval | off | Background-sound removal during music mode. Keep off, or the music itself fades in and out. |
| BackgroundRemovalAmount | 0 | How strongly background sounds are removed. Only matters if the above is on. |
| Diagnostics | off | Logs a status line once per second while music mode is on. |
| Overlay | off | On-screen HUD with live VU meter readout: audio level in dB, transmit state, and DSP values. |

**Input**
| Setting | Default | What it does |
|---|---|---|
| ToggleKey | F9 | Flips Music Mode on/off. (Does not change the master Enabled switch.) |

## 🔨 Build from source

```bash
dotnet build
```

Requirements: .NET 6 SDK. All game/BepInEx references are pinned to your local install — **edit
`GameDir` at the top of `BigDJ.csproj`** if your Steam library lives elsewhere. The build copies
`BigDJ.dll` straight into `BepInEx/plugins/BigDJ/`. Close the game first or the copy fails with
MSB3027 (locked DLL).

## 🧠 How it works

- Every `LateUpdate` while music mode is on, the mod writes `CommActivationMode.Open` into your
  local broadcast triggers (room + proximity) — last writer wins over the game's own voice logic.
- When `IslandRadio2D` is selected, the mod sets `Networkis2DVoice = true` (on host) and calls
  `CmdSet2DVoice(true)` (on client). Mirror syncs this to all players, switching their voice playback
  controls to `TwoDMode = true` and eliminating 3D spatial drop-off.
- `HighPriority` sets `DissonanceComms.PlayerPriority = ChannelPriority.High`, keeping music packets
  at the front of the queue.
- `DryAcoustics` clamps `NetworkechoAmount = 0` and disables `SelfEcho.EchoOn` so cave reverbs don't muddy the sound.
- `Overlay` samples `LocalVoiceProvider.CachedVoiceData` at 20 Hz to calculate real-time RMS amplitude
  and peak values for the live VU meter.
- Toggling off (or unloading) writes back all stashed states — trigger mode, DSP, 2D voice, priority,
  and acoustics — leaving the voice pipeline exactly as found.

## 📥 More mods

Find my other mods on [Nexus Mods](https://www.nexusmods.com/profile/xJonder).

You can also find BigDJ on [Thunderstore](https://thunderstore.io/c/big-walk/p/Trifocals/BigDJ/) and [Nexus Mods](https://www.nexusmods.com/bigwalk/mods/46).

## 🙏 Special thanks

Special thanks to the legend **Arthurian**. His hard work hosting lobbies inspired this mod.

## 📄 License

MIT — do whatever you want, credit appreciated but not required.
