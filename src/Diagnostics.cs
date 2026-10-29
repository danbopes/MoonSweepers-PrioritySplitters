using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace PrioritySplitters;

// Only active with DebugRouting on: a 5 second summary of where each splitter sent capsules and which input each
// merger took, the state at the last refused handoff, and a snapshot of every tracked splitter's lanes.
internal static class Diagnostics
{
    private const float ReportSeconds = 5f;

    private static readonly SortedDictionary<int, (int[] entered, int refused)> byMachine = new();
    private static readonly SortedDictionary<int, string> lastRefusal = new();
    private static float nextReport;

    // For a splitter, port is the index of the chosen output; for a merger, the input port the capsule came from.
    public static void Record(int machine, int port, bool entered, string stateText)
    {
        if (!byMachine.TryGetValue(machine, out var stats))
            stats = (new int[4], 0);
        if (!entered)
        {
            stats.refused++;
            lastRefusal[machine] = $"chose {(port < 0 ? "none" : "p" + port)}; {stateText}";
        }
        else if (port >= 0 && port < stats.entered.Length)
            stats.entered[port]++;
        byMachine[machine] = stats;

        var now = Time.realtimeSinceStartup;
        if (now < nextReport)
            return;
        nextReport = now + ReportSeconds;

        var parts = new List<string>();
        foreach (var kv in byMachine)
            parts.Add($"#{kv.Key} p0={kv.Value.entered[0]} p1={kv.Value.entered[1]} p2={kv.Value.entered[2]} waited={kv.Value.refused}");
        Plugin.Logger.LogInfo($"[routing {ReportSeconds}s] {string.Join(" | ", parts)}");
        foreach (var kv in lastRefusal)
            Plugin.Logger.LogInfo($"[refused] #{kv.Key} {kv.Value}");
        byMachine.Clear();
        lastRefusal.Clear();

        var world = MachineLayout.World;
        if (world is null)
            return;
        var time = Time.time;
        foreach (var tracked in MachineLayout.Splitters)
        {
            var lanes = new List<string>();
            foreach (var lane in tracked.Outputs)
                lanes.Add(lane.Describe(world, lane.Route, time));
            Plugin.Logger.LogInfo($"[lanes] #{tracked.Id} {string.Join(" | ", lanes)}");
        }
    }

    // The raw rider table against the queues vanilla keeps per belt. Enter records a capsule in the rider table at
    // once but its queue only picks it up when that belt is next stepped, so the table may lead a queue by one;
    // a table that trails a queue, or leads by more, means the raw read is wrong.
    public static void CheckRiderTable(MoonWorld world, IEnumerable<Splitter> splitters)
    {
        var routes = 0;
        var leading = 0;
        var wrong = new List<string>();
        foreach (var splitter in splitters)
        {
            foreach (var output in splitter.Outputs)
            {
                routes++;
                var queued = world.beltQueues.TryGetValue(output.Route, out var queue) && queue is not null ? queue.Count : 0;
                var scanned = RiderTable.Of(output.Route).Count;
                if (scanned == queued + 1)
                    leading++;
                else if (scanned != queued)
                    wrong.Add($"route {output.Route}: queue {queued}, table {scanned}");
            }
        }
        if (wrong.Count == 0)
            Plugin.Logger.LogInfo($"Rider table check: {routes} routes agree with the belt queues ({leading} gained a capsule this frame)");
        else
            Plugin.Logger.LogWarning($"Rider table check: {wrong.Count} of {routes} routes disagree with the belt queues: {string.Join("; ", wrong)}");
    }

    public static void LogTopology(MoonWorld world, IEnumerable<Splitter> splitters)
    {
        foreach (var splitter in splitters)
        {
            if (!world.depots.TryGetValue(splitter.Id, out var depot))
                continue;
            var ports = world.BuildDefinition(depot.kind).conveyorPorts;
            world.StructurePose(depot, out var position, out var rotation);
            foreach (var output in splitter.Outputs)
                Plugin.Logger.LogInfo($"[topology] #{splitter.Id}@({position.x:0},{position.z:0}) p{output.Port} route {output.Route} -> {Downstream(world, position + rotation * ports[output.Port].position)}");
        }
    }

    private static string Downstream(MoonWorld world, Vector3 point)
    {
        var hops = new List<string>();
        for (var guard = 0; guard < 32; guard++)
        {
            var belt = world.BeltStartingAt(point);
            if (belt is not null)
            {
                hops.Add($"belt {belt.id}");
                point = belt.end;
                continue;
            }
            if (world.MachineInputAt(point, out var depot, out var input) && depot is not null)
                hops.Add($"#{depot.id} {world.BuildDefinition(depot.kind)?.displayName} in{input}");
            else
                hops.Add("nothing");
            break;
        }
        return string.Join(" -> ", hops);
    }
}
