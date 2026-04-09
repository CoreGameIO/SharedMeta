using System.Collections.Generic;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.PatchFuzzTests;

// =====================================================================
// State shape used by all fuzz tests.
//
// The shape is intentionally rich so that one shared roundtrip harness
// exercises every patch-tree code path:
//
//  - terminal scalar fields (int, long, string, bool, byte[], enum)
//  - sub-objects: nullable + required, with their own nested sub-object
//  - scalar lists at the root (List<int>, List<string>, List<byte>)
//  - element-sub-wrappable list at the root (List<Hero>) — every element
//    has its own [MemoryPackOrder] members so the generator emits a
//    specialized HeroPatchableList that hands out HeroPatchWrapper
//  - lists nested INSIDE list elements: Hero.Equipment (List<Item>,
//    element-sub-wrappable) and Hero.SkillIds (List<int>, scalar list)
//  - lists nested inside sub-objects of list elements: Item.Tags
//    (List<string>) lives inside Item which lives inside Hero.Equipment
//    which lives inside the root Heroes list — three levels of nesting
//  - sub-object inside list element: Hero.Stats
//  - Dictionary<string, int> and HashSet<int> at the root for
//    completeness
//
// All types are MemoryPackable so the harness can deep-copy via
// pack/unpack and compare states by serialized bytes.
// =====================================================================

public enum FuzzKind : byte
{
    None = 0,
    Alpha = 1,
    Beta = 2,
    Gamma = 3,
}

[MemoryPackable]
public partial class Stats
{
    [MemoryPackOrder(0)] public int Hp { get; set; }
    [MemoryPackOrder(1)] public int Mp { get; set; }
    [MemoryPackOrder(2)] public int Strength { get; set; }
    [MemoryPackOrder(3)] public int Agility { get; set; }
}

[MemoryPackable]
public partial class Item
{
    [MemoryPackOrder(0)] public int ItemId { get; set; }
    [MemoryPackOrder(1)] public string Name { get; set; } = "";
    [MemoryPackOrder(2)] public int Power { get; set; }
    [MemoryPackOrder(3)] public List<string> Tags { get; set; } = new();
}

[MemoryPackable]
public partial class Hero
{
    [MemoryPackOrder(0)] public int Id { get; set; }
    [MemoryPackOrder(1)] public string Name { get; set; } = "";
    [MemoryPackOrder(2)] public int Level { get; set; }
    [MemoryPackOrder(3)] public int Hp { get; set; }
    [MemoryPackOrder(4)] public int Exp { get; set; }
    [MemoryPackOrder(5)] public Stats? Stats { get; set; }
    [MemoryPackOrder(6)] public List<Item> Equipment { get; set; } = new();
    [MemoryPackOrder(7)] public List<int> SkillIds { get; set; } = new();
}

[MemoryPackable]
public partial class Profile
{
    [MemoryPackOrder(0)] public string DisplayName { get; set; } = "";
    [MemoryPackOrder(1)] public int Level { get; set; }
    [MemoryPackOrder(2)] public Stats? Stats { get; set; }
    [MemoryPackOrder(3)] public List<string> Achievements { get; set; } = new();
}

[MemoryPackable]
public partial class FuzzState : ISharedState
{
    // ── Scalar fields ────────────────────────────────────────────────
    [MemoryPackOrder(0)] public int Counter { get; set; }
    [MemoryPackOrder(1)] public long Timestamp { get; set; }
    [MemoryPackOrder(2)] public string Name { get; set; } = "";
    [MemoryPackOrder(3)] public bool Active { get; set; }
    [MemoryPackOrder(4)] public FuzzKind Kind { get; set; }
    [MemoryPackOrder(5)] public byte[] Blob { get; set; } = System.Array.Empty<byte>();

    // ── Sub-objects ──────────────────────────────────────────────────
    [MemoryPackOrder(6)] public Profile? OptionalProfile { get; set; }
    [MemoryPackOrder(7)] public Profile RequiredProfile { get; set; } = new();

    // ── Scalar lists ─────────────────────────────────────────────────
    [MemoryPackOrder(8)] public List<int> Numbers { get; set; } = new();
    [MemoryPackOrder(9)] public List<string> Tags { get; set; } = new();
    [MemoryPackOrder(10)] public List<byte> Buffer { get; set; } = new();

    // ── Element-sub-wrappable list with deeply nested shapes ─────────
    [MemoryPackOrder(11)] public List<Hero> Heroes { get; set; } = new();

    // ── Dictionary + HashSet ─────────────────────────────────────────
    [MemoryPackOrder(12)] public Dictionary<string, int> Counters { get; set; } = new();
    [MemoryPackOrder(13)] public HashSet<int> Flags { get; set; } = new();
}
