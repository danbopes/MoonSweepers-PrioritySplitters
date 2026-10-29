using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using MoonCleaners;

namespace PrioritySplitters;

// Per-machine priority, kept beside the BepInEx config in one JSON file per save slot. A splitter or merger
// with no entry alternates as in vanilla. The value is the priority port index, or -1 for round-robin.
internal static class Settings
{
    public const int RoundRobin = -1;

    private static readonly Dictionary<int, int> priorities = new();
    private static int loadedSlot = int.MinValue;

    private static string Folder => Path.Combine(Paths.ConfigPath, "PrioritySplitters");
    private static string FileFor(int slot) => Path.Combine(Folder, $"slot-{slot}.json");

    public static bool TryGet(int machine, out int priorityPort)
    {
        Load();
        return priorities.TryGetValue(machine, out priorityPort);
    }

    public static void Set(int machine, int priorityPort)
    {
        Load();
        priorities[machine] = priorityPort;
        Save();
    }

    public static void Forget()
    {
        priorities.Clear();
        loadedSlot = int.MinValue;
    }

    private static void Load()
    {
        var slot = MoonSaveSlots.Active;
        if (slot == loadedSlot)
            return;
        loadedSlot = slot;
        priorities.Clear();
        try
        {
            var file = FileFor(slot);
            if (!File.Exists(file))
                return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(file));
            if (stored is null)
                return;
            foreach (var kv in stored)
            {
                if (int.TryParse(kv.Key, out var id))
                    priorities[id] = kv.Value;
            }
            Plugin.Logger.LogInfo($"Loaded {priorities.Count} priority setting(s) for save slot {slot}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not read priority settings for save slot {slot}: {e.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var stored = new Dictionary<string, int>();
            foreach (var kv in priorities)
                stored[kv.Key.ToString()] = kv.Value;
            File.WriteAllText(FileFor(loadedSlot), JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Could not write priority settings for save slot {loadedSlot}: {e.Message}");
        }
    }
}
