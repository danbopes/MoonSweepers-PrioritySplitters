using System;
using System.Collections.Generic;
using HarmonyLib;
using MoonCleaners;

namespace PrioritySplitters;

// Vanilla routing (MoonWorld.EnterMachine, inlined by IL2CPP into HandOff) builds the list of connected
// outputs in port order, starts at machineTurns[machine.id] and tries each output in turn, skipping ones
// that are full or blocked, then stores the next index back. Resetting the cursor to 0 before every
// handoff turns round-robin into "first output, overflow to the rest" without touching the routing itself.
// It has to happen per handoff, not per frame: the cursor advances after each capsule.
[HarmonyPatch(typeof(MoonWorld), "HandOff")]
internal static class HandOffPatch
{
    private static readonly Dictionary<int, bool> splitterKinds = new();
    private static readonly List<int> splitterIds = new();
    private static MoonWorld cachedWorld;
    private static int cachedDepotCount = -1;
    private static bool faulted;

    private static void Prefix(MoonWorld __instance)
    {
        if (faulted || !Plugin.Enabled.Value || Plugin.Mode.Value != SplitterMode.FirstOutput)
            return;

        try
        {
            RefreshSplitters(__instance);

            // The interop Dictionary has no indexer setter; a missing key reads back as cursor 0.
            var turns = __instance.machineTurns;
            foreach (var id in splitterIds)
                turns.Remove(id);
        }
        catch (Exception e)
        {
            // A game update that renames these members must not break belts: log once and step aside.
            faulted = true;
            Plugin.Logger.LogError($"Disabled after an error, splitters are back to vanilla: {e}");
        }
    }

    private static void RefreshSplitters(MoonWorld world)
    {
        var depots = world.depots;
        if (ReferenceEquals(world, cachedWorld) && depots.Count == cachedDepotCount)
            return;

        splitterIds.Clear();
        foreach (var entry in depots)
        {
            if (IsSplitter(world, entry.Value.kind))
                splitterIds.Add(entry.Key);
        }

        if (!ReferenceEquals(world, cachedWorld))
            splitterKinds.Clear();
        cachedWorld = world;
        cachedDepotCount = depots.Count;
        Plugin.Logger.LogDebug($"Tracking {splitterIds.Count} splitter(s) out of {depots.Count} structures");
    }

    private static bool IsSplitter(MoonWorld world, int kind)
    {
        if (splitterKinds.TryGetValue(kind, out var known))
            return known;

        // Processors take a separate branch in HandOff and never read the routing cursor.
        if (kind == MoonWorld.PressureChamberKind || kind == MoonWorld.StonePressKind)
        {
            splitterKinds[kind] = false;
            return false;
        }

        var inputs = 0;
        var outputs = 0;
        var def = world.BuildDefinition(kind);
        var ports = def?.conveyorPorts;
        if (ports is not null)
        {
            for (var i = 0; i < ports.Length; i++)
            {
                if (ports[i].output)
                    outputs++;
                else
                    inputs++;
            }
        }

        var splitter = inputs == 1 && outputs >= 2;
        splitterKinds[kind] = splitter;
        if (splitter)
            Plugin.Logger.LogInfo($"Kind {kind} ({def.displayName}) is a splitter: {inputs} in, {outputs} out");
        return splitter;
    }
}
