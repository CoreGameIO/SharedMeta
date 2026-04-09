using System;
using System.Collections.Generic;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Property-based fuzz tests. Each [InlineData] seed picks a different
/// random op stream; xUnit treats every seed as an independent test, so
/// regressions surface as a list of "fuzz seed N failed" entries which
/// can be reproduced deterministically by re-running with that seed.
///
/// Two flavors:
///   - <see cref="ScalarListFuzz"/> exercises the PatchableList&lt;T&gt; op
///     stream (the largest surface area)
///   - <see cref="HeroListFuzz"/> exercises element-sub-wrappable lists
///     with element-subtree mutations interleaved with structural ops
///   - <see cref="DeepFuzz"/> walks the entire FuzzState shape, including
///     dict/hashset/sub-objects/nested lists, with all op kinds
/// </summary>
public class FuzzTests
{
    // ── Scalar list fuzz (List<int>) ─────────────────────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    public void ScalarListFuzz(int seed)
    {
        var rng = new Random(seed);
        var initialSize = rng.Next(0, 8);
        var initial = new int[initialSize];
        for (int i = 0; i < initialSize; i++) initial[i] = rng.Next(-100, 100);

        var state = SeedWithNumbers(initial);
        var opCount = rng.Next(5, 25);

        AssertRoundtrip(state, w =>
        {
            for (int i = 0; i < opCount; i++)
            {
                var op = rng.Next(0, 9);
                switch (op)
                {
                    case 0: // Add
                        w.Numbers.Add(rng.Next(-1000, 1000));
                        break;
                    case 1: // Insert
                        if (w.Numbers.Count > 0)
                            w.Numbers.Insert(rng.Next(0, w.Numbers.Count + 1), rng.Next(-1000, 1000));
                        else
                            w.Numbers.Add(rng.Next(-1000, 1000));
                        break;
                    case 2: // RemoveAt
                        if (w.Numbers.Count > 0) w.Numbers.RemoveAt(rng.Next(0, w.Numbers.Count));
                        break;
                    case 3: // IndexerSet
                        if (w.Numbers.Count > 0) w.Numbers[rng.Next(0, w.Numbers.Count)] = rng.Next(-1000, 1000);
                        break;
                    case 4: // Clear
                        w.Numbers.Clear();
                        break;
                    case 5: // Sort
                        w.Numbers.Sort();
                        break;
                    case 6: // Reverse
                        w.Numbers.Reverse();
                        break;
                    case 7: // Reassign empty
                        w.Numbers = new List<int>();
                        break;
                    case 8: // Reassign with content
                        var fresh = new List<int>();
                        for (int k = 0; k < rng.Next(0, 6); k++) fresh.Add(rng.Next(-1000, 1000));
                        w.Numbers = fresh;
                        break;
                }
            }
        });
    }

    // ── Hero list fuzz (element-sub-wrappable) ───────────────────────
    [Theory]
    [InlineData(101)]
    [InlineData(102)]
    [InlineData(103)]
    [InlineData(104)]
    [InlineData(105)]
    [InlineData(106)]
    [InlineData(107)]
    [InlineData(108)]
    [InlineData(109)]
    [InlineData(110)]
    [InlineData(111)]
    [InlineData(112)]
    [InlineData(113)]
    [InlineData(114)]
    [InlineData(115)]
    [InlineData(116)]
    [InlineData(117)]
    [InlineData(118)]
    [InlineData(119)]
    [InlineData(120)]
    public void HeroListFuzz(int seed)
    {
        var rng = new Random(seed);
        var initialHeroes = rng.Next(0, 6);
        var state = SeedWithHeroes(initialHeroes, itemsPerHero: rng.Next(0, 3));
        var opCount = rng.Next(5, 30);

        AssertRoundtrip(state, w =>
        {
            for (int i = 0; i < opCount; i++)
            {
                var op = rng.Next(0, 11);
                switch (op)
                {
                    case 0: // Add hero
                        w.Heroes.Add(new Hero
                        {
                            Id = rng.Next(1000, 9999),
                            Name = $"FuzzHero{i}",
                            Level = rng.Next(1, 100),
                            Hp = rng.Next(1, 1000),
                            Exp = rng.Next(0, 10000),
                        });
                        break;
                    case 1: // Insert hero
                        var insertIdx = w.Heroes.Count == 0 ? 0 : rng.Next(0, w.Heroes.Count + 1);
                        w.Heroes.Insert(insertIdx, new Hero
                        {
                            Id = rng.Next(1000, 9999),
                            Name = $"InsHero{i}",
                            Hp = 100,
                        });
                        break;
                    case 2: // RemoveAt
                        if (w.Heroes.Count > 0) w.Heroes.RemoveAt(rng.Next(0, w.Heroes.Count));
                        break;
                    case 3: // Replace via indexer
                        if (w.Heroes.Count > 0)
                            w.Heroes[rng.Next(0, w.Heroes.Count)] = new Hero
                            {
                                Id = rng.Next(1000, 9999),
                                Name = $"Repl{i}",
                                Hp = 100,
                            };
                        break;
                    case 4: // Mutate hero field
                        if (w.Heroes.Count > 0)
                            w.Heroes[rng.Next(0, w.Heroes.Count)].Hp = rng.Next(1, 1000);
                        break;
                    case 5: // Mutate Exp
                        if (w.Heroes.Count > 0)
                            w.Heroes[rng.Next(0, w.Heroes.Count)].Exp = rng.Next(0, 10000);
                        break;
                    case 6: // Mutate Name
                        if (w.Heroes.Count > 0)
                            w.Heroes[rng.Next(0, w.Heroes.Count)].Name = $"Renamed{i}";
                        break;
                    case 7: // Add to SkillIds
                        if (w.Heroes.Count > 0)
                            w.Heroes[rng.Next(0, w.Heroes.Count)].SkillIds.Add(rng.Next(1, 100));
                        break;
                    case 8: // Mutate Stats.Hp
                        if (w.Heroes.Count > 0)
                        {
                            var h = w.Heroes[rng.Next(0, w.Heroes.Count)];
                            if (h.Stats != null) h.Stats.Hp = rng.Next(1, 9999);
                            else h.Stats = new Stats { Hp = rng.Next(1, 9999) };
                        }
                        break;
                    case 9: // Clear
                        w.Heroes.Clear();
                        break;
                    case 10: // Reassign
                        var fresh = new List<Hero>();
                        for (int k = 0; k < rng.Next(0, 4); k++)
                            fresh.Add(new Hero { Id = k, Name = $"Re{k}", Hp = 100 });
                        w.Heroes = fresh;
                        break;
                }
            }
        });
    }

