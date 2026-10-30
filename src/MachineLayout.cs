using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;

namespace PrioritySplitters;

// A conveyor port that can hold the priority: an output of a splitter or an input of a merger.
internal class MachinePort
{
    public int Port;
    // Which side of the flow this port sits on, as the player sees it looking downstream: "left", "right", or
    // "port N" when the geometry does not say.
    public string Side;
    // How far the port sits to the player's right of the flow line, in the machine's frame; negative is left.
    public float Lateral;
}

internal sealed class Output : MachinePort
{
    public int Route;
    // A belt or machine input sits at the port. Only connected outputs take part in routing; every output can
    // hold the priority, so it can be set before the belts are built.
    public bool Connected;

    private int cap = -1;
    private bool downstreamResolved;
    private int belt;
    private int nextMachine;
    private int nextInput;

    public int Belt => belt;
    public int NextMachine => nextMachine;

    // The most capsules the route holds when stopped without one standing in the shared entrance. This is the
    // count at which vanilla's RouteFull turns true, computed the same way so the two never disagree.
    public int Cap(MoonWorld world)
    {
        if (cap < 0)
        {
            var path = world.MachinePath(Route);
            if (path is null)
                return int.MaxValue;
            var length = path.Length;
            var n = 0;
            while (n < 64 && !(MoonWorld.BeltSpacing > length - n * MoonWorld.BeltSpacing))
                n++;
            cap = n;
        }
        return cap;
    }

    // Backed up ahead of the route: the belt it feeds has a confirmed stop, or the splitter it feeds directly
    // is about to refuse. Either one means capsules sent now may come to rest on the route.
    public bool DownstreamPressured(MoonWorld world, float now, int depth)
    {
        ResolveDownstream(world);
        if (belt != 0)
            return LaneMonitor.Pressured(belt, now);
        if (nextMachine != 0 && depth < 2 && MachineLayout.PeekSplitter(nextMachine, out var splitter))
            return splitter.NearlyRefusing(world, nextInput, now, depth + 1);
        return false;
    }

    public string Describe(MoonWorld world, int route, float now)
    {
        var lane = RiderTable.Of(route);
        var text = $"p{Port} {lane.Count}/{Cap(world)}";
        if (lane.Stopped > 0)
            text += $" st{lane.Stopped}";
        if (world.RouteFull(route))
            text += " FULL";
        if (LaneMonitor.Pressured(route, now))
            text += " P";
        ResolveDownstream(world);
        if (belt != 0)
        {
            var next = RiderTable.Of(belt);
            text += $" >belt{belt} {next.Count}";
            if (next.Stopped > 0)
                text += $" st{next.Stopped} {LaneMonitor.StoppedFor(belt, now):0.0}s";
            if (LaneMonitor.Pressured(belt, now))
                text += " P";
        }
        else if (nextMachine != 0)
        {
            text += $" >#{nextMachine}";
            if (DownstreamPressured(world, now, 0))
                text += " NR";
        }
        return text;
    }

    private void ResolveDownstream(MoonWorld world)
    {
        if (downstreamResolved)
            return;
        var path = world.MachinePath(Route);
        if (path is null)
            return;
        downstreamResolved = true;
        var next = world.BeltStartingAt(path.end);
        if (next is not null)
        {
            belt = next.id;
            return;
        }
        if (world.MachineInputAt(path.end, out var depot, out var input) && depot is not null)
        {
            nextMachine = depot.id;
            nextInput = input;
        }
    }
}

internal sealed class Input : MachinePort
{
    // Where the input sits in the world, the point vanilla looks a feeding belt up by.
    public Vector3 Point;
    // The lanes that end at this input: the belt, or the output routes of a machine linked straight to it with
    // no belt in between. Capsules waiting on any of them count as waiting at this input.
    public readonly List<int> Lanes = new();
}

internal abstract class Machine
{
    public int Id;
    public Vector3 AimPoint;
    // The machine's frame in the world, so a direction in that frame can be compared with its scene objects.
    public Quaternion Rotation = Quaternion.identity;
    // Direction of flow through the machine, in its own frame.
    public Vector3 Flow = Vector3.forward;
    // The port served first, or Settings.RoundRobin for vanilla alternation.
    public int PriorityPort = Settings.RoundRobin;
    // Ports from the player's left to right, which is also the cycle order for the toggle key.
    public readonly List<MachinePort> LeftToRight = new();

