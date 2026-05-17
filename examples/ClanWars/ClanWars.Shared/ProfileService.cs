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
            => new()
            {
                Score = S.Score,
                Money = S.Money,
                ClanId = S.ClanId,
                ApprovedInvitations = S.ApprovedInvitations.Count == 0
                    ? null
                    : new List<string>(S.ApprovedInvitations),
            };

        /// <summary>
        /// Receive a membership offer from a clan that accepted our application. If we're free,
        /// commit immediately (set ClanId + send ConfirmJoin to clan). If we're already in
        /// another clan, park the offer for a later switch decision.
        /// </summary>
        public async Task OfferMembership(string clanId)
        {
            if (string.IsNullOrEmpty(clanId)) return;
            // Always drop the matching pending app — the leader's decision is now reflected.
            S.PendingApplications.Remove(clanId);

            // IMPORTANT: this method is called CROSS-ENTITY from clan.AcceptApplication, so
            // Context.CallerId here is NOT the player — it's the previous-hop grain (clan) /
            // original client caller (the leader of that clan). We need our OWN playerId, which
            // for a Private-scoped Profile entity equals Context.EntityId.
            var playerId = Context.EntityId ?? "anon";

            if (string.IsNullOrEmpty(S.ClanId))
            {
                // Free — join immediately. Await ConfirmJoin so we only commit S.ClanId on
                // confirmed-in-roster: a concurrent join via another path may have filled the
                // clan since AcceptApplication left a slot open, in which case ConfirmJoin
                // returns false and we leave S.ClanId null. This also closes the race window
                // that previously caused ResolveClan failures (subscribe before IsAuthorized
                // saw the new Member).
                var joined = await GetIClanService(clanId).ConfirmJoin(playerId, S.Score);
                if (joined)
                {
                    S.ClanId = clanId;
                    S.PendingApplications.Clear();
                    S.ApprovedInvitations.Remove(clanId);
                }
            }
            else if (S.ClanId != clanId && !S.ApprovedInvitations.Contains(clanId))
            {
                // Busy — park as approved invitation for later AcceptInvitation.
                S.ApprovedInvitations.Add(clanId);
            }
        }

        /// <summary>
        /// Switch from current clan (if any) to a previously-approved invitation. Returns false
        /// when the target invitation is unknown or the leave step rejects.
        /// </summary>
        public async Task<bool> AcceptInvitation(string clanId)
        {
            if (string.IsNullOrEmpty(clanId)) return false;
            if (!S.ApprovedInvitations.Contains(clanId)) return false;

            // Use EntityId rather than CallerId — same reasoning as in OfferMembership. Even
            // though AcceptInvitation is client-initiated (where CallerId == EntityId for a
            // UserOwned entity), keep the canonical reference so a future caller-chain change
            // can't silently misroute the playerId argument.
            var playerId = Context.EntityId ?? "anon";

            if (!string.IsNullOrEmpty(S.ClanId) && S.ClanId != clanId)
            {
                var current = GetIClanService(S.ClanId);
                var removed = await current.RemoveMember(playerId, S.Score);
                if (!removed) return false;
                S.ClanId = null;
            }

            // Await ConfirmJoin so the new clan has us in Members before we declare S.ClanId.
            // Subsequent ResolveClan would otherwise race the fire-and-forget and fail
            // IsAuthorized. If ConfirmJoin returns false (roster full), drop the invitation
            // and leave the player clan-less.
            var joined = await GetIClanService(clanId).ConfirmJoin(playerId, S.Score);
            S.ApprovedInvitations.Remove(clanId);
            if (!joined) return false;
            S.ClanId = clanId;
            S.PendingApplications.Clear();
            return true;
        }

        public Task<bool> DeclineInvitation(string clanId)
        {
            return Task.FromResult(S.ApprovedInvitations.Remove(clanId));
        }
    }
}
