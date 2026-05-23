using System.Collections.Generic;
using MemoryPack;
using MessagePack;
using Orleans;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    /// <summary>
    /// Player profile. Private-scoped — the owning player is the only subscriber.
    /// Other players talk to it via cross-entity calls (e.g. clan operations that need
    /// to query / mutate a player's score). Holds Money, Score (profile power), and the
    /// id of the clan the player currently belongs to (null when none).
    /// <para>
    /// Shape modelled after a typical mobile RPG profile: a roster of Heroes (each with
    /// stats and equipped gear), an Inventory of un-equipped items, history of completed
    /// campaign levels and opened chests. Serialized size of a "warmed-up" profile
    /// reaches 30–100 KB, which matches real-world meta backends and exercises
    /// MemoryPack throughput + grain persistence under realistic payload size.
    /// </para>
    /// </summary>
    [SharedState]
    [EntityScope(EntityScope.Private)]
    [MemoryPackable(GenerateType.VersionTolerant)]
    [GenerateSerializer]
    public partial class ProfileState : ISharedState
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int Score { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public int Money { get; set; } = 1000;
        [MemoryPackOrder(2), Id(2), Key(2)] public string? ClanId { get; set; }
        [MemoryPackOrder(3), Id(3), Key(3)] public List<string> PendingApplications { get; set; } = new();
        // Clans that accepted this player's application while the player was already in another
        // clan. Filled by ProfileService.OfferMembership (cross-entity Notification from
        // ClanService.AcceptApplication). The player decides later whether to switch to one of
        // these via AcceptInvitation (LeaveClan + ConfirmJoin under the hood).
        [MemoryPackOrder(4), Id(4), Key(4)] public List<string> ApprovedInvitations { get; set; } = new();

        // Roster of heroes the player owns. Each carries stats, equipped gear, level/XP.
        [MemoryPackOrder(5), Id(5), Key(5)] public List<HeroData> Heroes { get; set; } = new();

        // Inventory of un-equipped items. Items move between Inventory ↔ Hero.Equipped.
        [MemoryPackOrder(6), Id(6), Key(6)] public List<InventoryItem> Inventory { get; set; } = new();

        // History of completed campaign levels. Stars (1–3) per level.
        [MemoryPackOrder(7), Id(7), Key(7)] public List<CompletedLevel> CompletedLevels { get; set; } = new();

        // Chest open history with timestamp + tier. Cosmetic for stress-test but realistic.
        [MemoryPackOrder(8), Id(8), Key(8)] public List<ChestRecord> ChestHistory { get; set; } = new();

        // Soft currencies + tokens (energy, gems, raid tickets, event currency, etc).
        [MemoryPackOrder(9), Id(9), Key(9)] public Dictionary<string, int> Resources { get; set; } = new();

        // Monotonic counter used as a fresh id source for inventory items / chests / heroes.
        [MemoryPackOrder(10), Id(10), Key(10)] public int NextEntityId { get; set; } = 1;
    }

    /// <summary>A single hero in the roster with stats and equipped gear.</summary>
    [MemoryPackable(GenerateType.VersionTolerant)]
    [MessagePackObject]
    [GenerateSerializer]
    public partial class HeroData
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int HeroId { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public string Name { get; set; } = "";
        [MemoryPackOrder(2), Id(2), Key(2)] public string Class { get; set; } = "";   // "Warrior","Mage","Rogue","Cleric","Archer"
        [MemoryPackOrder(3), Id(3), Key(3)] public int Level { get; set; } = 1;
        [MemoryPackOrder(4), Id(4), Key(4)] public int Xp { get; set; }
        [MemoryPackOrder(5), Id(5), Key(5)] public int Stars { get; set; } = 1;        // 1-7
        [MemoryPackOrder(6), Id(6), Key(6)] public int Power { get; set; }
        [MemoryPackOrder(7), Id(7), Key(7)] public HeroStats Stats { get; set; } = new();
        // 6 gear slots: head, chest, legs, weapon, offhand, trinket. Sparse list — only filled slots.
        [MemoryPackOrder(8), Id(8), Key(8)] public List<EquippedItem> Equipped { get; set; } = new();
        [MemoryPackOrder(9), Id(9), Key(9)] public List<string> Skills { get; set; } = new();
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    public partial class HeroStats
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int Health { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public int Attack { get; set; }
        [MemoryPackOrder(2), Id(2), Key(2)] public int Defense { get; set; }
        [MemoryPackOrder(3), Id(3), Key(3)] public int Speed { get; set; }
        [MemoryPackOrder(4), Id(4), Key(4)] public int CritChance { get; set; }
        [MemoryPackOrder(5), Id(5), Key(5)] public int CritDamage { get; set; }
        [MemoryPackOrder(6), Id(6), Key(6)] public int Resistance { get; set; }
        [MemoryPackOrder(7), Id(7), Key(7)] public int Accuracy { get; set; }
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    public partial class EquippedItem
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int ItemId { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public string Slot { get; set; } = "";   // "head","chest","legs","weapon","offhand","trinket"
        [MemoryPackOrder(2), Id(2), Key(2)] public string Name { get; set; } = "";
        [MemoryPackOrder(3), Id(3), Key(3)] public int Tier { get; set; }            // 1..6
        [MemoryPackOrder(4), Id(4), Key(4)] public int Level { get; set; }
        [MemoryPackOrder(5), Id(5), Key(5)] public List<ItemAffix> Affixes { get; set; } = new();
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    public partial class InventoryItem
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int ItemId { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public string Name { get; set; } = "";
        [MemoryPackOrder(2), Id(2), Key(2)] public string Slot { get; set; } = "";
        [MemoryPackOrder(3), Id(3), Key(3)] public int Tier { get; set; }
        [MemoryPackOrder(4), Id(4), Key(4)] public int Level { get; set; }
        [MemoryPackOrder(5), Id(5), Key(5)] public List<ItemAffix> Affixes { get; set; } = new();
        [MemoryPackOrder(6), Id(6), Key(6)] public string? Description { get; set; }   // flavor text — realistic-size strings
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    public partial class ItemAffix
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public string Stat { get; set; } = "";    // "Attack","Health",...
        [MemoryPackOrder(1), Id(1), Key(1)] public int Value { get; set; }
        [MemoryPackOrder(2), Id(2), Key(2)] public int Tier { get; set; }
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    public partial class CompletedLevel
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public string LevelId { get; set; } = "";
        [MemoryPackOrder(1), Id(1), Key(1)] public int Stars { get; set; }            // 1..3
        [MemoryPackOrder(2), Id(2), Key(2)] public int BestScore { get; set; }
        [MemoryPackOrder(3), Id(3), Key(3)] public int Attempts { get; set; }
        [MemoryPackOrder(4), Id(4), Key(4)] public long LastPlayedTicks { get; set; }
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    public partial class ChestRecord
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int ChestId { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public int Tier { get; set; }            // 1..4 (common..legendary)
        [MemoryPackOrder(2), Id(2), Key(2)] public long OpenedTicks { get; set; }
        [MemoryPackOrder(3), Id(3), Key(3)] public List<int> ItemIdsAwarded { get; set; } = new();
        [MemoryPackOrder(4), Id(4), Key(4)] public int MoneyAwarded { get; set; }
    }

    /// <summary>Wire-safe profile summary returned by a Query method.</summary>
    [MemoryPackable]
    [GenerateSerializer]
    public partial class ProfileSummary
    {
        [MemoryPackOrder(0), Id(0)] public int Score { get; set; }
        [MemoryPackOrder(1), Id(1)] public int Money { get; set; }
        [MemoryPackOrder(2), Id(2)] public string? ClanId { get; set; }
        [MemoryPackOrder(3), Id(3)] public List<string>? ApprovedInvitations { get; set; }
        [MemoryPackOrder(4), Id(4)] public int HeroCount { get; set; }
        [MemoryPackOrder(5), Id(5)] public int InventoryCount { get; set; }
        [MemoryPackOrder(6), Id(6)] public int LevelsCompleted { get; set; }
    }
}
