using System.Collections.Generic;
using MemoryPack;
using Orleans;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    /// <summary>
    /// Clan state. Shared-scoped — multiple players (the members) all subscribe to the
    /// same instance. First subscriber establishes the config-version pin, and that pin
    /// drives the asymmetric force-patch decision for joiners on a different config branch.
    /// <para>
    /// Schema 2 is gated by ClanConfig &gt;= 2.0 — when a v2 client creates the clan, the
    /// migration to schema 2 runs immediately. v1 clients later subscribing to that clan
    /// (pinned at config 2.0) get force-patch on the IClanService surface because the
    /// <c>[MetaConfigStructureBoundary("2.0")]</c> declaration on ClanConfig kicks in.
    /// </para>
    /// </summary>
    [SharedState]
    [EntityScope(EntityScope.Shared)]
    [MetaStateVersion(2, "2.0", typeof(ClanConfig))]
    [MemoryPackable(GenerateType.VersionTolerant)]
    [GenerateSerializer]
    public partial class ClanState : ISharedState
    {
        [MemoryPackOrder(0), Id(0)] public string Name { get; set; } = "";
        [MemoryPackOrder(1), Id(1)] public string LeaderId { get; set; } = "";
        [MemoryPackOrder(2), Id(2)] public List<string> Members { get; set; } = new();
        [MemoryPackOrder(3), Id(3)] public List<string> Applications { get; set; } = new();
        [MemoryPackOrder(4), Id(4)] public int Power { get; set; }

        // ── v2-additive fields (schema 2 introduces these via [MetaInit] migration) ──
        [MemoryPackOrder(5), Id(5)] public List<string> Officers { get; set; } = new();
        [MemoryPackOrder(6), Id(6)] public List<FriendshipEntry> Friendships { get; set; } = new();
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    [GenerateSerializer]
    public partial class FriendshipEntry
    {
        [MemoryPackOrder(0), Id(0)] public string OtherClanId { get; set; } = "";
        [MemoryPackOrder(1), Id(1)] public FriendshipStatus Status { get; set; }
    }

    public enum FriendshipStatus
    {
        Pending = 0,
        Active = 1,
    }

    /// <summary>Wire-safe clan summary for queries / recommendations.</summary>
    [MemoryPackable]
    [GenerateSerializer]
    public partial class ClanSummary
    {
        [MemoryPackOrder(0), Id(0)] public string ClanId { get; set; } = "";
        [MemoryPackOrder(1), Id(1)] public string Name { get; set; } = "";
        [MemoryPackOrder(2), Id(2)] public int MemberCount { get; set; }
        [MemoryPackOrder(3), Id(3)] public int Power { get; set; }
    }
}
