using System.Collections.Generic;
using System.Linq;
using Xunit;
using static SharedMeta.PatchFuzzTests.PatchRoundtrip;

namespace SharedMeta.PatchFuzzTests;

/// <summary>
/// Realistic gameplay-shaped scenarios that exercise multiple code paths in a
/// single mutation lambda. Each test reads as a small game-loop snippet — the
/// kind of thing that would actually happen in a real meta service — and is
/// engineered to hit ≥4 of these patch-tree code paths at once:
///
///  - structural ops on the outer Heroes list (Add/Insert/RemoveAt/Set/Clear)
///  - element-subtree field mutations on existing Heroes
///  - structural ops on inner Equipment list inside an existing Hero
///  - element-subtree field mutations on existing Items inside Equipment
///  - mutations on the third level (Item.Tags scalar list)
///  - sub-object mutations (Hero.Stats fields, Profile fields)
///  - full-replace ops for collection reassignment
///
/// Their job is to catch interaction bugs that focused unit tests can't see —
/// e.g. "shifting an element child correctly while a deeper element subtree
/// has its own pending mutations" or "FullReplace on the outer list erasing
/// inner subtree state cleanly".
/// </summary>
public class ScenarioTests
{
    private static Hero NewHero(int id, string name, int hp = 100, int level = 1)
        => new() { Id = id, Name = name, Level = level, Hp = hp, Exp = 0, Stats = new Stats { Hp = hp, Mp = 50, Strength = 10, Agility = 10 } };

    private static Item NewItem(int id, string name, int power)
        => new() { ItemId = id, Name = name, Power = power };

