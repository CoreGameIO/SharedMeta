using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Edge cases that have a habit of breaking serializers / appliers:
/// empty collections, single-element collections, very large collections,
/// duplicate values, special characters, extreme integers, and "no-op"
/// patches that should produce no children at all.
/// </summary>
public class EdgeCaseTests
{
    // ── Empty patches / no-ops ───────────────────────────────────────
    [Fact]
    public void NoMutations_StatesUnchanged()
        => AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), _ => { /* nothing */ });

    [Fact]
    public void NoOpListAccess_DoesNotPollutePatch()
        => AssertRoundtrip(SeedWithHeroes(3), w =>
        {
            // Just touching getters must not change anything
            _ = w.Heroes;
            _ = w.Numbers;
            _ = w.RequiredProfile.Level;
        });

    [Fact]
    public void IdempotentSet_SameValue()
        => AssertRoundtrip(SeedSimple(), w => w.Counter = 10);

    // ── Empty collections ────────────────────────────────────────────
    [Fact]
    public void EmptyHeroes_AddOne()
        => AssertRoundtrip(SeedSimple(), w => w.Heroes.Add(new Hero { Id = 1, Name = "Solo" }));

    [Fact]
    public void EmptyNumbers_Sort_Noop()
        => AssertRoundtrip(SeedSimple(), w => w.Numbers.Sort());

    [Fact]
    public void EmptyTags_Reverse_Noop()
        => AssertRoundtrip(SeedSimple(), w => w.Tags.Reverse());

    [Fact]
    public void EmptyHeroes_Clear_Noop()
        => AssertRoundtrip(SeedSimple(), w => w.Heroes.Clear());

    [Fact]
    public void EmptyEquipment_AddOne()
        => AssertRoundtrip(SeedWithHeroes(1), w => w.Heroes[0].Equipment.Add(new Item { ItemId = 1, Name = "First" }));

    // ── Single-element collections ───────────────────────────────────
    [Fact]
    public void SingleHero_RemoveAtZero()
        => AssertRoundtrip(SeedWithHeroes(1), w => w.Heroes.RemoveAt(0));

    [Fact]
    public void SingleNumber_Replace()
        => AssertRoundtrip(SeedWithNumbers(42), w => w.Numbers[0] = 99);

    [Fact]
    public void SingleNumber_Clear()
        => AssertRoundtrip(SeedWithNumbers(42), w => w.Numbers.Clear());

    [Fact]
    public void SingleNumber_Reverse_Noop()
        => AssertRoundtrip(SeedWithNumbers(42), w => w.Numbers.Reverse());

    // ── Large collections ────────────────────────────────────────────
    [Fact]
    public void LargeNumbers_Add100()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 100; i++) w.Numbers.Add(i);
        });

    [Fact]
    public void LargeBuffer_Add1024()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 1024; i++) w.Buffer.Add((byte)(i & 0xFF));
        });

    [Fact]
    public void LargeHeroes_Add50()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 50; i++)
                w.Heroes.Add(new Hero { Id = i, Name = $"H{i}", Level = i, Hp = 100 });
        });

    [Fact]
    public void LargeHeroes_RemoveHalf()
    {
        var seed = SeedSimple();
        for (int i = 0; i < 20; i++)
            seed.Heroes.Add(new Hero { Id = i, Name = $"H{i}", Hp = 100 });

        AssertRoundtrip(seed, w =>
        {
            for (int i = 0; i < 10; i++) w.Heroes.RemoveAt(0);
        });
    }

    // ── Duplicates ───────────────────────────────────────────────────
    [Fact]
    public void Numbers_AddDuplicates()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Numbers.Add(7);
            w.Numbers.Add(7);
            w.Numbers.Add(7);
        });

    [Fact]
    public void Tags_AddDuplicates()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Tags.Add("dup");
            w.Tags.Add("dup");
        });

    [Fact]
    public void Heroes_AddTwoWithSameId()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Heroes.Add(new Hero { Id = 1, Name = "First" });
            w.Heroes.Add(new Hero { Id = 1, Name = "Second" });
        });

    // ── Extreme integer values ───────────────────────────────────────
    [Fact]
    public void Numbers_ExtremeValues()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Numbers.Add(int.MaxValue);
            w.Numbers.Add(int.MinValue);
            w.Numbers.Add(0);
        });

    [Fact]
    public void Hero_ExtremeFieldValues()
        => AssertRoundtrip(SeedWithHeroes(1), w =>
        {
            var h = w.Heroes[0];
            h.Hp = int.MinValue;
            h.Exp = int.MaxValue;
            h.Level = 0;
        });

    // ── String edge cases ────────────────────────────────────────────
    [Fact]
    public void Tags_VariousStrings()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Tags.Add("");
            w.Tags.Add(" ");
            w.Tags.Add("\t");
            w.Tags.Add("\n");
            w.Tags.Add("\"");
            w.Tags.Add("\\");
            w.Tags.Add("Юникод");
            w.Tags.Add("🎮");
        });

    [Fact]
    public void Hero_NameWithSpecialChars()
        => AssertRoundtrip(SeedWithHeroes(1), w =>
            w.Heroes[0].Name = "Aragorn \"the King\" 🗡 son-of-Arathorn");

    // ── Reset everything in one call ─────────────────────────────────
    [Fact]
    public void ResetEverything_OneCall()
        => AssertRoundtrip(SeedWithHeroes(5, itemsPerHero: 3), w =>
        {
            w.Counter = 0;
            w.Timestamp = 0;
            w.Name = "";
            w.Active = false;
            w.Kind = FuzzKind.None;
            w.Blob = System.Array.Empty<byte>();
            w.OptionalProfile = null;
            w.RequiredProfile = new Profile();
            w.Numbers = new List<int>();
            w.Tags = new List<string>();
            w.Buffer = new List<byte>();
            w.Heroes = new List<Hero>();
            w.Counters = new Dictionary<string, int>();
            w.Flags = new HashSet<int>();
        });

    [Fact]
    public void ResetEverythingThenPopulate_OneCall()
        => AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            w.Heroes = new List<Hero>();
            w.Heroes.Add(new Hero { Id = 1, Name = "Solo", Hp = 100 });
            w.Heroes[0].Equipment.Add(new Item { ItemId = 1, Name = "Sword", Power = 100 });
            w.Heroes[0].Equipment[0].Tags.Add("legendary");
        });

    // ── Pathological op streams ──────────────────────────────────────
    [Fact]
    public void Numbers_AddRemoveAlternating()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 10; i++)
            {
                w.Numbers.Add(i);
                if (i > 0 && w.Numbers.Count > 0) w.Numbers.RemoveAt(0);
            }
        });

    [Fact]
    public void Heroes_BuildAndTeardown()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            for (int i = 0; i < 10; i++)
                w.Heroes.Add(new Hero { Id = i, Name = $"H{i}", Hp = 100 });
            for (int i = 0; i < 5; i++)
                w.Heroes.RemoveAt(0);
            for (int i = 0; i < w.Heroes.Count; i++)
                w.Heroes[i].Hp = (i + 1) * 10;
        });

    [Fact]
    public void Heroes_InsertAtAllPositions()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Heroes.Insert(0, new Hero { Id = 1, Name = "A" });
            w.Heroes.Insert(0, new Hero { Id = 2, Name = "B" });
            w.Heroes.Insert(1, new Hero { Id = 3, Name = "C" });
            w.Heroes.Insert(3, new Hero { Id = 4, Name = "D" });
            w.Heroes.Insert(2, new Hero { Id = 5, Name = "E" });
        });
}
