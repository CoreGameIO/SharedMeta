using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Dictionary&lt;string, int&gt; and HashSet&lt;int&gt; mutations.
/// These collections use the simpler "terminal Value with each mutation"
/// path (no structural ops), so the contract is: any mutation produces a
/// patch whose receiver-side application yields the same dictionary/set.
/// </summary>
public class DictHashSetTests
{
    // ── Counters: Dictionary<string, int> ────────────────────────────
    [Fact]
    public void Counters_AddOne_ToEmpty()
        => AssertRoundtrip(SeedSimple(), w => w.Counters.Add("first", 1));

    [Fact]
    public void Counters_AddMany_ToEmpty()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Counters.Add("a", 1);
            w.Counters.Add("b", 2);
            w.Counters.Add("c", 3);
        });

    [Fact]
    public void Counters_IndexerAdd()
        => AssertRoundtrip(SeedSimple(), w => w.Counters["new"] = 42);

    [Fact]
    public void Counters_IndexerOverwrite()
        => AssertRoundtrip(SeedWithCounters(("k", 1)), w => w.Counters["k"] = 999);

    [Fact]
    public void Counters_RemoveExisting()
        => AssertRoundtrip(SeedWithCounters(("a", 1), ("b", 2)), w => w.Counters.Remove("a"));

    [Fact]
    public void Counters_RemoveNonExistent_Noop()
        => AssertRoundtrip(SeedWithCounters(("a", 1)), w => w.Counters.Remove("missing"));

    [Fact]
    public void Counters_Clear()
        => AssertRoundtrip(SeedWithCounters(("a", 1), ("b", 2), ("c", 3)), w => w.Counters.Clear());

    [Fact]
    public void Counters_AddRemoveAddSameKey()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Counters["k"] = 1;
            w.Counters.Remove("k");
            w.Counters["k"] = 2;
        });

    [Fact]
    public void Counters_TryAdd_Existing_Noop()
        => AssertRoundtrip(SeedWithCounters(("k", 1)), w => w.Counters.TryAdd("k", 999));

    [Fact]
    public void Counters_TryAdd_New()
        => AssertRoundtrip(SeedWithCounters(("k", 1)), w => w.Counters.TryAdd("new", 99));

    [Fact]
    public void Counters_Reassign_Empty()
        => AssertRoundtrip(SeedWithCounters(("a", 1), ("b", 2)), w => w.Counters = new Dictionary<string, int>());

    [Fact]
    public void Counters_Reassign_NonEmpty()
        => AssertRoundtrip(SeedWithCounters(("a", 1)), w => w.Counters = new Dictionary<string, int>
        {
            ["x"] = 100,
            ["y"] = 200,
        });

    [Fact]
    public void Counters_Reassign_PropertySetter()
        => AssertRoundtrip(SeedWithCounters(("a", 1)), w => w.Counters = new Dictionary<string, int>
        {
            ["x"] = 100,
        });

    [Fact]
    public void Counters_Reassign_ThenAddAfter()
        => AssertRoundtrip(SeedWithCounters(("a", 1), ("b", 2)), w =>
        {
            w.Counters = new Dictionary<string, int>();
            w.Counters["c"] = 3;
        });

    [Fact]
    public void Counters_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithCounters(("a", 1)), w =>
        {
            w.Counters = new Dictionary<string, int>();
            w.Counters["new"] = 42;
        });

    [Fact]
    public void Counters_SpecialCharsKeys()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Counters[""] = 0;
            w.Counters["with space"] = 1;
            w.Counters["Юникод"] = 2;
            w.Counters["a:b/c\\d"] = 3;
        });

    [Fact]
    public void Counters_LongKeyChain()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 30; i++) w.Counters[$"k{i:D3}"] = i * i;
        });

    [Fact]
    public void Counters_AddMostThenRemoveSome()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 10; i++) w.Counters[$"k{i}"] = i;
            for (int i = 0; i < 5; i++) w.Counters.Remove($"k{i}");
        });

    // ── Flags: HashSet<int> ──────────────────────────────────────────
    [Fact]
    public void Flags_AddOne_ToEmpty()
        => AssertRoundtrip(SeedSimple(), w => w.Flags.Add(42));

    [Fact]
    public void Flags_AddMany_ToEmpty()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 10; i++) w.Flags.Add(i);
        });

    [Fact]
    public void Flags_AddDuplicate_Noop()
        => AssertRoundtrip(SeedWithFlags(1, 2, 3), w => w.Flags.Add(2));

    [Fact]
    public void Flags_RemoveExisting()
        => AssertRoundtrip(SeedWithFlags(1, 2, 3), w => w.Flags.Remove(2));

    [Fact]
    public void Flags_RemoveNonExistent_Noop()
        => AssertRoundtrip(SeedWithFlags(1, 2, 3), w => w.Flags.Remove(999));

    [Fact]
    public void Flags_Clear()
        => AssertRoundtrip(SeedWithFlags(1, 2, 3, 4, 5), w => w.Flags.Clear());

    [Fact]
    public void Flags_AddRemoveAddSame()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Flags.Add(99);
            w.Flags.Remove(99);
            w.Flags.Add(99);
        });

    [Fact]
    public void Flags_Reassign_Empty()
        => AssertRoundtrip(SeedWithFlags(1, 2, 3), w => w.Flags = new HashSet<int>());

    [Fact]
    public void Flags_Reassign_NonEmpty()
        => AssertRoundtrip(SeedWithFlags(1), w => w.Flags = new HashSet<int> { 10, 20, 30 });

    [Fact]
    public void Flags_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithFlags(1), w =>
        {
            w.Flags = new HashSet<int>();
            w.Flags.Add(99);
        });
}
