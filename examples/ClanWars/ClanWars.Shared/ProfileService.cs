using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    /// <summary>
    /// Profile service implementation. Cross-entity calls into <see cref="IClanService"/>
    /// for clan operations; the server-only <see cref="IClanContainerService"/> is reached
    /// via DI on the silo side (Context.Resolver) and is NOT visible to clients.
    /// </summary>
    [MetaServiceImpl(typeof(IProfileService), typeof(ProfileState), typeof(IClanService))]
    public partial class ProfileService : IProfileService
    {
        private ProfileState S => State;
        private ClanConfig C => (ClanConfig)Config!;

        public Task GainPoints(int amount)
        {
            if (amount <= 0) return Task.CompletedTask;
            S.Score += amount;

            // Forward the delta to the clan (cross-entity, fire-and-forget). Profile doesn't
            // need to wait for the clan grain to apply the delta — clan broadcasts its own
            // State.Power change to its subscribers independently, and profile never reads
            // clan state after this call. Saves one grain-to-grain await on the hot path.
            if (!string.IsNullOrEmpty(S.ClanId))
            {
                GetIClanService(S.ClanId).AddPower(amount);
            }
            return Task.CompletedTask;
        }

        public async Task<string?> CreateClan(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (S.Money < C.CreateClanCost) return null;
            if (!string.IsNullOrEmpty(S.ClanId)) return null;  // already in a clan

            // Derive a deterministic clan id from the player + name so a retry of the same
            // creation produces the same entity (stress-test friendliness).
            var clanId = $"clan-{Context.CallerId ?? "anon"}-{name}";

            S.Money -= C.CreateClanCost;
            S.ClanId = clanId;

            var clan = GetIClanService(clanId);
            var ok = await clan.Initialize(Context.CallerId ?? "anon", name);
            if (!ok)
            {
                // Rollback if the clan grain rejected the initialize (e.g. concurrent create).
                S.Money += C.CreateClanCost;
                S.ClanId = null;
                return null;
            }
            // Seed initial clan power with the player's current score (OneWay — see GainPoints
            // commentary; same reasoning applies here).
            if (S.Score > 0)
                clan.AddPower(S.Score);

            return clanId;
        }

        public async Task<bool> ApplyToClan(string clanId)
        {
            if (string.IsNullOrEmpty(clanId)) return false;
            if (!string.IsNullOrEmpty(S.ClanId)) return false;  // already in one
            if (S.PendingApplications.Contains(clanId)) return false;

            S.PendingApplications.Add(clanId);
            var clan = GetIClanService(clanId);
            await clan.SubmitApplication(Context.CallerId ?? "anon");
            return true;
        }

        public async Task<bool> LeaveClan()
        {
            var clanId = S.ClanId;
            if (string.IsNullOrEmpty(clanId)) return false;

            var clan = GetIClanService(clanId);
            var removed = await clan.RemoveMember(Context.CallerId ?? "anon", S.Score);
            if (!removed) return false;

            S.ClanId = null;
            return true;
        }

        public List<ClanSummary> GetRecommendedClans(int limit)
        {
            // Query mode body. The local sync wrapper on ApiClient returns empty client-side
            // (server-only DI is unreachable); meaningful callers MUST use the generated
            // ProfileServiceQueryApi RPC variant to hit the server's container snapshot.
            if (!Context.IsServer) return new List<ClanSummary>();
            return Context.ResolveService<IClanContainerService>().GetRecommended(limit);
        }

        public ProfileSummary GetSummary()
            => new() { Score = S.Score, Money = S.Money, ClanId = S.ClanId };
    }
}
