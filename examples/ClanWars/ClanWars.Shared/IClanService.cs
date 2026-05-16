using System.Threading.Tasks;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    [MetaService(StateType = typeof(ClanState), ConfigType = typeof(ClanConfig))]
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
        Task SubmitApplication(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
        Task<bool> RemoveMember(string playerId, int playerScore);

        // ── Leader / member actions (client-callable) ────────────────────────────

        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> AcceptApplication(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> RejectApplication(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> KickMember(string playerId);

        // ── v2-only mechanics ────────────────────────────────────────────────────
        // Declared on the interface so all clients compile; the
        // [MetaConfigStructureBoundary("2.0")] on ClanConfig causes the server to
        // force-ServerPatch the entire IClanService surface for v1 subscribers of a
        // clan pinned at config 2.0. v1 client code paths CAN call these methods
        // (no signature drift), but every call will be downgraded to patch mode and
        // the v2 method body runs server-side. v1's local body never fires.

        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> PromoteToOfficer(string playerId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> SendFriendshipProposal(string otherClanId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> AcceptFriendship(string otherClanId);

        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> RevokeFriendship(string otherClanId);

        [MetaMethod(Mode = ExecutionMode.Query)]
        ClanSummary GetSummary();
    }
}
