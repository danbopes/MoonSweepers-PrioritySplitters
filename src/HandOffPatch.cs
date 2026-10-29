using System;
using System.Collections.Generic;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace PrioritySplitters;

// Vanilla routing (MoonWorld.EnterMachine, inlined by IL2CPP into HandOff) takes the connected output at
// machineTurns[machine.id]. It refuses the handoff when that route is full or the shared entrance is busy, and
// otherwise enters it: splitters alternate strictly and never overflow. The prefix picks the output instead and
// points the cursor at it, or refuses the handoff itself when no output should take the capsule yet, since
// vanilla would happily top up a route that is about to stop.
//
// For a merger the same code first makes the input recorded in lastInput[machine.id], the one that fed the
// machine last, give way when another input has a capsule waiting at the end of its belt. The prefix clears
// that record for the priority input so it never gives way, and refuses the other inputs itself while the
// priority input has a capsule waiting.
[HarmonyPatch(typeof(MoonWorld), "HandOff")]
internal static class HandOffPatch
{
    internal static bool Faulted { get; private set; }

    private static Machine handingTo;
    private static int chosen;
    private static string states;

    // A game update that renames these members must not break belts: log once and step aside.
    internal static void Fault(Exception e)
    {
        Faulted = true;
        Plugin.Logger.LogError($"Disabled after an error, splitters and mergers are back to vanilla: {e}");
    }

    private static bool Prefix(MoonWorld __instance, MoonConveyorPath path, ref bool __result)
    {
        handingTo = null;
        if (Faulted || path is null || !Plugin.Enabled.Value)
            return true;

        try
        {
            MachineLayout.Note(__instance);
            // Resolve the target the way HandOff does, so machines fed straight from another machine count too.
            if (!__instance.MachineInputAt(path.end, out var depot, out var input)
                || depot is null
                || !MachineLayout.TryGet(depot.id, out var machine)
                || machine.PriorityPort == Settings.RoundRobin)
                return true;

            return machine switch
            {
                Splitter splitter => RouteSplitter(__instance, splitter, input, ref __result),
                Merger merger => RouteMerger(__instance, merger, input, ref __result),
                _ => true,
            };
        }
        catch (Exception e)
        {
            Fault(e);
            return true;
        }
    }

    private static bool RouteSplitter(MoonWorld world, Splitter splitter, int input, ref bool result)
    {
        if (splitter.Outputs.Count < 2)
            return true;

        var now = Time.time;
        if (!StepConveyorsPatch.Applied)
            LaneMonitor.Sample(world, now, Time.frameCount);

        var debug = Plugin.DebugRouting.Value ? new List<string>() : null;
        chosen = splitter.Choose(world, input, now, debug);
        states = debug is null ? null : string.Join(" | ", debug);
        handingTo = splitter;
        if (chosen < 0)
        {
            result = false;
            return false;
        }

        // The interop Dictionary has no indexer setter; a missing key reads back as cursor 0.
        var turns = world.machineTurns;
        turns.Remove(splitter.Id);
        if (chosen > 0)
            turns.Add(splitter.Id, chosen);
        return true;
    }

    private static bool RouteMerger(MoonWorld world, Merger merger, int input, ref bool result)
    {
        if (merger.Inputs.Count < 2)
            return true;

        handingTo = merger;
        chosen = input;
        if (input == merger.PriorityPort)
        {
            // Vanilla only makes the input it remembers as the last one give way, so forgetting it is enough.
            world.lastInput.Remove(merger.Id);
            states = "priority input";
            return true;
        }
        if (merger.PriorityWaiting(world))
        {
            states = $"in{input} waits for the priority input";
            result = false;
            return false;
        }
        // Vanilla's own turn-taking still applies among the other inputs.
        states = $"in{input} enters, priority input idle";
        return true;
    }

    private static void Postfix(bool __result)
    {
        if (handingTo is not null && Plugin.DebugRouting.Value)
            Diagnostics.Record(handingTo.Id, chosen, __result, states);
        handingTo = null;
    }
}
