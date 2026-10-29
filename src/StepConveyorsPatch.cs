using System;
using HarmonyLib;
using MoonCleaners;
using UnityEngine;

namespace PrioritySplitters;

// StepConveyors moves every capsule once per frame, so its end is the one place to sample who is standing still.
[HarmonyPatch(typeof(MoonWorld), "StepConveyors")]
internal static class StepConveyorsPatch
{
    internal static bool Applied;
    private static bool checkedTable;

    private static void Postfix(MoonWorld __instance)
    {
        if (HandOffPatch.Faulted || !Plugin.Enabled.Value)
            return;
        try
        {
            LaneMonitor.Sample(__instance, Time.time, Time.frameCount);
            if (!checkedTable && MachineLayout.Count > 0)
            {
                checkedTable = true;
                Diagnostics.CheckRiderTable(__instance, MachineLayout.Splitters);
            }
        }
        catch (Exception e)
        {
            HandOffPatch.Fault(e);
        }
    }
}