    // ─────────────────────────────────────────────────────────────────
    // Combat scenarios
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Combat_AoeDamage_OneHeroDies_NewEnemyArrives()
    {
        // Three heroes take damage, the second one dies (RemoveAt 1), a new enemy
        // joins via Add. Survivors get Exp. Touches: outer list RemoveAt + Add,
        // element mutations on multiple existing elements, index shift on the
        // mutation that targeted hero[2] (which becomes hero[1] after the remove).
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 1), w =>
        {
            w.Heroes[0].Hp = 25;          // hero 0 takes damage
            w.Heroes[2].Hp = 80;          // hero 2 takes lighter damage
            w.Heroes.RemoveAt(1);          // hero 1 died — element child for [2] must shift to [1]
            w.Heroes[1].Exp += 50;         // hero 2 (now at idx 1) gains exp from the kill
            w.Heroes[0].Exp += 50;         // hero 0 also gains exp
            w.Heroes.Add(NewHero(99, "ReinforcementOrc", hp: 200));
        });
    }

    [Fact]
    public void Combat_BossEncounter_CritMissBuff()
    {
        // Boss hits the tank (hero 0): Hp -50, Stats.Agility -2.
        // Healer (hero 1): casts heal on tank — Mp -10, target Hp +30.
        // DPS (hero 2): crits — Exp +100, weapon power increment.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            // Tank takes hit
            w.Heroes[0].Hp -= 50;
            w.Heroes[0].Stats!.Agility -= 2;
            // Healer casts
            w.Heroes[1].Stats!.Mp -= 10;
            w.Heroes[0].Hp += 30;
            // DPS crits and weapon "wears" (Power--, then += from kill bonus)
            w.Heroes[2].Exp += 100;
            w.Heroes[2].Equipment[0].Power += 5;
            w.Heroes[2].Equipment[0].Tags.Add("blooded");
        });
    }

    [Fact]
    public void Combat_PartyWipe_RestartFromCheckpoint()
    {
        // Party wipes, server restores last checkpoint state for everyone:
        // wholesale-replaces Heroes with checkpoint snapshots, then immediately
        // applies the "respawn debuff" (Hp -10%, Exp -50) to each new instance.
        AssertRoundtrip(SeedWithHeroes(4, itemsPerHero: 2), w =>
        {
            w.Heroes = new List<Hero>
            {
                NewHero(1, "Tank",   hp: 200, level: 10),
                NewHero(2, "Healer", hp: 120, level: 10),
                NewHero(3, "DPS",    hp: 100, level: 10),
            };
            // Respawn debuff applied through the wrapper after the FullReplace op
            for (int i = 0; i < w.Heroes.Count; i++)
            {
                w.Heroes[i].Hp -= w.Heroes[i].Hp / 10;
                w.Heroes[i].Exp = -50;
                w.Heroes[i].Equipment.Add(NewItem(900 + i, "RespawnSickness", power: -5));
            }
        });
    }

    [Fact]
    public void Combat_MultiTargetSpell_WithCollateral()
    {
        // Spell hits heroes 0, 2, 4 of a party of 5. Hero 4 dies (RemoveAt 4),
        // hero 0 also loses an equipped charm (RemoveAt on inner list).
        // Hero 2 survives but its equipment burns (Power-- on every item).
        var seed = SeedWithHeroes(5, itemsPerHero: 3);
        AssertRoundtrip(seed, w =>
        {
            w.Heroes[0].Hp -= 30;
            w.Heroes[0].Equipment.RemoveAt(0);
            w.Heroes[2].Hp -= 50;
            for (int j = 0; j < w.Heroes[2].Equipment.Count; j++)
                w.Heroes[2].Equipment[j].Power -= 1;
            w.Heroes[4].Hp = 0;
            w.Heroes.RemoveAt(4);
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Inventory / shop scenarios
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Inventory_SellTwoBuyThree_UpgradeOneAddTagToAnother()
    {
        // Hero 0 sells items at indices 0 and 1, then buys 3 new items, then
        // upgrades Power on the 2nd new item, and tags one of the existing
        // (post-shift) items as "favorite".
        AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 4), w =>
        {
            var hero = w.Heroes[0];
            hero.Equipment.RemoveAt(0);                   // sells item @0
            hero.Equipment.RemoveAt(0);                   // sells (formerly) item @1
            hero.Equipment.Add(NewItem(101, "Sword", 30));
            hero.Equipment.Add(NewItem(102, "Shield", 20));
            hero.Equipment.Add(NewItem(103, "Potion", 5));
            // Newly bought Shield gets upgraded
            hero.Equipment[hero.Equipment.Count - 2].Power += 10;
            // The original item that survived the sell (was idx 2, now at 0) gets tagged
            hero.Equipment[0].Tags.Add("favorite");
        });
    }

    [Fact]
    public void Inventory_FullSwap_ViaSetEquipment()
    {
        // Hero swaps to a completely different loadout via SetEquipment, then
        // immediately starts modifying the new gear. Repro for the Set+mutate
        // bug class on a nested list.
        AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 2), w =>
        {
            w.Heroes[0].Equipment = new List<Item>
            {
                NewItem(201, "PvPSword", 50),
                NewItem(202, "PvPArmor", 40),
                NewItem(203, "PvPRing", 25),
            };
            w.Heroes[0].Equipment[0].Tags.Add("pvp-loadout");
            w.Heroes[0].Equipment[1].Power += 5;
            w.Heroes[0].Equipment[2].Tags.Add("legacy");
        });
    }

    [Fact]
    public void Inventory_Crafting_ConsumeMaterialsCreateItem()
    {
        // Hero crafts: removes 2 source items (consumed materials), adds 1 result
        // item, the result item has 3 tags applied immediately, and the hero gains
        // a crafting skill (SkillIds.Add) and exp.
        AssertRoundtrip(SeedWithHeroes(1, itemsPerHero: 4), w =>
        {
            var h = w.Heroes[0];
            h.Equipment.RemoveAt(2);  // consume material 1
            h.Equipment.RemoveAt(2);  // consume material 2 (now at idx 2 after first remove)
            h.Equipment.Add(NewItem(500, "EpicBlade", 100));
            var crafted = h.Equipment[h.Equipment.Count - 1];
            crafted.Tags.Add("crafted");
            crafted.Tags.Add("legendary");
            crafted.Tags.Add("masterwork");
            h.SkillIds.Add(99);
            h.Exp += 250;
        });
    }

    [Fact]
    public void Inventory_BulkClearAndRefill()
    {
        // Hero dumps everything (Equipment.Clear) and then refills from a fresh
        // template — tests that Clear's "drop element children" semantics interact
        // correctly with subsequent Add ops.
        AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 3), w =>
        {
            w.Heroes[1].Equipment.Clear();
            for (int i = 0; i < 5; i++)
                w.Heroes[1].Equipment.Add(NewItem(700 + i, $"Slot{i}", 10 + i));
            w.Heroes[1].Equipment[0].Tags.Add("primary");
            w.Heroes[1].Equipment[2].Power += 50;
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Progression scenarios
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LevelUp_StatsAndSkillsAndGearUpgrade()
    {
        // Hero 1 levels up: Level++, all stats increase, Hp restored to new max,
        // gains a skill, all existing gear gets a +1 power bonus, and a celebration
        // tag is added to the favorite weapon.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 3), w =>
        {
            var h = w.Heroes[1];
            h.Level += 1;
            h.Stats!.Hp += 20;
            h.Stats!.Mp += 10;
            h.Stats!.Strength += 2;
            h.Stats!.Agility += 1;
            h.Hp = h.Stats!.Hp;
            h.SkillIds.Add(h.Level * 100);
            for (int j = 0; j < h.Equipment.Count; j++)
                h.Equipment[j].Power += 1;
            h.Equipment[0].Tags.Add($"lvl-{h.Level}-bonus");
        });
    }

    [Fact]
    public void LevelUp_TwoHeroesSimultaneously_DifferentRewards()
    {
        AssertRoundtrip(SeedWithHeroes(4, itemsPerHero: 2), w =>
        {
            w.Heroes[1].Level += 1;
            w.Heroes[1].Stats!.Strength += 5;
            w.Heroes[1].Equipment[0].Power += 10;

            w.Heroes[3].Level += 2;
            w.Heroes[3].Stats!.Mp += 30;
            w.Heroes[3].SkillIds.Add(777);
            w.Heroes[3].Equipment[1].Tags.Add("lvl-up-reward");
        });
    }

    [Fact]
    public void Quest_TurnIn_Rewards_ProfileBumps()
    {
        // Player turns in a quest — multiple heroes get exp, profile gets a level
        // bump and an achievement, optional profile (party leader bio) gets set
        // for the first time, top-level scalar Counter increments.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 1), w =>
        {
            for (int i = 0; i < w.Heroes.Count; i++)
                w.Heroes[i].Exp += 250;

            w.RequiredProfile.Level += 1;
            w.RequiredProfile.Achievements.Add("first-quest-done");
            w.RequiredProfile.Stats = new Stats { Hp = 100, Mp = 50, Strength = 25, Agility = 10 };

            w.OptionalProfile = new Profile
            {
                DisplayName = "Quest Hero",
                Level = w.RequiredProfile.Level,
                Stats = new Stats { Hp = 1, Mp = 1 },
                Achievements = new List<string> { "starter" },
            };

            w.Counter += 1;
            w.Timestamp = 1_700_000_000_000;
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Party management scenarios
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Party_RerollEveryone_PerHeroSetup()
    {
        // SetHeroes wholesale-replaces the party, then for each new hero we set
        // up its Stats, drop in starter equipment, and grant a starter skill.
        // This is a textbook reassign-then-mutate pattern at multiple levels.
        AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 1), w =>
        {
            w.Heroes = new List<Hero>
            {
                NewHero(1, "Reroll-A", level: 1),
                NewHero(2, "Reroll-B", level: 1),
                NewHero(3, "Reroll-C", level: 1),
                NewHero(4, "Reroll-D", level: 1),
            };

            for (int i = 0; i < w.Heroes.Count; i++)
            {
                var h = w.Heroes[i];
                h.Stats!.Strength = 10 + i;
                h.Stats!.Agility = 10 + i;
                h.Equipment.Add(NewItem(i * 100, "StarterWeapon", 5));
                h.Equipment.Add(NewItem(i * 100 + 1, "StarterArmor", 3));
                h.SkillIds.Add(i + 1);
            }
        });
    }

    [Fact]
    public void Party_DismissDeadAndPromote()
    {
        // Five heroes; 0 and 2 are dead (Hp == 0). Server cleans them up via
        // RemoveAll-equivalent (we mutate explicitly to keep determinism), then
        // the surviving heroes get a promotion: Level += 1, Exp gain, new tag on
        // their primary weapon, and a new "veteran" skill.
        var seed = SeedWithHeroes(5, itemsPerHero: 2);
        seed.Heroes[0].Hp = 0;
        seed.Heroes[2].Hp = 0;
        AssertRoundtrip(seed, w =>
        {
            // Remove from highest index first to avoid shifting effects on lower idx
            w.Heroes.RemoveAt(2);
            w.Heroes.RemoveAt(0);

            for (int i = 0; i < w.Heroes.Count; i++)
            {
                var h = w.Heroes[i];
                h.Level += 1;
                h.Exp += 100;
                h.Equipment[0].Tags.Add("veteran");
                h.SkillIds.Add(9999);
            }
        });
    }

    [Fact]
    public void Party_RecruitAndKickInOneCall()
    {
        // Mid-game shuffle: kick out underleveled hero at idx 1, recruit two new
        // heroes via Insert at the start (indices 0 and 1), buff the existing
        // hero who used to be at idx 2 (now at idx 3 after the two inserts and
        // one removal), and rename the recruit at idx 0.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 1), w =>
        {
            w.Heroes.RemoveAt(1);
            w.Heroes.Insert(0, NewHero(101, "Recruit1", hp: 80));
            w.Heroes.Insert(1, NewHero(102, "Recruit2", hp: 90));
            // Now: [Recruit1, Recruit2, original-hero-0, original-hero-2]
            w.Heroes[3].Hp += 50;
            w.Heroes[3].Equipment[0].Power += 5;
            w.Heroes[0].Name = "Recruit1-Renamed";
        });
    }

    [Fact]
    public void Party_ReplaceMiddleHero_PreserveOthers()
    {
        // Indexer set on a middle hero (Heroes[2] = ...) replaces wholesale, but
        // the surrounding heroes get mutated in the same call. Tests Set op
        // behaviour vs neighbouring element children.
        AssertRoundtrip(SeedWithHeroes(5, itemsPerHero: 2), w =>
        {
            w.Heroes[1].Exp += 100;
            w.Heroes[2] = NewHero(999, "Replacement", hp: 150, level: 20);
            w.Heroes[3].Exp += 100;
            w.Heroes[3].Equipment[0].Power += 1;
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Mass-update / save scenarios
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_DailyTick_AllHeroesRegen()
    {
        // Daily tick: every hero regenerates Hp, every item that's a "consumable"
        // tagged item gets its Power restored, profile counter increments, the
        // top-level Numbers list is appended to (audit log).
        AssertRoundtrip(SeedWithHeroes(4, itemsPerHero: 3), w =>
        {
            for (int i = 0; i < w.Heroes.Count; i++)
            {
                var h = w.Heroes[i];
                h.Hp = h.Stats!.Hp;
                for (int j = 0; j < h.Equipment.Count; j++)
                    h.Equipment[j].Power += 1;
            }
            w.RequiredProfile.Level += 0; // no-op writeback to test idempotency
            w.RequiredProfile.Achievements.Add("daily-2026-04-09");
            w.Numbers.Add(1);
            w.Numbers.Add(2);
            w.Numbers.Add(3);
            w.Counter += 1;
        });
    }

    [Fact]
    public void Save_WeeklyReset_ClearProgressKeepGear()
    {
        // Weekly reset: clear achievements, clear flags, reset counters dict, but
        // KEEP equipment and stats. Then immediately re-grant a starter
        // achievement and seed the new week's flags.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            w.RequiredProfile.Achievements.Clear();
            w.Counters = new Dictionary<string, int>();
            w.Flags = new HashSet<int>();
            // Reseed
            w.RequiredProfile.Achievements.Add("week-start");
            w.Counters["weekly_quests_remaining"] = 7;
            w.Flags.Add(1);
            w.Flags.Add(2);
            // Heroes still get a small bonus
            for (int i = 0; i < w.Heroes.Count; i++)
                w.Heroes[i].Exp += 10;
        });
    }

    [Fact]
    public void Save_FullProfileSnapshot_AndPartyShuffle()
    {
        // Server pushes a fresh profile snapshot (Set), then mutates the new
        // snapshot in place AND reshuffles the party order via a series of
        // structural ops, AND mutates surviving heroes — this is the densest
        // single-call test in the suite.
        AssertRoundtrip(SeedWithHeroes(4, itemsPerHero: 2), w =>
        {
            // Profile snapshot push + in-place mutation
            w.RequiredProfile = new Profile
            {
                DisplayName = "Snapshot",
                Level = 50,
                Stats = new Stats { Hp = 500, Mp = 250, Strength = 50, Agility = 50 },
                Achievements = new List<string> { "snapshot-pushed" },
            };
            w.RequiredProfile.Level += 1;
            w.RequiredProfile.Stats!.Strength += 5;
            w.RequiredProfile.Achievements.Add("post-snapshot-bonus");

            // Party shuffle
            w.Heroes[1].Exp += 500;
            w.Heroes[2].Equipment[0].Power += 10;
            w.Heroes.RemoveAt(0); // drop the leader
            w.Heroes.Insert(0, NewHero(99, "NewLeader", hp: 300));      // promote a new one
            w.Heroes[2].Equipment.Add(NewItem(8888, "PromoBlade", 75));
            w.Heroes[3].Stats!.Agility += 3;


            // Audit log entries
            w.Counter += 1;
            w.Numbers.Add(50);
            w.Counters["last_snapshot_seq"] = 1_700_000;
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Sort / reorder scenarios — FullReplace path on outer + element churn
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Sort_HeroesByLevel_ViaReassign()
    {
        // Sort by Level descending via a reassignment (we don't have Sort on
        // HeroPatchableList — the realistic pattern is to compute the new ordering
        // and SetHeroes(...) it). Then mutate the now-top hero.
        AssertRoundtrip(SeedWithHeroes(5, itemsPerHero: 1), w =>
        {
            var sorted = w.Heroes.Select(h => h.Raw).OrderByDescending(h => h.Level).ToList();
            w.Heroes = sorted;
            w.Heroes[0].Exp += 1000;
            w.Heroes[0].Equipment[0].Tags.Add("party-leader");
        });
    }

    [Fact]
    public void Sort_NumbersThenAppendAndRemove()
    {
        // Sort the Numbers list, append a few, then remove the smallest.
        AssertRoundtrip(SeedWithNumbers(7, 2, 9, 1, 4, 8, 3), w =>
        {
            w.Numbers.Sort();
            w.Numbers.Add(100);
            w.Numbers.Add(101);
            w.Numbers.RemoveAt(0); // drop smallest
        });
    }

    [Fact]
    public void Reorder_ReverseAndMutate()
    {
        // Reverse the Numbers list (FullReplace under the hood) and immediately
        // start indexer-setting on it.
        AssertRoundtrip(SeedWithNumbers(1, 2, 3, 4, 5), w =>
        {
            w.Numbers.Reverse();
            w.Numbers[0] = 999;
            w.Numbers[w.Numbers.Count - 1] = -999;
            w.Numbers.Add(0);
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // Rare interaction patterns
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rare_MutateThenRemoveSameElement()
    {
        // Mutate hero 1's fields, then RemoveAt(1) — the element subtree at idx 1
        // must be dropped cleanly, no leftover children. Hero 2 (now at idx 1)
        // gets its own mutation after the remove to prove the slot is reusable.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            w.Heroes[1].Hp = 999;
            w.Heroes[1].Exp = 999;
            w.Heroes[1].Equipment[0].Power = 999;
            w.Heroes.RemoveAt(1);
            w.Heroes[1].Hp = 1;  // hero originally at idx 2, now at idx 1
        });
    }

    [Fact]
    public void Rare_InsertAtIndexThenMutateInsertedAtSameIndex()
    {
        // Insert a new hero at idx 1, then immediately mutate it via the same
        // index. The element child must be addressable at the new post-insert
        // position even though it didn't exist before this call.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 1), w =>
        {
            w.Heroes.Insert(1, NewHero(999, "Inserted", hp: 50));
            w.Heroes[1].Hp += 25;
            w.Heroes[1].Equipment.Add(NewItem(8000, "GiftedBlade", 30));
            w.Heroes[1].Equipment[0].Tags.Add("welcome-gift");
        });
    }

    [Fact]
    public void Rare_DoubleReassignWithMutationsBetween()
    {
        // Reassign Heroes, mutate, reassign again, mutate again. The second
        // reassign must drop the first reassign's element children cleanly.
        AssertRoundtrip(SeedWithHeroes(2, itemsPerHero: 1), w =>
        {
            w.Heroes = new List<Hero>
            {
                NewHero(1, "First-Wave-A"),
                NewHero(2, "First-Wave-B"),
            };
            w.Heroes[0].Exp = 100;
            w.Heroes[1].Exp = 200;

            w.Heroes = new List<Hero>
            {
                NewHero(10, "Second-Wave-A"),
                NewHero(20, "Second-Wave-B"),
                NewHero(30, "Second-Wave-C"),
            };
            w.Heroes[2].Hp = 50;
            w.Heroes[0].Equipment.Add(NewItem(1, "Late-Add", 10));
        });
    }

    [Fact]
    public void Rare_OuterStructuralWhileInnerStructural()
    {
        // Simultaneously mutate the outer Heroes list AND the inner Equipment
        // list of a hero whose index is changing.
        AssertRoundtrip(SeedWithHeroes(4, itemsPerHero: 3), w =>
        {
            // Mutate hero[3]'s equipment first (element child @ outer idx 3)
            w.Heroes[3].Equipment.Add(NewItem(8001, "PreShift", 20));
            w.Heroes[3].Equipment[0].Power += 5;
            w.Heroes[3].Equipment[1].Tags.Add("pre-shift");

            // Now structural op on the outer list — hero 3 should shift to idx 2
            w.Heroes.RemoveAt(0);

            // After the shift, mutate the same hero (now at idx 2) again
            w.Heroes[2].Equipment.Add(NewItem(8002, "PostShift", 30));
            w.Heroes[2].Hp -= 10;
        });
    }

    [Fact]
    public void Rare_EveryDomainTouched()
    {
        // Single mutation touching every domain in FuzzState — scalars, both
        // profiles, all three scalar lists, the Heroes element list with element
        // mutations, nested Equipment with item mutations, three-level Tags,
        // SkillIds, dict, hashset. If anything in any domain regresses, this
        // single test will fail.
        AssertRoundtrip(SeedWithHeroes(3, itemsPerHero: 2), w =>
        {
            // Scalars
            w.Counter += 1;
            w.Timestamp = 1_700_000_002_000;
            w.Name = "every-domain";
            w.Active = true;
            w.Kind = FuzzKind.Gamma;
            w.Blob = new byte[] { 1, 2, 3 };

            // Required profile + nested (seed Stats since SeedSimple leaves it null)
            w.RequiredProfile.DisplayName = "Updated";
            w.RequiredProfile.Level += 1;
            w.RequiredProfile.Stats = new Stats { Hp = 1, Mp = 2, Strength = 3, Agility = 4 };
            w.RequiredProfile.Stats!.Strength += 1;
            w.RequiredProfile.Achievements.Add("touched");

            // Optional profile null→object
            w.OptionalProfile = new Profile { DisplayName = "Opt", Level = 5 };
            w.OptionalProfile!.Achievements.Add("opt-touched");

            // Scalar lists
            w.Numbers.Add(42);
            w.Tags.Add("hello");
            w.Buffer.Add(0xAB);

            // Heroes — structural + element + nested
            w.Heroes.Add(NewHero(99, "NewArrival"));
            w.Heroes.RemoveAt(0);
            w.Heroes[0].Hp += 10;
            w.Heroes[0].Equipment.Add(NewItem(900, "Trinket", 5));
            w.Heroes[0].Equipment[0].Power += 1;
            w.Heroes[0].Equipment[0].Tags.Add("touched");
            w.Heroes[0].SkillIds.Add(123);
            w.Heroes[1].Stats!.Hp += 5;

            // Dict + Set
            w.Counters["touched"] = 1;
            w.Flags.Add(7);
        });
    }
}
