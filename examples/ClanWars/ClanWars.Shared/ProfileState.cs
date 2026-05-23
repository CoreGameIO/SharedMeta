using System.Collections.Generic;
using MemoryPack;
using Orleans;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    /// <summary>
    /// Player profile. Private-scoped — the owning player is the only subscriber.
    /// Other players talk to it via cross-entity calls (e.g. clan operations that need
    /// to query / mutate a player's score). Holds Money, Score (profile power), and the
    /// id of the clan the player currently belongs to (null when none).
    /// </summary>
    [SharedState]
    [EntityScope(EntityScope.Private)]
    [MemoryPackable(GenerateType.VersionTolerant)]
    [GenerateSerializer]
    public partial class ProfileState : ISharedState
    {
        [MemoryPackOrder(0), Id(0)] public int Score { get; set; }
        [MemoryPackOrder(1), Id(1)] public int Money { get; set; } = 1000;
        [MemoryPackOrder(2), Id(2)] public string? ClanId { get; set; }
        [MemoryPackOrder(3), Id(3)] public List<string> PendingApplications { get; set; } = new();
        // Clans that accepted this player's application while the player was already in another
        // clan. Filled by ProfileService.OfferMembership (cross-entity Notification from
        // ClanService.AcceptApplication). The player decides later whether to switch to one of
        // these via AcceptInvitation (LeaveClan + ConfirmJoin under the hood).
        [MemoryPackOrder(4), Id(4)] public List<string> ApprovedInvitations { get; set; } = new();
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
    }
}
