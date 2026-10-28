using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace PrioritySplitters;

public enum SplitterMode
{
    RoundRobin,
    FirstOutput,
}

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "danbopes.moonsweepers.prioritysplitters";
    public const string Name = "Priority Splitters";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger { get; private set; }
    internal static ConfigEntry<bool> Enabled { get; private set; }
    internal static ConfigEntry<SplitterMode> Mode { get; private set; }

    public override void Load()
    {
        Logger = Log;

        Enabled = Config.Bind("General", "Enabled", true,
            "Set to false to restore vanilla round-robin splitters without uninstalling.");
        Mode = Config.Bind("General", "Mode", SplitterMode.FirstOutput,
            "FirstOutput: every splitter fills its first connected output and only overflows to the next one when it is full or blocked.\n" +
            "RoundRobin: vanilla behaviour.");

        new Harmony(Guid).PatchAll(typeof(HandOffPatch));
        Logger.LogInfo($"{Name} {Version} loaded (mode: {Mode.Value})");
    }
}
