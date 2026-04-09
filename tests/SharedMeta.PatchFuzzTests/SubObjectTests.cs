using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Sub-object (Profile / Stats) mutation tests.
///
/// Covers:
///  - in-place mutation through wrapper (RequiredProfile.X = Y)
///  - nested-nested mutation (RequiredProfile.Stats.Hp = Y)
///  - SetX wholesale replacement
///  - SetX-then-mutate (the regression class — Value+Children on the same node)
///  - nullable null↔object transitions
///  - mutating an inner scalar list on a sub-object (Profile.Achievements)
/// </summary>
public class SubObjectTests
{
    // ── RequiredProfile direct field mutations ────────────────────────
    [Fact]
    public void RequiredProfile_DisplayName_Set()
        => AssertRoundtrip(SeedSimple(), w => w.RequiredProfile.DisplayName = "Renamed");

    [Fact]
    public void RequiredProfile_Level_Set()
        => AssertRoundtrip(SeedSimple(), w => w.RequiredProfile.Level = 99);

    [Fact]
    public void RequiredProfile_DisplayNameAndLevel_OneCall()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile.DisplayName = "Champion";
            w.RequiredProfile.Level = 50;
        });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void RequiredProfile_Level_VariousValues(int level)
        => AssertRoundtrip(SeedSimple(), w => w.RequiredProfile.Level = level);

    // ── RequiredProfile SetProfile wholesale ──────────────────────────
    [Fact]
    public void RequiredProfile_SetWholesale()
        => AssertRoundtrip(SeedSimple(), w => w.RequiredProfile = new Profile
        {
            DisplayName = "NewProfile",
            Level = 7,
            Stats = new Stats { Hp = 1, Mp = 2, Strength = 3, Agility = 4 },
        });

    [Fact]
    public void RequiredProfile_SetWholesale_ThenMutateField()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            // Reproduces the SubWrappable Set+mutate bug class — sender writes Value
            // for the sub-object snapshot, then layers Children for the in-place
            // mutation. Receiver must apply Value first, then recurse into Children.
            w.RequiredProfile = new Profile { DisplayName = "Fresh", Level = 0 };
            w.RequiredProfile.Level = 99;
        });

    [Fact]
    public void RequiredProfile_SetWholesale_ThenMutateMultipleFields()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile = new Profile { DisplayName = "Fresh", Level = 0, Stats = new Stats() };
            w.RequiredProfile.DisplayName = "MutatedName";
            w.RequiredProfile.Level = 88;
            w.RequiredProfile.Stats!.Hp = 1234;
        });

    // ── Nested Stats on RequiredProfile ───────────────────────────────
    [Fact]
    public void RequiredProfile_Stats_NullToObject_ViaSet()
        => AssertRoundtrip(SeedSimple(), w => w.RequiredProfile.Stats = new Stats
        {
            Hp = 100, Mp = 50, Strength = 25, Agility = 10,
        });

    [Fact]
    public void RequiredProfile_Stats_FieldMutation()
    {
        var seed = SeedSimple();
        seed.RequiredProfile.Stats = new Stats { Hp = 100, Mp = 50, Strength = 25, Agility = 10 };
        AssertRoundtrip(seed, w => w.RequiredProfile.Stats!.Hp = 200);
    }

    [Fact]
    public void RequiredProfile_Stats_AllFieldsMutation()
    {
        var seed = SeedSimple();
        seed.RequiredProfile.Stats = new Stats { Hp = 100, Mp = 50, Strength = 25, Agility = 10 };
        AssertRoundtrip(seed, w =>
        {
            w.RequiredProfile.Stats!.Hp = 999;
            w.RequiredProfile.Stats!.Mp = 888;
            w.RequiredProfile.Stats!.Strength = 777;
            w.RequiredProfile.Stats!.Agility = 666;
        });
    }

    [Fact]
    public void RequiredProfile_Stats_SetNullViaSet()
    {
        var seed = SeedSimple();
        seed.RequiredProfile.Stats = new Stats { Hp = 100 };
        AssertRoundtrip(seed, w => w.RequiredProfile.Stats = null);
    }

    [Fact]
    public void RequiredProfile_Stats_SetThenMutate()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile.Stats = new Stats { Hp = 1, Mp = 2 };
            w.RequiredProfile.Stats!.Hp = 100;
            w.RequiredProfile.Stats!.Strength = 99;
        });

    // ── RequiredProfile.Achievements (scalar list inside sub-object) ──
    [Fact]
    public void RequiredProfile_Achievements_Add()
        => AssertRoundtrip(SeedSimple(), w => w.RequiredProfile.Achievements.Add("first"));

    [Fact]
    public void RequiredProfile_Achievements_AddMany()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile.Achievements.Add("a");
            w.RequiredProfile.Achievements.Add("b");
            w.RequiredProfile.Achievements.Add("c");
        });

    [Fact]
    public void RequiredProfile_Achievements_Reassign()
        => AssertRoundtrip(SeedSimple(), w => w.RequiredProfile.Achievements = new List<string> { "x", "y" });

    [Fact]
    public void RequiredProfile_Achievements_ReassignThenAdd()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile.Achievements = new List<string>();
            w.RequiredProfile.Achievements.Add("first");
            w.RequiredProfile.Achievements.Add("second");
        });

    [Fact]
    public void RequiredProfile_Achievements_RemoveAt()
    {
        var seed = SeedSimple();
        seed.RequiredProfile.Achievements.AddRange(new[] { "a", "b", "c" });
        AssertRoundtrip(seed, w => w.RequiredProfile.Achievements.RemoveAt(1));
    }

    [Fact]
    public void RequiredProfile_Achievements_Clear()
    {
        var seed = SeedSimple();
        seed.RequiredProfile.Achievements.AddRange(new[] { "a", "b", "c" });
        AssertRoundtrip(seed, w => w.RequiredProfile.Achievements.Clear());
    }

    // ── OptionalProfile null transitions ──────────────────────────────
    [Fact]
    public void OptionalProfile_NullToObject_ViaSet()
        => AssertRoundtrip(SeedSimple(), w => w.OptionalProfile = new Profile
        {
            DisplayName = "Optional", Level = 1,
        });

    [Fact]
    public void OptionalProfile_ObjectToNull_ViaSet()
    {
        var seed = SeedSimple();
        seed.OptionalProfile = new Profile { DisplayName = "Existing", Level = 10 };
        AssertRoundtrip(seed, w => w.OptionalProfile = null);
    }

    [Fact]
    public void OptionalProfile_ObjectToObject_ViaSet()
    {
        var seed = SeedSimple();
        seed.OptionalProfile = new Profile { DisplayName = "Old", Level = 1 };
        AssertRoundtrip(seed, w => w.OptionalProfile = new Profile { DisplayName = "New", Level = 2 });
    }

    [Fact]
    public void OptionalProfile_FieldMutation_AfterSeeded()
    {
        var seed = SeedSimple();
        seed.OptionalProfile = new Profile { DisplayName = "Existing", Level = 1 };
        AssertRoundtrip(seed, w => w.OptionalProfile!.Level = 99);
    }

    [Fact]
    public void OptionalProfile_SetThenMutateFields()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.OptionalProfile = new Profile { DisplayName = "Fresh", Level = 0 };
            w.OptionalProfile!.DisplayName = "Renamed";
            w.OptionalProfile!.Level = 50;
        });

    [Fact]
    public void OptionalProfile_SetThenMutateNestedStats()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.OptionalProfile = new Profile { DisplayName = "Fresh", Level = 0, Stats = new Stats() };
            w.OptionalProfile!.Stats!.Hp = 500;
            w.OptionalProfile!.Stats!.Mp = 250;
        });

    [Fact]
    public void OptionalProfile_SetThenAddAchievement()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.OptionalProfile = new Profile { DisplayName = "Fresh", Level = 0 };
            w.OptionalProfile!.Achievements.Add("first-achievement");
        });

    // ── Combined sub-object + scalar mutations ─────────────────────────
    [Fact]
    public void Combined_RequiredProfile_AndScalar()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.Counter = 999;
            w.RequiredProfile.Level = 88;
            w.RequiredProfile.DisplayName = "Both";
        });

    [Fact]
    public void Combined_BothProfiles_OneCall()
        => AssertRoundtrip(SeedSimple(), w =>
        {
            w.RequiredProfile.Level = 10;
            w.OptionalProfile = new Profile { DisplayName = "Opt", Level = 20 };
        });
}
