using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Two and three levels of nesting:
///   - Heroes[i].Equipment.Add — element-sub-wrappable list inside an
///     element-sub-wrappable list element
///   - Heroes[i].Equipment[j].Power = X — element mutation inside the inner
///     element-sub-wrappable list
///   - Heroes[i].Equipment[j].Tags.Add — scalar list inside an element-sub-
///     wrappable list element inside an element-sub-wrappable list element
/// </summary>
public class DeepNestedTests
{
    private static Item MakeItem(int id, string name = "Item", int power = 10)
        => new() { ItemId = id, Name = name, Power = power };

    // ── Equipment list structural ops on a single hero ───────────────
    [Fact]
    public void Equipment_AddOne_FromEmpty()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].Equipment.Add(MakeItem(99, "Sword")));

    [Fact]
    public void Equipment_AddMany_FromEmpty()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            for (int i = 0; i < 5; i++) w.Heroes[0].Equipment.Add(MakeItem(i, $"It{i}", i * 10));
        });

    [Fact]
    public void Equipment_AddOne_OntoExisting()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w => w.Heroes[1].Equipment.Add(MakeItem(99)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Equipment_InsertAt(int index)
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w => w.Heroes[0].Equipment.Insert(index, MakeItem(99)));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Equipment_RemoveAt(int index)
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w => w.Heroes[1].Equipment.RemoveAt(index));

    [Fact]
    public void Equipment_IndexerSet()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
            w.Heroes[1].Equipment[0] = MakeItem(999, "Replaced", 999));

    [Fact]
    public void Equipment_Clear()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 3), w => w.Heroes[0].Equipment.Clear());

    [Fact]
    public void Equipment_ClearThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment.Clear();
            w.Heroes[0].Equipment.Add(MakeItem(99, "AfterClear"));
        });

    // ── Equipment reassignment then mutate ───────────────────────────
    [Fact]
    public void Equipment_Reassign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment = new List<Item>();
            w.Heroes[0].Equipment.Add(MakeItem(99));
        });

    [Fact]
    public void Equipment_PropertyAssign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment = new List<Item>();
            w.Heroes[0].Equipment.Add(MakeItem(99));
            w.Heroes[0].Equipment.Add(MakeItem(98));
        });

    // ── Inner element mutation: Heroes[i].Equipment[j].X = Y ─────────
    [Fact]
    public void Equipment_PowerMutation()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w => w.Heroes[1].Equipment[0].Power = 999);

    [Fact]
    public void Equipment_NameMutation()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w => w.Heroes[1].Equipment[1].Name = "Renamed");

    [Fact]
    public void Equipment_AllFieldsMutation()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            var item = w.Heroes[0].Equipment[1];
            item.ItemId = 9999;
            item.Name = "Mutated";
            item.Power = 100;
        });

    [Fact]
    public void Equipment_TwoItemsMutated_SameHero()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 3), w =>
        {
            w.Heroes[0].Equipment[0].Power = 1;
            w.Heroes[0].Equipment[2].Power = 9;
        });

    [Fact]
    public void Equipment_TwoItemsMutated_DifferentHeroes()
        => AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment[0].Power = 1;
            w.Heroes[2].Equipment[1].Power = 9;
        });

    // ── Three levels deep: Heroes[i].Equipment[j].Tags ────────────────
    [Fact]
    public void Equipment_Tags_AddOne()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
            w.Heroes[0].Equipment[0].Tags.Add("brand-new"));

    [Fact]
    public void Equipment_Tags_RemoveAt()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
            w.Heroes[0].Equipment[0].Tags.RemoveAt(0));

    [Fact]
    public void Equipment_Tags_IndexerSet()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
            w.Heroes[0].Equipment[0].Tags[0] = "MUTATED");

    [Fact]
    public void Equipment_Tags_Clear()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
            w.Heroes[0].Equipment[0].Tags.Clear());

    [Fact]
    public void Equipment_Tags_Reassign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment[0].Tags = new List<string>();
            w.Heroes[0].Equipment[0].Tags.Add("after-reassign");
        });

    [Fact]
    public void Equipment_Tags_PropertyAssign_ThenMutate()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment[0].Tags = new List<string> { "x", "y" };
            w.Heroes[0].Equipment[0].Tags.Add("z");
        });

    [Fact]
    public void Equipment_Tags_TwoItems_OneCall()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 3), w =>
        {
            w.Heroes[0].Equipment[0].Tags.Add("first");
            w.Heroes[0].Equipment[2].Tags.Add("third");
        });

    [Fact]
    public void Equipment_Tags_TwoHeroes_OneCall()
        => AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment[0].Tags.Add("h0i0");
            w.Heroes[2].Equipment[1].Tags.Add("h2i1");
        });

    // ── Combined ops at multiple levels ──────────────────────────────
    [Fact]
    public void Equipment_StructuralOnInnerListWhileMutatingHeroFields()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Hp = 50;
            w.Heroes[0].Equipment.Add(MakeItem(99));
            w.Heroes[0].Equipment[0].Power = 999;
        });

    [Fact]
    public void Equipment_MixedShift_RemoveItemAfterMutatingLater()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 4), w =>
        {
            // Mutate item at idx 3 first, then remove item at idx 1 — element child
            // for idx 3 must shift down to idx 2.
            w.Heroes[0].Equipment[3].Power = 999;
            w.Heroes[0].Equipment.RemoveAt(1);
        });

    [Fact]
    public void Equipment_MixedShift_InsertItemBeforeMutatingLater()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 3), w =>
        {
            w.Heroes[0].Equipment[1].Power = 555;
            w.Heroes[0].Equipment.Insert(0, MakeItem(99));
        });

    [Fact]
    public void Equipment_OuterListReassignDropsInnerMutations()
        => AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            // Mutating an item then reassigning the entire equipment list — the mutation
            // must NOT survive on the receiver because the list it referenced is gone.
            w.Heroes[0].Equipment[0].Power = 999;
            w.Heroes[0].Equipment = new List<Item> { MakeItem(101, "AfterReset"), MakeItem(102) };
        });

    [Fact]
    public void Equipment_ChainOfThreeLevels()
        => AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            // Touch every level: outer hero list, inner equipment list, inner item tag list.
            w.Heroes[0].Hp = 50;
            w.Heroes[1].Equipment.Add(MakeItem(99));
            w.Heroes[2].Equipment[1].Tags.Add("triple-nested");
            w.Heroes[2].Equipment[0].Power = 777;
            w.Heroes[2].Stats!.Strength = 99;
        });
}