    public abstract string Kind { get; }
    public abstract IEnumerable<MachinePort> Ports { get; }
    // The routes capsules leave this machine on, one per (input, output) pair vanilla can use.
    public abstract IEnumerable<int> OutputRoutes { get; }

    public MachinePort Leftmost => LeftToRight.Count > 0 ? LeftToRight[0] : null;

    public MachinePort Priority
    {
        get
        {
            foreach (var port in Ports)
            {
                if (port.Port == PriorityPort)
                    return port;
            }
            return null;
        }
    }

    public void ApplyPriority(int priorityPort) => PriorityPort = priorityPort;

    // Cycle: left, then each port further right, then round-robin.
    public int NextPriority()
    {
        for (var i = 0; i < LeftToRight.Count; i++)
        {
            if (LeftToRight[i].Port == PriorityPort)
                return i + 1 < LeftToRight.Count ? LeftToRight[i + 1].Port : Settings.RoundRobin;
        }
        return Leftmost?.Port ?? Settings.RoundRobin;
    }

    public string Describe(int priorityPort)
    {
        if (priorityPort == Settings.RoundRobin)
            return "round-robin";
        foreach (var port in Ports)
        {
            if (port.Port == priorityPort)
                return $"{port.Side} first";
        }
        return "round-robin";
    }
}

internal sealed class Splitter : Machine
{
    public int Input;
    // Every output port, in port order.
    public readonly List<Output> AllOutputs = new();
    // Connected outputs in port order: the same list, in the same order, that vanilla indexes machineTurns into.
    public readonly List<Output> Outputs = new();

    public override string Kind => "Splitter";
    public override IEnumerable<MachinePort> Ports => AllOutputs;

    public override IEnumerable<int> OutputRoutes
    {
        get
        {
            foreach (var output in Outputs)
                yield return output.Route;
        }
    }

    // The first output, priority first and the rest in port order, that takes a capsule without leaving it
    // standing in the shared entrance later. A route is skipped when vanilla calls it full, or when it or
    // what it feeds is backed up and the route already holds as many capsules as can stop on it clear of the
    // entrance. Returns the index into Outputs, or -1 when every output is skipped.
    public int Choose(MoonWorld world, int input, float now, List<string> debug)
    {
        var choice = -1;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < Outputs.Count; i++)
            {
                var output = Outputs[i];
                var priority = output.Port == PriorityPort;
                if (priority != (pass == 0))
                    continue;
                var route = RouteFor(input, output);
                var skip = Skip(world, output, route, now, 0, 0);
                debug?.Add(output.Describe(world, route, now) + (priority ? " *" : "") + (skip ? " skip" : ""));
                if (!skip && choice < 0)
                {
                    choice = i;
                    if (debug is null)
                        return choice;
                }
            }
        }
        return choice;
    }

    // True when at most one more capsule fits before every output refuses: what a splitter feeding this one
    // directly, with no belt in between, needs to know a capsule ahead of time.
    public bool NearlyRefusing(MoonWorld world, int input, float now, int depth)
    {
        foreach (var output in Outputs)
        {
            if (!Skip(world, output, RouteFor(input, output), now, depth, 1))
                return false;
        }
        return true;
    }

    private int RouteFor(int input, Output output) =>
        input == Input ? output.Route : MoonWorld.MachineRoute(Id, input, output.Port);

    private static bool Skip(MoonWorld world, Output output, int route, float now, int depth, int headroom)
    {
        if (world.RouteFull(route))
            return true;
        var count = RiderTable.Of(route).Count;
        if (count + headroom < output.Cap(world))
            return false;
        return LaneMonitor.Pressured(route, now) || output.DownstreamPressured(world, now, depth);
    }
}

// Vanilla arbitrates a merger's inputs in HandOff: the input that fed the machine last gives way whenever
// another input has a capsule in the last 0.75 m of its belt (WaitingAtEnd), which alternates them. With a
// priority input, that input never gives way and the others only enter while it has nothing waiting.
internal sealed class Merger : Machine
{
    public int Output;
    // Connected inputs in port order.
    public readonly List<Input> Inputs = new();

    public override string Kind => "Merger";
    public override IEnumerable<MachinePort> Ports => Inputs;

    public override IEnumerable<int> OutputRoutes
    {
        get
        {
            foreach (var input in Inputs)
                yield return MoonWorld.MachineRoute(Id, input.Port, Output);
        }
    }

