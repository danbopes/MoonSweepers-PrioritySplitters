using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MoonCleaners;

namespace PrioritySplitters;

// The capsules on one belt or route, as of the last scan.
internal struct Lane
{
    public int Count;
    public int Stopped;
    public float Head;
    public float Tail;
}

// MoonWorld.riders maps capsule id to a Rider struct (belt, distance, lag, moving). The generated interop hands
// native code an 8 byte slot for that 40 byte struct whenever it is passed by reference, which corrupts the
// stack, so the dictionary is read straight from memory instead. Every offset comes from the runtime's own
// metadata and each scan is checked against the dictionary's count, so a layout change disables the mod rather
// than misrouting capsules.
internal static unsafe class RiderTable
{
    // Il2CppObject is a class pointer plus a monitor; Il2CppArray adds a bounds pointer and the length.
    private const int ObjectHeader = 0x10;
    private const int ArrayData = 0x20;

    private static bool tableResolved, entryResolved;
    private static int ridersOffset;
    private static int entriesOffset, countOffset, freeCountOffset;
    private static int entrySize, hashOffset, nextOffset, valueOffset;
    private static int beltOffset, distanceOffset, movingOffset;

    private static readonly Dictionary<int, Lane> lanes = new();

    public static IReadOnlyDictionary<int, Lane> Lanes => lanes;

    public static Lane Of(int belt) => lanes.TryGetValue(belt, out var lane) ? lane : default;

    public static void Scan(MoonWorld world)
    {
        if (!tableResolved)
            ridersOffset = Offset(Il2CppClassPointerStore<MoonWorld>.NativeClassPtr, "riders");
        var dict = *(byte**)((byte*)world.Pointer + ridersOffset);
        if (dict == null)
            throw new InvalidOperationException("the world has no rider table");
        if (!tableResolved)
            ResolveTable(dict);

        lanes.Clear();
        var used = *(int*)(dict + countOffset);
        var expected = used - *(int*)(dict + freeCountOffset);
        var entries = *(byte**)(dict + entriesOffset);
        if (entries == null)
        {
            // The dictionary only allocates its entry array on the first Add, so a world that has never had a
            // capsule on a belt has none. Nothing to scan, and the entry layout waits for the first capsule.
            if (expected != 0)
                throw new InvalidOperationException($"rider table holds {expected} riders without an entry array");
            return;
        }
        if (!entryResolved)
            ResolveEntry(entries);

        var length = (int)IL2CPP.il2cpp_array_length((IntPtr)entries);
        if (used > length)
            throw new InvalidOperationException($"rider table claims {used} entries in an array of {length}");

        var live = 0;
        var entry = entries + ArrayData;
        for (var i = 0; i < used; i++, entry += entrySize)
        {
            // Freed slots carry a negative hash in older runtimes and a next index below -1 in newer ones.
            if (*(int*)(entry + hashOffset) < 0 || *(int*)(entry + nextOffset) < -1)
                continue;
            live++;

            var rider = entry + valueOffset;
            var belt = *(int*)(rider + beltOffset);
            var distance = *(float*)(rider + distanceOffset);
            var moving = *(rider + movingOffset) != 0;

            lanes.TryGetValue(belt, out var lane);
            if (lane.Count == 0 || distance > lane.Head)
                lane.Head = distance;
            if (lane.Count == 0 || distance < lane.Tail)
                lane.Tail = distance;
            lane.Count++;
            if (!moving)
                lane.Stopped++;
            lanes[belt] = lane;
        }

        if (live != expected)
            throw new InvalidOperationException($"rider table read {live} live riders but the dictionary holds {expected}");
    }

    private static void ResolveTable(byte* dict)
    {
        var dictClass = IL2CPP.il2cpp_object_get_class((IntPtr)dict);
        entriesOffset = Offset(dictClass, "_entries");
        countOffset = Offset(dictClass, "_count");
        freeCountOffset = Offset(dictClass, "_freeCount");
        tableResolved = true;
    }

    // The entry type is a nested generic instantiation with no handle of its own in the interop, so its layout
    // is read off the first entry array the dictionary allocates.
    private static void ResolveEntry(byte* entries)
    {
        var arrayClass = IL2CPP.il2cpp_object_get_class((IntPtr)entries);
        var entryClass = IL2CPP.il2cpp_class_get_element_class(arrayClass);
        entrySize = IL2CPP.il2cpp_array_element_size(arrayClass);

        // Field offsets of value types include the object header they would carry when boxed.
        hashOffset = Offset(entryClass, "hashCode") - ObjectHeader;
        nextOffset = Offset(entryClass, "next") - ObjectHeader;
        valueOffset = Offset(entryClass, "value") - ObjectHeader;

        var riderClass = IL2CPP.GetIl2CppNestedType(Il2CppClassPointerStore<MoonWorld>.NativeClassPtr, "Rider");
        beltOffset = Offset(riderClass, "belt") - ObjectHeader;
        distanceOffset = Offset(riderClass, "distance") - ObjectHeader;
        movingOffset = Offset(riderClass, "moving") - ObjectHeader;

        Plugin.Logger.LogInfo($"Rider table layout: entry {entrySize} bytes, hash@{hashOffset} next@{nextOffset} rider@{valueOffset}; belt@{beltOffset} distance@{distanceOffset} moving@{movingOffset}");
        if (hashOffset < 0 || nextOffset < 0 || valueOffset < 0 || beltOffset < 0 || distanceOffset < 0 || movingOffset < 0
            || valueOffset + movingOffset >= entrySize)
            throw new InvalidOperationException("the rider table layout is not one this build understands");
        entryResolved = true;
    }

    private static int Offset(IntPtr klass, string field)
    {
        var handle = IL2CPP.il2cpp_class_get_field_from_name(klass, field);
        if (handle == IntPtr.Zero)
            throw new MissingFieldException(field);
        return (int)IL2CPP.il2cpp_field_get_offset(handle);
    }
}
