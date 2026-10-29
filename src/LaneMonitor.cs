using System.Collections.Generic;
using MoonCleaners;

namespace PrioritySplitters;

// Tracks, per belt or route, how long it has had a capsule standing still. A short grace period filters out the
// frame or two a capsule pauses at a busy entrance. Once a stop outlasts it the lane counts as backed up, and
// stays so for a release period after the last confirmed stop, which carries an inching lane through the
// moments it creeps forward.
internal static class LaneMonitor
{
    private sealed class State
    {
        public float StoppedSince = -1f;
        public float LastConfirmed = float.NegativeInfinity;
        public int SeenFrame = -1;
    }

    private static readonly Dictionary<int, State> states = new();
    private static int sampledFrame = -1;

    public static void Reset()
    {
        states.Clear();
        sampledFrame = -1;
    }

    public static void Sample(MoonWorld world, float now, int frame)
    {
        if (frame == sampledFrame)
            return;
        sampledFrame = frame;
        RiderTable.Scan(world);

        var grace = Plugin.GraceSeconds.Value;
        foreach (var kv in RiderTable.Lanes)
        {
            if (kv.Value.Stopped == 0)
                continue;
            if (!states.TryGetValue(kv.Key, out var s))
                states[kv.Key] = s = new State();
            if (s.StoppedSince < 0f)
                s.StoppedSince = now;
            if (now - s.StoppedSince >= grace)
                s.LastConfirmed = now;
            s.SeenFrame = frame;
        }
        foreach (var s in states.Values)
        {
            if (s.SeenFrame != frame)
                s.StoppedSince = -1f;
        }
    }

    public static bool Pressured(int lane, float now) =>
        states.TryGetValue(lane, out var s) && now - s.LastConfirmed < Plugin.ReleaseSeconds.Value;

    public static float StoppedFor(int lane, float now) =>
        states.TryGetValue(lane, out var s) && s.StoppedSince >= 0f ? now - s.StoppedSince : 0f;
}