    public bool PriorityWaiting(MoonWorld world)
    {
        if (Priority is not Input priority)
            return false;
        foreach (var lane in priority.Lanes)
        {
            if (world.WaitingAtEnd(lane))
                return true;
        }
        return false;
    }
}

// Machines are found from build definitions rather than hard-coded kinds, so modded splitters and mergers with
// more ports are covered too. The layout only changes when the player builds, so it is cached and rebuilt when
// the depot or belt count changes, with a slow periodic refresh for edits that keep both counts the same.
internal static class MachineLayout
{
    private const float RefreshSeconds = 3f;
    // Vanilla's BeltEndingAt matches a belt end to a port within this distance.
    private const float PortToleranceSquared = 0.0025f;

    private enum Role { None, Splitter, Merger }

    private static readonly Dictionary<int, Role> roles = new();
    private static readonly Dictionary<int, Machine> machines = new();
    private static MoonWorld cachedWorld;
    private static int cachedDepotCount = -1;
    private static int cachedConveyorCount = -1;
    private static float nextRefresh;
    private static string lastSummary;

    private static MoonWorld liveWorld;
    private static float nextWorldSearch;

    public static MoonWorld World => cachedWorld;
    public static IEnumerable<Machine> All => machines.Values;
    public static int Count => machines.Count;

    // The world instance HandOff is stepping, which is the one to lay out.
    public static void Note(MoonWorld world)
    {
        if (!ReferenceEquals(world, liveWorld))
            liveWorld = world;
    }

    // Forgets the world and everything laid out from it. Leaving for the menu destroys the world with its scene,
    // and loading a save again makes a new one; nothing read from the old one is worth keeping.
    public static void Reset()
    {
        liveWorld = null;
        cachedWorld = null;
        cachedDepotCount = -1;
        cachedConveyorCount = -1;
        nextRefresh = 0f;
        nextWorldSearch = 0f;
        lastSummary = null;
        machines.Clear();
        roles.Clear();
        LaneMonitor.Reset();
        Settings.Forget();
    }

    // Called every frame from the behaviour. The layout is read from the game outside StepConveyors on purpose:
    // while the world is stepping, BeltStartingAt and friends cache every position they are asked about, a
    // missing belt included, so asking about an unconnected port then would leave a stale answer behind for
    // a belt built there later.
    public static void Poll()
    {
        try
        {
            var world = liveWorld;
            // `is null` is the managed reference; `== null` asks Unity whether the object still exists, which a
            // world destroyed by a scene change no longer does.
            if (world is not null && world == null)
            {
                Reset();
                world = null;
            }
            if (world is null)
            {
                world = liveWorld = FindWorld();
                if (world is null)
                    return;
            }
            if (world.stepping)
                return;
            Refresh(world);
        }
        catch (ObjectCollectedException)
        {
            // The interop pools wrappers, and a wrapper whose object the runtime has collected throws on any use.
            Reset();
        }
    }

