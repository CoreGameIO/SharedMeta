using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Structural ops on the element-sub-wrappable <c>Heroes</c> list.
/// Heroes contain their own sub-objects (Stats) and sub-lists (Equipment, SkillIds),
/// so structural ops on the outer list have to coexist with element children
/// for any pre-existing per-hero mutations.
/// </summary>
public class HeroListTests
{
    private static Hero MakeHero(int id, string name = "Hero", int level = 1)
        => new() { Id = id, Name = name, Level = level, Hp = 100, Exp = 0 };

    // ── Add ──────────────────────────────────────────────────────────
    [Fact]
    public void Heroes_AddOne_FromEmpty()
        => AssertRoundtrip(SeedSimple(), w => w.Heroes.Add(MakeHero(1, "First")));

    [Fact]
    public void Heroes_AddMany_FromEmpty()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 1; i <= 10; i++) w.Heroes.Add(MakeHero(i, $"H{i}", i));
        });

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(50)]
    public void Heroes_Add_BatchSizes(int count)
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 1; i <= count; i++) w.Heroes.Add(MakeHero(i));
        });

    [Fact]
    public void Heroes_AddOne_OntoExisting()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes.Add(MakeHero(99, "New")));

    // ── Insert ───────────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Heroes_InsertAt(int index)
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes.Insert(index, MakeHero(99, "Ins")));

    [Fact]
    public void Heroes_InsertHead_RepeatedTimes()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes.Insert(0, MakeHero(101));
            w.Heroes.Insert(0, MakeHero(102));
            w.Heroes.Insert(0, MakeHero(103));
        });

    [Fact]
    public void Heroes_InsertMiddle_RepeatedTimes()
        => AssertRoundtrip(SeedWithHeroes(4), w =>
        {
            w.Heroes.Insert(2, MakeHero(101));
            w.Heroes.Insert(2, MakeHero(102));
            w.Heroes.Insert(2, MakeHero(103));
        });

    // ── RemoveAt ─────────────────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Heroes_RemoveAt(int index)
        => AssertRoundtrip(SeedWithHeroes(5), w => w.Heroes.RemoveAt(index));

    [Fact]
    public void Heroes_RemoveAll_FromTail()
        => AssertRoundtrip(SeedWithHeroes(5), w =>
        {
            while (w.Heroes.Count > 0) w.Heroes.RemoveAt(w.Heroes.Count - 1);
        });

    [Fact]
    public void Heroes_RemoveAll_FromHead()
        => AssertRoundtrip(SeedWithHeroes(5), w =>
        {
            while (w.Heroes.Count > 0) w.Heroes.RemoveAt(0);
        });

    // ── Indexer Set ──────────────────────────────────────────────────
    [Fact]
    public void Heroes_IndexerSet_First()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[0] = MakeHero(99, "Replaced"));

    [Fact]
    public void Heroes_IndexerSet_Last()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[2] = MakeHero(99, "Replaced"));

    [Fact]
    public void Heroes_IndexerSet_AllElements()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[0] = MakeHero(101, "A");
            w.Heroes[1] = MakeHero(102, "B");
            w.Heroes[2] = MakeHero(103, "C");
        });

    // ── Clear ────────────────────────────────────────────────────────
    [Fact]
    public void Heroes_Clear_NonEmpty()
        => AssertRoundtrip(SeedWithHeroes(5), w => w.Heroes.Clear());

    [Fact]
    public void Heroes_Clear_Empty_Noop()
        => AssertRoundtrip(SeedSimple(), w => w.Heroes.Clear());

    [Fact]
    public void Heroes_ClearThenAdd()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes.Clear();
            w.Heroes.Add(MakeHero(99, "AfterClear"));
        });

    // ── Mixed: structural + element mutations ────────────────────────
    [Fact]
    public void Heroes_AddThenRemoveAt()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes.Add(MakeHero(99));
            w.Heroes.RemoveAt(0);
        });

    [Fact]
    public void Heroes_InsertThenSet()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes.Insert(1, MakeHero(99));
            w.Heroes[2] = MakeHero(88);
        });

    [Fact]
    public void Heroes_MixedShift_RemoveCausesIndexShift()
        => AssertRoundtrip(SeedWithHeroes(4), w =>
        {
            // Mutate the hero at idx 3 first (it gets an element child at index 3),
            // then remove the hero at idx 1 — sender must shift the element child
            // from idx 3 down to idx 2, applier must apply mutation at the new idx.
            w.Heroes[3].Exp = 500;
            w.Heroes.RemoveAt(1);
        });

    [Fact]
    public void Heroes_MixedShift_InsertCausesIndexShift()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            // Mutate hero at idx 1 (element child at index 1), then insert a new
            // hero at idx 0 — child must shift from idx 1 to idx 2.
            w.Heroes[1].Exp = 500;
            w.Heroes.Insert(0, MakeHero(99));
        });

    [Fact]
    public void Heroes_MultipleMutationsBeforeRemove()
        => AssertRoundtrip(SeedWithHeroes(5), w =>
        {
            w.Heroes[0].Exp = 100;
            w.Heroes[1].Exp = 200;
            w.Heroes[2].Exp = 300;
            w.Heroes[3].Exp = 400;
            w.Heroes[4].Exp = 500;
            w.Heroes.RemoveAt(2);
        });

    [Fact]
    public void Heroes_MutateThenInsertThenMutateNew()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes[0].Hp = 50;
            w.Heroes.Insert(1, MakeHero(99, "Inserted"));
            w.Heroes[1].Hp = 200;
            w.Heroes[2].Hp = 30;
        });

    // ── Reassign-then-mutate (the regression class) ───────────────────
    [Fact]
    public void Heroes_Reassign_EmptyList()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes = new List<Hero>());

    [Fact]
    public void Heroes_Reassign_NonEmpty()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes = new List<Hero>
        {
            MakeHero(101, "New1"),
            MakeHero(102, "New2"),
        });

    [Fact]
    public void Heroes_Reassign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes = new List<Hero>();
            w.Heroes.Add(MakeHero(99, "AfterReassign"));
        });

    [Fact]
    public void Heroes_Reassign_ThenAddMany()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes = new List<Hero>();
            for (int i = 0; i < 5; i++) w.Heroes.Add(MakeHero(i + 100));
        });

    [Fact]
    public void Heroes_Reassign_ThenInsert()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes = new List<Hero> { MakeHero(1), MakeHero(2) };
            w.Heroes.Insert(1, MakeHero(99));
        });

    [Fact]
    public void Heroes_Reassign_ThenSet()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes = new List<Hero> { MakeHero(1), MakeHero(2), MakeHero(3) };
            w.Heroes[1] = MakeHero(99);
        });

    [Fact]
    public void Heroes_Reassign_PropertySetter()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes = new List<Hero> { MakeHero(101), MakeHero(102) };
        });

    [Fact]
    public void Heroes_Reassign_PropertySetter_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes = new List<Hero>();
            w.Heroes.Add(MakeHero(101, "First"));
            w.Heroes.Add(MakeHero(102, "Second"));
        });

    [Fact]
    public void Heroes_Reassign_PropertySetter_ThenSet()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes = new List<Hero> { MakeHero(1), MakeHero(2) };
            w.Heroes[0].Hp = 999;
        });
}
