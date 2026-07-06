    using System.Threading.Tasks;
using SharedMeta.Core;

namespace ClanWars.Shared
{

    public enum OperationResult
    {
        Ok,
        NoPlayer,
        NoPermission,
        PlayersLimit
    }
    // Authorized: only clan members may subscribe (driven by IsAuthorized on ClanService).
    // Non-members can still call methods marked [MetaMethod(OpenAccess = true)] like
    // GetSummary — that's the preview-without-subscription path. Cross-entity entry points
    // (Initialize / SubmitApplication / AddPower) bypass subscription gating entirely.
    //
    // Deliberately kept on the legacy ConfigType (obsolete but functional) rather than
    // migrated to [ServiceConfig]: ClanWars has no xUnit test coverage (only a stress-test
    // tool), and ClanConfig carries [MetaConfigStructureBoundary("2.0")] — the force-patch
    // mechanism hasn't been verified against [ServiceConfig] yet. Migrate once either real
    // test coverage exists or that verification has been done; don't migrate on faith.
#pragma warning disable CS0618
    [MetaService(StateType = typeof(ClanState), ConfigType = typeof(ClanConfig),
                 AccessPolicy = EntityAccessPolicy.Authorized)]
#pragma warning restore CS0618
    public interface IClanService : IMetaService
    {
        // ── Bootstrap / cross-entity API (not client-callable) ───────────────────
        // Initialize is called once by ProfileService.CreateClan via cross-entity; it
        // runs [MetaInit]-equivalent setup. Inbound applications + power-deltas also
        // arrive via cross-entity from ProfileService.

        [MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
        Task<bool> Initialize(string leaderId, string name);

        // 0.22.0+: Notification — entity→entity fire-and-forget (peer of Signal on the
        // cross-entity axis). Caller (ProfileService.GainPoints) fires this and continues;
        // clan grain receives via [OneWay] grain entry, mutates State.Power, broadcasts to
        // its own subscribers normally. Profile doesn't depend on the result, doesn't read
        // clan state after this call, and isn't subscribed to the clan, so losing the
        // round-trip is purely a latency / throughput win.
        [MetaMethod(Mode = ExecutionMode.Notification)]
        Task AddPower(int delta);

        [MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void SubmitApplication(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
        Task<bool> RemoveMember(string playerId, int playerScore);

        /// <summary>
        /// Cross-entity call from <see cref="IProfileService.OfferMembership"/>'s in-process
        /// accept path (or from <see cref="IProfileService.AcceptInvitation"/>). Player has
        /// decided to join — clan officially adds them to Members and bumps Power. Returns true
        /// when the player is in Members after the call (already-member or freshly added); false
        /// only when the roster is full. Server-mode (not Notification) so profile can await and
        /// only commit S.ClanId on confirmed success — otherwise a ResolveClan during the
        /// fire-and-forget window would trip IsAuthorized (player not yet in Members) and fail
        /// the subscribe.
        /// </summary>
        [MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
        Task<bool> ConfirmJoin(string playerId, int playerScore);

        // ── Leader / member actions (client-callable) ────────────────────────────

        [MetaMethod(Mode = ExecutionMode.Server)]
        OperationResult AcceptApplication(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        OperationResult RejectApplication(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        bool KickMember(string playerId);

        // ── v2-only mechanics ────────────────────────────────────────────────────
        // Declared on the interface so all clients compile; the
        // [MetaConfigStructureBoundary("2.0")] on ClanConfig causes the server to
        // force-ServerPatch the entire IClanService surface for v1 subscribers of a
        // clan pinned at config 2.0. v1 client code paths CAN call these methods
        // (no signature drift), but every call will be downgraded to patch mode and
        // the v2 method body runs server-side. v1's local body never fires.

        [MetaMethod(Mode = ExecutionMode.Server)]
        bool PromoteToOfficer(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        bool SendFriendshipProposal(string otherClanId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        bool AcceptFriendship(string otherClanId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        bool RevokeFriendship(string otherClanId);

        // OpenAccess: read-only preview that bypasses Authorized subscription gate.
        // Lets non-members window-shop clans without acquiring a subscription.
        [MetaMethod(Mode = ExecutionMode.Query, OpenAccess = true)]
        ClanSummary GetSummary();
    }
}
