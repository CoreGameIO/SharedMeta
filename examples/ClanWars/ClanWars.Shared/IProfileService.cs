using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    public enum ApplyResult
    {
        Ok,
        NoClan,
        AlreadyIn,
        Contains
    }

    // PatchTracking = false: this service's impl body isn't patch-copy-compatible (FindHero
    // returns raw HeroData, and EquipItem/UnequipItem do `Affixes = item.Affixes`, leaking a
    // wrapper collection into a raw List<> field — which the deliberate no-wrapper→raw rule
    // forbids). Rather than write it in the copy-compatible style (wrapper-typed helpers + no
    // raw-field leaks, see PartyService), the example opts out: force-patch clients are rejected
    // at negotiation instead of mis-served. (The ClanConfig 2.0 boundary still force-patches
    // IClanService, whose body IS copy-compatible.)
    // Deliberately kept on the legacy ConfigType (obsolete but functional), same reasoning as
    // IClanService — no xUnit coverage for ClanWars, and shares ClanConfig (which carries
    // [MetaConfigStructureBoundary("2.0")]) with IClanService, so migrate both together once
    // that's verified.
#pragma warning disable CS0618
    [MetaService(StateType = typeof(ProfileState), ConfigType = typeof(ClanConfig), PatchTracking = false)]
#pragma warning restore CS0618
    public interface IProfileService : IMetaService
    {
        /// <summary>Award bonus power to the player. Forwarded to the clan (if any).</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        ValueTask GainPoints(int amount);

        /// <summary>Spend money + create a new clan grain. Returns the new clan id on success.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<string?> CreateClan(string name);

        /// <summary>Submit an application to join a clan. Cross-entity call into ClanService.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<ApplyResult> ApplyToClan(string clanId);

        /// <summary>Leave the current clan. Player's score is subtracted from clan power.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> LeaveClan();

        /// <summary>
        /// Server-driven recommendation list — pulls non-full clans (any pinned config version)
        /// from the silo's <c>ClanContainerService</c> cache. The cluster-wide list spans BOTH
        /// v1- and v2-pinned clans, so v1 callers will naturally see v2-pinned candidates and
        /// vice versa — the engine of the cross-version subscription mix that drives force-patch.
        /// <para>
        /// Mode = Query: read-only, no state sync, no replay validation. Callers invoke via the
        /// generated <c>ProfileServiceQueryApi.EntityApi(playerId).GetRecommendedClansAsync(n)</c>
        /// (RPC variant) — the regular ApiClient local wrapper would return the client-side
        /// empty list (server-only DI is unreachable from client-side replay).
        /// </para>
        /// </summary>
        [MetaMethod(Mode = ExecutionMode.Query)]
        List<ClanSummary> GetRecommendedClans(int limit);

        /// <summary>Read profile summary without modifying state.</summary>
        [MetaMethod(Mode = ExecutionMode.Query)]
        ProfileSummary GetSummary();

        // ── Day-to-day profile mutations ─────────────────────────────────────────
        // None of these touch the clan. They're the "normal" non-social game actions
        // (heroes, gear, levels, chests) typical of a mobile RPG meta loop. The stress
        // simulator runs ~80% of its actions through these to approximate real meta
        // payloads and per-RPC work; the other 20% is the clan flow (GainPoints +
        // social actions).

        /// <summary>Buy a new item of the given tier — adds an InventoryItem.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        bool BuyItem(int tier);

        /// <summary>Sell an inventory item by index — removes it, refunds money.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        bool SellItem(int inventoryIndex);

        /// <summary>Hire a new hero of the given class.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        bool HireHero(string heroClass);

        /// <summary>Spend money + XP, bump hero level by 1.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        bool LevelUpHero(int heroId);

        /// <summary>Equip a stored inventory item onto a hero's slot.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        bool EquipItem(int heroId, int inventoryIndex);

        /// <summary>Move an equipped item from a hero back to the inventory.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        bool UnequipItem(int heroId, string slot);

        /// <summary>Mark a campaign level as completed (or improved).</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        void CompleteCampaignLevel(string levelId, int stars, int score);

        /// <summary>Open a chest — adds a record + a handful of inventory items.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        void OpenChest(int tier);

        /// <summary>Award daily login bonus — adds money + a resource bump.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        void ClaimDailyReward();

        /// <summary>
        /// Cross-entity Notification from <see cref="IClanService.AcceptApplication"/>. Tells
        /// the player that the clan accepted their application. If the player is free, profile
        /// joins immediately and confirms back to the clan; if already in another clan, the
        /// offer is parked in <see cref="ProfileState.ApprovedInvitations"/> for a later
        /// <see cref="AcceptInvitation"/> decision.
        /// </summary>
        [MetaMethod(Mode = ExecutionMode.Notification, GenerateClientApi = false)]
        Task OfferMembership(string clanId);

        /// <summary>
        /// Client-callable: switch to a previously-approved invitation. Leaves the current clan
        /// (if any) and joins the target clan. Returns false if the invitation is unknown.
        /// </summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> AcceptInvitation(string clanId);

        /// <summary>Drop a parked invitation without switching.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        bool DeclineInvitation(string clanId);
    }
}
