using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Per-element mutations on the element-sub-wrappable Heroes list — i.e.
/// <c>state.Heroes[i].SomeField = X</c>. The patch tree must produce
/// per-element subtree nodes (Kind = ElementByIndex) at the canonical post-op
/// indices, and the applier must apply mutations to the right hero.
/// </summary>
public class HeroElementTests
{
    // ── Single-field mutations on a single hero ──────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Heroes_HpMutation(int index)
        => AssertRoundtrip(SeedWithHeroes(5), w => w.Heroes[index].Hp = 99);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void Heroes_ExpMutation(int index)
        => AssertRoundtrip(SeedWithHeroes(5), w => w.Heroes[index].Exp = 1234);

    [Fact]
    public void Heroes_NameMutation()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].Name = "Renamed");

    [Fact]
    public void Heroes_LevelMutation()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[2].Level = 99);

    [Fact]
    public void Heroes_IdMutation()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[0].Id = 999);

    // ── Multiple fields on the same hero ─────────────────────────────
    [Fact]
    public void Heroes_MultipleFields_OneHero()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            var h = w.Heroes[1];
            h.Hp = 50;
            h.Exp = 999;
            h.Level = 7;
            h.Name = "Hybrid";
        });

    // ── Mutations across multiple heroes ─────────────────────────────
    [Fact]
    public void Heroes_TwoHeroes_OneCall()
        => AssertRoundtrip(SeedWithHeroes(5), w =>
        {
            w.Heroes[0].Exp = 100;
            w.Heroes[4].Hp = 999;
        });

    [Fact]
    public void Heroes_AllHeroes_OneFieldEach()
        => AssertRoundtrip(SeedWithHeroes(5), w =>
        {
            for (int i = 0; i < 5; i++) w.Heroes[i].Exp = (i + 1) * 100;
        });

    [Fact]
    public void Heroes_AllHeroes_AllFields()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            for (int i = 0; i < 3; i++)
            {
                var h = w.Heroes[i];
                h.Hp = 100 + i;
                h.Exp = 200 + i;
                h.Level = 10 + i;
                h.Name = $"Updated{i}";
            }
        });

    // ── Stats sub-object on heroes ───────────────────────────────────
    [Fact]
    public void Heroes_Stats_FieldMutation()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].Stats!.Hp = 999);

    [Fact]
    public void Heroes_Stats_AllFieldsMutation()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            var s = w.Heroes[1].Stats!;
            s.Hp = 1;
            s.Mp = 2;
            s.Strength = 3;
            s.Agility = 4;
        });

    [Fact]
    public void Heroes_Stats_NullToObject()
    {
        var seed = SeedWithHeroes(3);
        seed.Heroes[1].Stats = null;
        AssertRoundtrip(seed, w => w.Heroes[1].Stats = new Stats { Hp = 100, Mp = 50 });
    }

    [Fact]
    public void Heroes_Stats_ObjectToNull()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].Stats = null);

    [Fact]
    public void Heroes_Stats_ObjectToObject()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].Stats = new Stats
        {
            Hp = 9999, Mp = 8888, Strength = 100, Agility = 50,
        });

    [Fact]
    public void Heroes_Stats_SetThenMutate()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[1].Stats = new Stats { Hp = 10 };
            w.Heroes[1].Stats!.Hp = 200;
            w.Heroes[1].Stats!.Mp = 100;
        });

    [Fact]
    public void Heroes_Stats_TwoHeroesMutated()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[0].Stats!.Hp = 1;
            w.Heroes[2].Stats!.Hp = 9999;
        });

    // ── SkillIds (scalar list inside list element) ───────────────────
    [Fact]
    public void Heroes_SkillIds_AddOne()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].SkillIds.Add(999));

    [Fact]
    public void Heroes_SkillIds_RemoveAt()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].SkillIds.RemoveAt(0));

    [Fact]
    public void Heroes_SkillIds_IndexerSet()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].SkillIds[0] = 9999);

    [Fact]
    public void Heroes_SkillIds_Clear()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].SkillIds.Clear());

    [Fact]
    public void Heroes_SkillIds_Reassign()
        => AssertRoundtrip(SeedWithHeroes(3), w => w.Heroes[1].SkillIds = new List<int> { 1, 2, 3 });

    [Fact]
    public void Heroes_SkillIds_Reassign_ThenAdd()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[1].SkillIds = new List<int>();
            w.Heroes[1].SkillIds.Add(99);
            w.Heroes[1].SkillIds.Add(98);
        });

    [Fact]
    public void Heroes_SkillIds_AcrossMultipleHeroes()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[0].SkillIds.Add(1);
            w.Heroes[1].SkillIds.Add(2);
            w.Heroes[2].SkillIds.Add(3);
        });

    [Fact]
    public void Heroes_SkillIds_SortInsideElement()
        => AssertRoundtrip(SeedWithHeroes(2), w =>
        {
            w.Heroes[0].SkillIds.Clear();
            w.Heroes[0].SkillIds.Add(3);
            w.Heroes[0].SkillIds.Add(1);
            w.Heroes[0].SkillIds.Add(2);
            w.Heroes[0].SkillIds.Sort();
        });

    // ── Combined element + structural ─────────────────────────────────
    [Fact]
    public void Heroes_MutateElementThenAddNew()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[1].Hp = 10;
            w.Heroes.Add(new Hero { Id = 99, Name = "New", Level = 1, Hp = 100 });
        });

    [Fact]
    public void Heroes_MutateElementThenClear()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            w.Heroes[1].Hp = 10;
            w.Heroes.Clear();
        });
}
