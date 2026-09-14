using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Dissonance.Audio.Capture;
using ModSettingsMenu.Api;
using UnityEngine;

namespace BigDJ;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
[BepInDependency(ModSettingsMenu.PluginInfo.PLUGIN_GUID, ">=1.1.0")]
public class Plugin : BasePlugin
{
    public const string PLUGIN_GUID = "walker.bigdj";
    public const string PLUGIN_NAME = "BigDJ";
    public const string PLUGIN_VERSION = "1.0.0";

    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;

        var enabled = Config.Bind("General", "Enabled", true,
            new ConfigDescription("Master switch for BigDJ. Turn this off to fully disable the mod and return to normal voice chat.",
                null, ModSettingsTags.Section("General", order: 10), ModSettingsTags.Entry(order: 10)));
        var musicMode = Config.Bind("Music", "MusicMode", false,
            new ConfigDescription("When on, your voice transmits continuously and speech cleanup is turned down, so music plays as a steady stream instead of cutting in and out. Turn off to go back to normal voice chat.",
                null, ModSettingsTags.Section("Music", order: 20), ModSettingsTags.Entry(order: 10)));
        var denoise = Config.Bind("Music", "Denoise", NoiseSuppressionLevels.Disabled,
            new ConfigDescription("How much background hiss to remove while music mode is on. Keep it on Disabled for music — higher settings can dull the sound.",
                null,
                ModSettingsTags.Entry(order: 20)));
        var bgRemoval = Config.Bind("Music", "BackgroundRemoval", false,
            new ConfigDescription("Removes background sounds while music mode is on. Keep this off for music, or the music itself may fade in and out.",
                null, ModSettingsTags.Entry(order: 30)));
        var bgAmount = Config.Bind("Music", "BackgroundRemovalAmount", 0f,
            new ConfigDescription("How strongly background sounds are removed. Only does anything if Background Removal (above) is on.",
                new AcceptableValueRange<float>(0f, 1f),
                ModSettingsTags.Entry(order: 40, sliderStep: 0.05d)));
        var diagnostics = Config.Bind("Music", "Diagnostics", false,
            new ConfigDescription("Writes a status line to the game log once per second while music mode is on, so you can confirm it stays in always-transmit mode.",
                null, ModSettingsTags.Entry(order: 50)));
        var toggleKey = Config.Bind("Input", "ToggleKey", KeyCode.F9,
            new ConfigDescription("Keyboard shortcut that turns Music Mode on and off. (It does not change the master Enabled switch.)",
                null, ModSettingsTags.Section("Input", order: 30), ModSettingsTags.Entry(order: 10)));

        ModSettingsRegistry.Register(PLUGIN_GUID, new ModSettingsModOptions
        {
            Name = "Big DJ",
            Description = "Music mode for voice chat: steady transmission with speech cleanup turned down.",
            Version = PLUGIN_VERSION
        });

        Dj.Bind(enabled, musicMode, denoise, bgRemoval, bgAmount, diagnostics, toggleKey);
        AddComponent<Dj>(); // BasePlugin.AddComponent also injects the type

        Log.LogInfo($"BigDJ v{PLUGIN_VERSION} loaded.");
    }
}
