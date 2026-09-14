# 🎧 BigDJ

**Music mode for Big Walk voice chat.** Play music through proximity voice as a steady,
unfiltered stream — no more volume pumping while the game treats your song like talking.

Big Walk's voice chat is tuned for speech: it only transmits when it hears a voice, and it
scrubs the audio with noise suppression. Great for talking, terrible for music. BigDJ flips
both behaviors with one hotkey and restores everything when you toggle back off.

---

## ✨ What it does

| | Normal voice | Music mode (F9) |
|---|---|---|
| Transmission | Only while speech is detected (volume pumps with the music) | **Always transmitting** — a constant stream |
| Noise cleanup | Denoise + background removal on (eats cymbals, tails, quiet parts) | **Turned down** (configurable, defaults to untouched) |
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

## ⚙️ Settings

**General**
| Setting | Default | What it does |
|---|---|---|
| Enabled | on | Master switch. Off = mod fully idle, normal voice chat. |

**Music**
| Setting | Default | What it does |
|---|---|---|
| MusicMode | off | The effect itself: continuous transmit + relaxed speech cleanup. |
| Denoise | Disabled | Background-hiss removal during music mode. Keep Disabled for music — higher settings dull the sound. |
| BackgroundRemoval | off | Background-sound removal during music mode. Keep off, or the music itself fades in and out. |
| BackgroundRemovalAmount | 0 | How strongly background sounds are removed. Only matters if the above is on. |
| Diagnostics | off | Logs a status line once per second while music mode is on, so you can confirm it stays in always-transmit. |

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
  local broadcast triggers (room + proximity) — last writer wins over the game's own voice logic,
  so transmission never gates.
- `VoiceSettings` cleanup (`DenoiseAmount`, `BackgroundSoundRemovalEnabled/Amount`) is set to
  your menu values, with the game's originals snapshotted first.
- Toggling off (or unloading) writes back the stashed trigger mode and DSP values — the voice
  pipeline is left exactly as found.

## 📥 More mods

Find my other mods on [Nexus Mods](https://www.nexusmods.com/profile/xJonder).

## 📄 License

MIT — do whatever you want, credit appreciated but not required.
