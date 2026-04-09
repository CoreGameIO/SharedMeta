using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Concentrated coverage of "wholesale assign followed by in-place mutation in
/// the same call". This is the regression class that motivated the patch fuzz
/// suite — earlier versions wrote a terminal Value on the setter and silently
/// dropped subsequent structural ops on the receiver.
///
/// Every collection / sub-object reachable from FuzzState gets a test for both
/// the property setter (state.Field = newCollection) and the explicit Set helper
/// (state.SetField(newCollection)), followed by mutations through the wrapper.
/// </summary>
public class ReassignThenMutateTests
{
    private static Hero MakeHero(int id, string name = "H") => new() { Id = id, Name = name, Hp = 100 };

    // ── Numbers (PatchableList<int>) ─────────────────────────────────
    [Fact]
    public void Numbers_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w =>
        {
            w.Numbers = new List<int>();
            w.Numbers.Add(99);
        });

    [Fact]
    public void Numbers_PropertyAssign_NonEmpty_ThenAdd()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Numbers = new List<int> { 1, 2, 3 };
            w.Numbers.Add(4);
        });

    [Fact]
    public void Numbers_SetX_ThenAddMany()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w =>
        {
            w.Numbers = new List<int>();
            for (int i = 10; i < 20; i++) w.Numbers.Add(i);
        });

    [Fact]
    public void Numbers_PropertyAssign_ThenIndexerSet()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Numbers = new List<int> { 0, 0, 0, 0, 0 };
            for (int i = 0; i < 5; i++) w.Numbers[i] = i + 1;
        });

    [Fact]
    public void Numbers_PropertyAssign_ThenInsert()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Numbers = new List<int> { 1, 3, 5 };
            w.Numbers.Insert(1, 2);
            w.Numbers.Insert(3, 4);
        });

    [Fact]
    public void Numbers_PropertyAssign_ThenClear_ThenAdd()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w =>
        {
            w.Numbers = new List<int> { 10, 20, 30 };
            w.Numbers.Clear();
            w.Numbers.Add(99);
        });

    [Fact]
    public void Numbers_PropertyAssign_TwiceInRow()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w =>
        {
            w.Numbers = new List<int> { 10 };
            w.Numbers = new List<int> { 20, 30 };
        });

    [Fact]
    public void Numbers_PropertyAssign_TwiceInRow_ThenAdd()
        => AssertRoundtrip(SeedWithNumbers(1, 2, 3), w =>
        {
            w.Numbers = new List<int> { 10 };
            w.Numbers = new List<int> { 20, 30 };
            w.Numbers.Add(40);
        });

    // ── Tags (PatchableList<string>) ─────────────────────────────────
    [Fact]
    public void Tags_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithTags("a", "b"), w =>
        {
            w.Tags = new List<string>();
            w.Tags.Add("fresh");
        });

    [Fact]
    public void Tags_SetX_ThenAddMany()
        => AssertRoundtrip(SeedWithTags("old"), w =>
        {
            w.Tags = new List<string>();
            w.Tags.Add("a");
            w.Tags.Add("b");
            w.Tags.Add("c");
        });

    [Fact]
    public void Tags_PropertyAssign_ThenSort()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Tags = new List<string> { "c", "a", "b" };
            w.Tags.Sort();
        });

    // ── Buffer (PatchableList<byte>) — the "Cells" code path ─────────
    [Fact]
    public void Buffer_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithBuffer(1, 2, 3), w =>
        {
            w.Buffer = new List<byte>();
            w.Buffer.Add(99);
        });

    [Fact]
    public void Buffer_PropertyAssign_PrePopulated_ThenAdd()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Buffer = new List<byte>(64);
            for (int i = 0; i < 64; i++) w.Buffer.Add(0);
            for (int i = 0; i < 64; i++) w.Buffer[i] = (byte)i;
        });

    [Fact]
    public void Buffer_PropertyAssign_LikeRegenerateMap()
        => AssertRoundtrip(SeedWithBuffer(0, 0, 0, 0, 0, 0, 0, 0), w =>
        {
            // Closest analog to the user's RegenerateMap bug:
            // 1) Wholesale-replace the buffer with a fresh empty list
            // 2) Add elements one by one
            // 3) Mutate selected elements
            const int totalCells = 32;
            w.Buffer = new List<byte>(totalCells);
            for (int i = 0; i < totalCells; i++) w.Buffer.Add(0);
            for (int i = 0; i < totalCells; i += 4) w.Buffer[i] = 0xFF;
        });

    // ── Heroes (element-sub-wrappable list) ──────────────────────────
    [Fact]
    public void Heroes_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes = new List<Hero>();
            w.Heroes.Add(MakeHero(99));
        });

    [Fact]
    public void Heroes_SetX_ThenAddMultiple()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes = new List<Hero>();
            for (int i = 100; i < 105; i++) w.Heroes.Add(MakeHero(i));
        });

    [Fact]
    public void Heroes_PropertyAssign_PrePopulated_ThenMutate()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Heroes = new List<Hero>
            {
                MakeHero(1, "A"),
                MakeHero(2, "B"),
            };
            w.Heroes[0].Hp = 50;
            w.Heroes[1].Exp = 999;
        });

    [Fact]
    public void Heroes_PropertyAssign_ThenInsert()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes = new List<Hero> { MakeHero(1), MakeHero(3) };
            w.Heroes.Insert(1, MakeHero(2));
        });

    // ── Equipment (nested element-sub-wrappable list) ────────────────
    [Fact]
    public void Equipment_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment = new List<Item>();
            w.Heroes[0].Equipment.Add(new Item { ItemId = 99, Name = "New", Power = 1 });
        });

    [Fact]
    public void Equipment_SetX_ThenAddMultiple()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 3), w =>
        {
            w.Heroes[1].Equipment = new List<Item>();
            for (int i = 0; i < 3; i++)
                w.Heroes[1].Equipment.Add(new Item { ItemId = i, Name = $"It{i}" });
        });

    [Fact]
    public void Equipment_PropertyAssign_PrePopulated_ThenMutate()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment = new List<Item>
            {
                new() { ItemId = 1, Name = "X" },
                new() { ItemId = 2, Name = "Y" },
            };
            w.Heroes[0].Equipment[0].Power = 999;
        });

    // ── SkillIds (scalar list inside list element) ────────────────────
    [Fact]
    public void SkillIds_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[1].SkillIds = new List<int>();
            w.Heroes[1].SkillIds.Add(99);
        });

    // ── Tags inside Item inside Hero ─────────────────────────────────
    [Fact]
    public void ItemTags_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment[0].Tags = new List<string>();
            w.Heroes[0].Equipment[0].Tags.Add("brand-new");
        });

    // ── Profile.Achievements ─────────────────────────────────────────
    [Fact]
    public void Achievements_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile.Achievements = new List<string>();
            w.RequiredProfile.Achievements.Add("first");
        });

    [Fact]
    public void Achievements_SetX_ThenAddMany()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile.Achievements = new List<string> { "seed" };
            w.RequiredProfile.Achievements.Add("a");
            w.RequiredProfile.Achievements.Add("b");
        });

    // ── Counters (Dictionary) — terminal-Value path ──────────────────
    [Fact]
    public void Counters_PropertyAssign_ThenIndexerAdd()
        => AssertRoundtrip(SeedWithCounters(("seed", 1)), w =>
        {
            w.Counters = new Dictionary<string, int>();
            w.Counters["new"] = 99;
        });

    [Fact]
    public void Counters_SetX_ThenAdd()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Counters = new Dictionary<string, int>();
            w.Counters["k"] = 1;
        });

    // ── Flags (HashSet) — terminal-Value path ────────────────────────
    [Fact]
    public void Flags_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithFlags(1), w =>
        {
            w.Flags = new HashSet<int>();
            w.Flags.Add(99);
        });

    [Fact]
    public void Flags_SetX_ThenAddMany()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Flags = new HashSet<int>();
            for (int i = 1; i <= 5; i++) w.Flags.Add(i);
        });

    // ── Profile sub-object — Set + mutate ────────────────────────────
    [Fact]
    public void RequiredProfile_SetX_ThenMutateField()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile = new Profile { DisplayName = "Fresh", Level = 0 };
            w.RequiredProfile.Level = 99;
        });

    [Fact]
    public void OptionalProfile_SetX_ThenMutateField()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.OptionalProfile = new Profile { DisplayName = "Fresh", Level = 0 };
            w.OptionalProfile!.DisplayName = "Mutated";
        });

    [Fact]
    public void RequiredProfile_SetX_WithStats_ThenMutateInnerStats()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile = new Profile
            {
                DisplayName = "Fresh",
                Level = 0,
                Stats = new Stats { Hp = 100 },
            };
            w.RequiredProfile.Stats!.Hp = 999;
            w.RequiredProfile.Stats!.Mp = 50;
        });

    [Fact]
    public void Stats_SetX_ThenMutate()
    {
        var seed = SeedSimple();
        AssertRoundtrip(seed, w =>
        {
            w.RequiredProfile.Stats = new Stats { Hp = 1, Mp = 2, Strength = 3, Agility = 4 };
            w.RequiredProfile.Stats!.Hp = 999;
        });
    }
}