    // ── Deep fuzz: every code path in one test ───────────────────────
    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(203)]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(206)]
    [InlineData(207)]
    [InlineData(208)]
    [InlineData(209)]
    [InlineData(210)]
    [InlineData(211)]
    [InlineData(212)]
    [InlineData(213)]
    [InlineData(214)]
    [InlineData(215)]
    [InlineData(216)]
    [InlineData(217)]
    [InlineData(218)]
    [InlineData(219)]
    [InlineData(220)]
    [InlineData(221)]
    [InlineData(222)]
    [InlineData(223)]
    [InlineData(224)]
    [InlineData(225)]
    [InlineData(226)]
    [InlineData(227)]
    [InlineData(228)]
    [InlineData(229)]
    [InlineData(230)]
    public void DeepFuzz(int seed)
    {
        var rng = new Random(seed);
        var state = SeedWithHeroes(rng.Next(0, 5), itemsPerHero: rng.Next(0, 4));
        var opCount = rng.Next(10, 40);

        AssertRoundtrip(state, w =>
        {
            for (int i = 0; i < opCount; i++)
            {
                var domain = rng.Next(0, 7);
                switch (domain)
                {
                    case 0: ScalarOp(rng, w); break;
                    case 1: NumbersOp(rng, w); break;
                    case 2: HeroesOp(rng, w, i); break;
                    case 3: ProfileOp(rng, w); break;
                    case 4: DictOp(rng, w, i); break;
                    case 5: SetOp(rng, w); break;
                    case 6: NestedOp(rng, w, i); break;
                }
            }
        });
    }

    private static void ScalarOp(Random rng, FuzzStatePatchWrapper w)
    {
        switch (rng.Next(0, 6))
        {
            case 0: w.Counter = rng.Next(); break;
            case 1: w.Timestamp = (long)rng.Next() * (rng.Next(2) == 0 ? 1 : -1); break;
            case 2: w.Name = $"name-{rng.Next(0, 1000)}"; break;
            case 3: w.Active = rng.Next(2) == 0; break;
            case 4: w.Kind = (FuzzKind)rng.Next(0, 4); break;
            case 5:
                var bytes = new byte[rng.Next(0, 16)];
                rng.NextBytes(bytes);
                w.Blob = bytes;
                break;
        }
    }

    private static void NumbersOp(Random rng, FuzzStatePatchWrapper w)
    {
        switch (rng.Next(0, 7))
        {
            case 0: w.Numbers.Add(rng.Next(-100, 100)); break;
            case 1:
                if (w.Numbers.Count > 0) w.Numbers.RemoveAt(rng.Next(0, w.Numbers.Count));
                break;
            case 2:
                if (w.Numbers.Count > 0) w.Numbers[rng.Next(0, w.Numbers.Count)] = rng.Next();
                break;
            case 3: w.Numbers.Clear(); break;
            case 4: w.Numbers.Sort(); break;
            case 5:
                if (w.Numbers.Count > 0) w.Numbers.Insert(rng.Next(0, w.Numbers.Count + 1), rng.Next());
                break;
            case 6:
                w.Numbers = new List<int> { rng.Next(), rng.Next() };
                break;
        }
    }

    private static void HeroesOp(Random rng, FuzzStatePatchWrapper w, int seq)
    {
        switch (rng.Next(0, 8))
        {
            case 0:
                w.Heroes.Add(new Hero { Id = seq, Name = $"H{seq}", Hp = 100 });
                break;
            case 1:
                if (w.Heroes.Count > 0)
                    w.Heroes.Insert(rng.Next(0, w.Heroes.Count + 1), new Hero { Id = seq, Hp = 100 });
                break;
            case 2:
                if (w.Heroes.Count > 0) w.Heroes.RemoveAt(rng.Next(0, w.Heroes.Count));
                break;
            case 3:
                if (w.Heroes.Count > 0) w.Heroes[rng.Next(0, w.Heroes.Count)].Hp = rng.Next(1, 1000);
                break;
            case 4:
                if (w.Heroes.Count > 0) w.Heroes[rng.Next(0, w.Heroes.Count)].Exp += rng.Next(1, 100);
                break;
            case 5: w.Heroes.Clear(); break;
            case 6:
                if (w.Heroes.Count > 0)
                    w.Heroes[rng.Next(0, w.Heroes.Count)] = new Hero { Id = seq + 9000, Hp = 50 };
                break;
            case 7:
                w.Heroes = new List<Hero> { new() { Id = seq, Hp = 100 } };
                break;
        }
    }

    private static void ProfileOp(Random rng, FuzzStatePatchWrapper w)
    {
        switch (rng.Next(0, 6))
        {
            case 0: w.RequiredProfile.DisplayName = $"P{rng.Next()}"; break;
            case 1: w.RequiredProfile.Level = rng.Next(0, 100); break;
            case 2:
                w.RequiredProfile.Stats = new Stats { Hp = rng.Next(1, 1000) };
                break;
            case 3:
                if (w.RequiredProfile.Stats != null)
                    w.RequiredProfile.Stats.Hp = rng.Next(1, 1000);
                break;
            case 4:
                w.RequiredProfile.Achievements.Add($"ach-{rng.Next(0, 100)}");
                break;
            case 5:
                w.OptionalProfile = rng.Next(2) == 0 ? null : new Profile { DisplayName = "Opt", Level = rng.Next(0, 50) };
                break;
        }
    }

    private static void DictOp(Random rng, FuzzStatePatchWrapper w, int seq)
    {
        switch (rng.Next(0, 5))
        {
            case 0: w.Counters[$"k{seq}"] = rng.Next(); break;
            case 1:
                if (w.Counters.Count > 0)
                {
                    foreach (var k in w.Counters.Keys)
                    {
                        w.Counters.Remove(k);
                        break;
                    }
                }
                break;
            case 2: w.Counters.Clear(); break;
            case 3: w.Counters = new Dictionary<string, int> { ["x"] = 1 }; break;
            case 4: w.Counters = new Dictionary<string, int>(); break;
        }
    }

    private static void SetOp(Random rng, FuzzStatePatchWrapper w)
    {
        switch (rng.Next(0, 5))
        {
            case 0: w.Flags.Add(rng.Next(0, 100)); break;
            case 1:
                if (w.Flags.Count > 0)
                {
                    foreach (var v in w.Flags)
                    {
                        w.Flags.Remove(v);
                        break;
                    }
                }
                break;
            case 2: w.Flags.Clear(); break;
            case 3: w.Flags = new HashSet<int> { 1, 2, 3 }; break;
            case 4: w.Flags = new HashSet<int>(); break;
        }
    }

    private static void NestedOp(Random rng, FuzzStatePatchWrapper w, int seq)
    {
        if (w.Heroes.Count == 0) return;
        var h = w.Heroes[rng.Next(0, w.Heroes.Count)];
        switch (rng.Next(0, 7))
        {
            case 0:
                h.Equipment.Add(new Item { ItemId = seq, Name = $"It{seq}", Power = rng.Next(1, 100) });
                break;
            case 1:
                if (h.Equipment.Count > 0) h.Equipment.RemoveAt(rng.Next(0, h.Equipment.Count));
                break;
            case 2:
                if (h.Equipment.Count > 0) h.Equipment[rng.Next(0, h.Equipment.Count)].Power = rng.Next(1, 999);
                break;
            case 3:
                if (h.Equipment.Count > 0) h.Equipment[rng.Next(0, h.Equipment.Count)].Tags.Add($"t{seq}");
                break;
            case 4: h.SkillIds.Add(rng.Next(1, 100)); break;
            case 5: h.Equipment.Clear(); break;
            case 6: h.Equipment = new List<Item> { new() { ItemId = seq, Name = "fresh" } }; break;
        }
    }
}