    // HandOff names the world the moment a capsule enters a machine. Until then, and again after a scene change,
    // the scene is searched for it a couple of times a second once the loading screen is gone, so a quiet
    // factory still gets its prompt and cones.
    private static MoonWorld FindWorld()
    {
        var now = Time.realtimeSinceStartup;
        if (now < nextWorldSearch || MoonSession.Loading || MoonLoadingScreen.Active)
            return null;
        nextWorldSearch = now + 0.5f;
        var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MoonWorld>(), false);
        for (var i = 0; found is not null && i < found.Length; i++)
        {
            var world = found[i].TryCast<MoonWorld>();
            if (world != null)
                return world;
        }
        return null;
    }

    public static IEnumerable<Splitter> Splitters
    {
        get
        {
            foreach (var machine in machines.Values)
            {
                if (machine is Splitter splitter)
                    yield return splitter;
            }
        }
    }

    public static bool TryGet(int depotId, out Machine machine) => machines.TryGetValue(depotId, out machine);

    public static bool PeekSplitter(int depotId, out Splitter splitter)
    {
        splitter = machines.TryGetValue(depotId, out var machine) ? machine as Splitter : null;
        return splitter is not null;
    }

    private static void Refresh(MoonWorld world)
    {
        var depots = world.depots;
        var conveyors = world.conveyors;
        var now = Time.realtimeSinceStartup;
        var worldChanged = !ReferenceEquals(world, cachedWorld);
        if (!worldChanged
            && depots.Count == cachedDepotCount
            && conveyors.Count == cachedConveyorCount
            && now < nextRefresh)
            return;

        if (worldChanged)
        {
            roles.Clear();
            LaneMonitor.Reset();
            Settings.Forget();
        }
        cachedWorld = world;
        cachedDepotCount = depots.Count;
        cachedConveyorCount = conveyors.Count;
        nextRefresh = now + RefreshSeconds;

        machines.Clear();
        var splitters = 0;
        var mergers = 0;
        foreach (var entry in depots)
        {
            var depot = entry.Value;
            var role = RoleOf(world, depot.kind);
            if (role == Role.None)
                continue;
            var def = world.BuildDefinition(depot.kind);
            world.StructurePose(depot, out var position, out var rotation);
            Machine machine = role == Role.Splitter
                ? BuildSplitter(world, depot, def, position, rotation)
                : BuildMerger(world, depot, def, position, rotation);
            machine.Id = depot.id;
            machine.Rotation = rotation;
            if (machine is Splitter)
                splitters++;
            else
                mergers++;
            machines[depot.id] = machine;
        }
        LinkDirectFeeds(world);

        // A machine with no saved choice alternates exactly as in vanilla, new ones included.
        foreach (var machine in machines.Values)
            machine.PriorityPort = Settings.TryGet(machine.Id, out var stored) ? stored : Settings.RoundRobin;

        var parts = new List<string>();
        foreach (var machine in machines.Values)
        {
            var ports = new List<string>();
            foreach (var port in machine.Ports)
            {
                var text = $"p{port.Port}={port.Side}{(port.Port == machine.PriorityPort ? "*" : "")}";
                if (port is Input input)
                    text += $"({string.Join(",", input.Lanes)})";
                else if (port is Output { Connected: false })
                    text += "?";
                ports.Add(text);
            }
            var role = machine is Splitter splitter ? $"in{splitter.Input}" : $"merger out{((Merger)machine).Output}";
            parts.Add($"#{machine.Id} {role} [{string.Join(" ", ports)}]");
        }
        var summary = string.Join("; ", parts);
        if (summary == lastSummary)
            return;
        lastSummary = summary;
        Plugin.Logger.LogInfo($"Tracking {splitters} splitter(s) and {mergers} merger(s): {summary}");
        if (Plugin.DebugRouting.Value)
            Diagnostics.LogTopology(world, Splitters);
    }

    private static Splitter BuildSplitter(MoonWorld world, MoonDepotSnapshot depot, MoonDepotBuildDefinition def, Vector3 position, Quaternion rotation)
    {
        var ports = def.conveyorPorts;
        var splitter = new Splitter { AimPoint = position };
        for (var i = 0; i < ports.Length; i++)
        {
            if (!ports[i].output)
                splitter.Input = i;
        }
        for (var i = 0; i < ports.Length; i++)
        {
            if (!ports[i].output)
                continue;
            var output = new Output
            {
                Port = i,
                Route = MoonWorld.MachineRoute(depot.id, splitter.Input, i),
                Connected = world.ConnectedOutput(depot.id, PortPoint(def, i, position, rotation)),
            };
            splitter.AllOutputs.Add(output);
            if (output.Connected)
                splitter.Outputs.Add(output);
        }
        // Flow runs from the input toward the outputs.
        var centroid = Vector3.zero;
        foreach (var output in splitter.AllOutputs)
            centroid += ports[output.Port].position;
        if (splitter.AllOutputs.Count > 0)
            centroid /= splitter.AllOutputs.Count;
        NameSides(splitter, def, centroid - ports[splitter.Input].position, ports[splitter.Input]);
        if (splitter.Outputs.Count > 0)
        {
            var path = world.MachinePath(splitter.Outputs[0].Route);
            if (path is not null)
                splitter.AimPoint = path.Point(path.Length * 0.5f);
        }
        return splitter;
    }

    private static Merger BuildMerger(MoonWorld world, MoonDepotSnapshot depot, MoonDepotBuildDefinition def, Vector3 position, Quaternion rotation)
    {
        var ports = def.conveyorPorts;
        var merger = new Merger { AimPoint = position };
        for (var i = 0; i < ports.Length; i++)
        {
            if (ports[i].output)
                merger.Output = i;
        }
        for (var i = 0; i < ports.Length; i++)
        {
            if (ports[i].output)
                continue;
            var point = PortPoint(def, i, position, rotation);
            var belt = world.BeltEndingAt(point);
            var input = new Input { Port = i, Point = point };
            if (belt is not null)
                input.Lanes.Add(belt.id);
            // Inputs with no belt may still be fed straight from another machine; LinkDirectFeeds finds those.
            // Inputs nothing feeds stay listed so the priority can be set before the belts are built.
            merger.Inputs.Add(input);
        }
        // Flow runs from the inputs toward the output.
        var centroid = Vector3.zero;
        foreach (var input in merger.Inputs)
            centroid += ports[input.Port].position;
        if (merger.Inputs.Count > 0)
            centroid /= merger.Inputs.Count;
        NameSides(merger, def, ports[merger.Output].position - centroid, ports[merger.Output]);
        if (merger.Inputs.Count > 0)
        {
            var path = world.MachinePath(MoonWorld.MachineRoute(depot.id, merger.Inputs[0].Port, merger.Output));
            if (path is not null)
                merger.AimPoint = path.Point(path.Length * 0.5f);
        }
        return merger;
    }

    // A merger input with no belt may sit right at another machine's output; capsules then arrive on that
    // machine's routes.
    private static void LinkDirectFeeds(MoonWorld world)
    {
        foreach (var machine in machines.Values)
        {
            if (machine is not Merger merger)
                continue;
            foreach (var input in merger.Inputs)
            {
                if (input.Lanes.Count > 0)
                    continue;
                foreach (var feeder in machines.Values)
                {
                    if (feeder.Id == merger.Id)
                        continue;
                    foreach (var route in feeder.OutputRoutes)
                    {
                        var path = world.MachinePath(route);
                        if (path is not null && (path.end - input.Point).sqrMagnitude <= PortToleranceSquared)
                            input.Lanes.Add(route);
                    }
                }
            }
        }
    }

    // Vanilla places a port at the structure's pose plus the scaled port offset.
    private static Vector3 PortPoint(MoonDepotBuildDefinition def, int port, Vector3 position, Quaternion rotation) =>
        position + rotation * (def.conveyorPorts[port].position * (def.scale > 0f ? def.scale : 1f));

    // Sides are judged in the machine's own frame: a port is on the player's right when it sits to the right of
    // the flow line through the single port on the other end, looking downstream. With two ports on opposite
    // sides they are simply "left" and "right"; anything else falls back to port numbers.
    private static void NameSides(Machine machine, MoonDepotBuildDefinition def, Vector3 forward, MoonDepotBuildDefinition.ConveyorPort anchor)
    {
        var ports = def.conveyorPorts;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-6f)
            forward = anchor.direction;
        machine.Flow = forward.normalized;
        var right = Vector3.Cross(Vector3.up, machine.Flow);

        var sides = new List<(float side, MachinePort port)>();
        foreach (var port in machine.Ports)
            sides.Add((Vector3.Dot(ports[port.Port].position - anchor.position, right), port));
        sides.Sort((a, b) => a.side.CompareTo(b.side));

        machine.LeftToRight.Clear();
        var distinct = sides.Count == 2 && sides[0].side < -0.05f && sides[1].side > 0.05f;
        for (var i = 0; i < sides.Count; i++)
        {
            sides[i].port.Side = distinct ? (i == 0 ? "left" : "right") : $"port {i + 1}";
            sides[i].port.Lateral = sides[i].side;
            machine.LeftToRight.Add(sides[i].port);
        }
    }

    private static Role RoleOf(MoonWorld world, int kind)
    {
        if (roles.TryGetValue(kind, out var known))
            return known;

        // Processors take a separate branch in HandOff and never read the routing cursor.
        if (kind == MoonWorld.PressureChamberKind || kind == MoonWorld.StonePressKind)
        {
            roles[kind] = Role.None;
            return Role.None;
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

        var role = inputs == 1 && outputs >= 2 ? Role.Splitter
            : inputs >= 2 && outputs == 1 ? Role.Merger
            : Role.None;
        roles[kind] = role;
        if (role != Role.None)
            Plugin.Logger.LogInfo($"Kind {kind} ({def.displayName}) is a {role.ToString().ToLowerInvariant()}: {inputs} in, {outputs} out, scale {def.scale}");
        return role;
    }
}
