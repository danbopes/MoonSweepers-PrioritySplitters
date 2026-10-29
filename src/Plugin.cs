using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace PrioritySplitters;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "danbopes.moonsweepers.prioritysplitters";
    public const string Name = "Priority Splitters";
    public const string Version = "1.0.0";

    internal static ManualLogSource Logger { get; private set; }
    internal static ConfigEntry<bool> Enabled { get; private set; }
    internal static ConfigEntry<string> ToggleKey { get; private set; }
    internal static ConfigEntry<float> GraceSeconds { get; private set; }
    internal static ConfigEntry<float> ReleaseSeconds { get; private set; }
    internal static ConfigEntry<float> ReachMeters { get; private set; }
    internal static ConfigEntry<bool> ShowPrompt { get; private set; }
    internal static ConfigEntry<float> LeanDegrees { get; private set; }
    internal static ConfigEntry<bool> DebugRouting { get; private set; }

    public override void Load()
    {
        Logger = Log;

        Enabled = Config.Bind("General", "Enabled", true,
            "Set to false to restore vanilla round-robin splitters and mergers without uninstalling. Machines you have not toggled always alternate as in vanilla.");
        ToggleKey = Config.Bind("General", "ToggleKey", "p",
            "Key that cycles the splitter or merger you are looking at between left first, right first and round-robin. Saved per save slot.");
        GraceSeconds = Config.Bind("Tuning", "GraceSeconds", 0.2f,
            "How long a capsule has to stand still before its lane counts as backed up. The frame or two a capsule pauses at a busy entrance should stay under this.");
        ReleaseSeconds = Config.Bind("Tuning", "ReleaseSeconds", 1.0f,
            "How long a lane has to keep moving after its last stop before it counts as flowing again. Keeps an inching lane backed up while it creeps forward.");
        ReachMeters = Config.Bind("General", "ReachMeters", 6f,
            "How close you have to be to a splitter or merger to toggle it and to see its key hint.");
        ShowPrompt = Config.Bind("Visuals", "ShowPrompt", true,
            "Show the game's key prompt with the toggle key and the current priority while you look at a splitter or merger.");
        LeanDegrees = Config.Bind("Visuals", "LeanDegrees", 20f,
            "How far the S cone on a splitter leans toward its priority output and the M cone on a merger toward its priority input, in degrees. Round-robin machines stand upright. 0 turns the indicator off.");
        DebugRouting = Config.Bind("Debug", "DebugRouting", false,
            "Log a summary every 5 seconds of which output each splitter used, which input each merger took, the state of every lane, and which handoffs were refused.");

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(HandOffPatch));
        try
        {
            harmony.PatchAll(typeof(StepConveyorsPatch));
            StepConveyorsPatch.Applied = true;
        }
        catch (Exception e)
        {
            // Without the per-frame sample, lanes are read when a capsule reaches a splitter instead.
            Logger.LogWarning($"Could not patch StepConveyors, sampling lanes from HandOff instead: {e.Message}");
        }
        try
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<PriorityBehaviour>())
                ClassInjector.RegisterTypeInIl2Cpp<PriorityBehaviour>();
            AddComponent<PriorityBehaviour>();
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Could not add the hotkey and cone behaviour, machines keep their saved priorities: {e.Message}");
        }
        Logger.LogInfo($"{Name} {Version} loaded (toggle key: {ToggleKey.Value})");
    }
}
