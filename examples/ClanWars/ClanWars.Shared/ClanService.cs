using System.Linq;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    /// <summary>
    /// Clan service implementation. State.Members tracks the canonical roster; Power is
    /// the sum of member Scores. The container DI service (server-only, reached via
    /// Context.Resolver) is notified of join/leave/power events for the recommendation cache.
    /// </summary>
    [MetaServiceImpl(typeof(IClanService), typeof(ClanState))]
    public partial class ClanService : IClanService
    {
        private ClanState S => State;
        private ClanConfig C => (ClanConfig)Config!;

        public async Task<bool> Initialize(string leaderId, string name)
        {
            if (S.Members.Count > 0) return false;  // already initialized
            S.Name = name;
            S.LeaderId = leaderId;
            S.Members.Add(leaderId);
            S.Power = 0;
            await NotifyContainerRegister();
            return true;
        }

        public async Task AddPower(int delta)
        {
            S.Power += delta;
            await NotifyContainerPowerDelta(delta);
        }

        public Task SubmitApplication(string playerId)
        {
            if (S.Members.Contains(playerId)) return Task.CompletedTask;
            if (S.Applications.Contains(playerId)) return Task.CompletedTask;
            if (S.Members.Count >= C.MaxMembers) return Task.CompletedTask;
            S.Applications.Add(playerId);
            return Task.CompletedTask;
        }

        public async Task<bool> RemoveMember(string playerId, int playerScore)
        {
            if (!S.Members.Remove(playerId)) return false;
            if (S.Officers.Contains(playerId)) S.Officers.Remove(playerId);
            S.Power -= playerScore;
            if (S.Power < 0) S.Power = 0;
            // Container learns about both the count change and the power delta. ApplyPowerDelta
            // is accumulated in-memory before flushing back; UpdateMemberCount happens immediately
            // so recommendations reflect roster size without delay.
            await NotifyContainerPowerDelta(-playerScore);
            await NotifyContainerMemberCount(S.Members.Count);
            return true;
        }

        public async Task<bool> AcceptApplication(string playerId)
        {
            if (Context.CallerId != S.LeaderId && !S.Officers.Contains(Context.CallerId ?? "")) return false;
            if (!S.Applications.Remove(playerId)) return false;
            if (S.Members.Count >= C.MaxMembers) return false;
            S.Members.Add(playerId);
            await NotifyContainerMemberCount(S.Members.Count);
            return true;
        }

        public Task<bool> RejectApplication(string playerId)
        {
            if (Context.CallerId != S.LeaderId && !S.Officers.Contains(Context.CallerId ?? "")) return Task.FromResult(false);
            return Task.FromResult(S.Applications.Remove(playerId));
        }

        public Task<bool> KickMember(string playerId)
        {
            if (Context.CallerId != S.LeaderId) return Task.FromResult(false);
            if (playerId == S.LeaderId) return Task.FromResult(false);
            return Task.FromResult(S.Members.Remove(playerId));
        }

        // ── v2-only mechanics ────────────────────────────────────────────────────

        public Task<bool> PromoteToOfficer(string playerId)
        {
            if (Context.CallerId != S.LeaderId) return Task.FromResult(false);
            if (!S.Members.Contains(playerId)) return Task.FromResult(false);
            if (S.Officers.Contains(playerId)) return Task.FromResult(false);
            if (S.Officers.Count >= C.MaxOfficers) return Task.FromResult(false);
            S.Officers.Add(playerId);
            return Task.FromResult(true);
        }

        public Task<bool> SendFriendshipProposal(string otherClanId)
        {
            if (Context.CallerId != S.LeaderId && !S.Officers.Contains(Context.CallerId ?? "")) return Task.FromResult(false);
            if (S.Friendships.Any(f => f.OtherClanId == otherClanId)) return Task.FromResult(false);
            if (S.Friendships.Count >= C.MaxFriendships) return Task.FromResult(false);
            S.Friendships.Add(new FriendshipEntry { OtherClanId = otherClanId, Status = FriendshipStatus.Pending });
            return Task.FromResult(true);
        }

        public Task<bool> AcceptFriendship(string otherClanId)
        {
            if (Context.CallerId != S.LeaderId && !S.Officers.Contains(Context.CallerId ?? "")) return Task.FromResult(false);
            var entry = S.Friendships.FirstOrDefault(f => f.OtherClanId == otherClanId);
            if (entry == null || entry.Status != FriendshipStatus.Pending) return Task.FromResult(false);
            entry.Status = FriendshipStatus.Active;
            return Task.FromResult(true);
        }

        public Task<bool> RevokeFriendship(string otherClanId)
        {
            if (Context.CallerId != S.LeaderId && !S.Officers.Contains(Context.CallerId ?? "")) return Task.FromResult(false);
            var entry = S.Friendships.FirstOrDefault(f => f.OtherClanId == otherClanId);
            if (entry == null) return Task.FromResult(false);
            S.Friendships.Remove(entry);
            return Task.FromResult(true);
        }

        public ClanSummary GetSummary()
            => new()
            {
                ClanId = Context.EntityId ?? "",
                Name = S.Name,
                MemberCount = S.Members.Count,
                Power = S.Power,
            };

        // ── Container DI notifications ───────────────────────────────────────────
        // Container is a server-only DI singleton. Method bodies run on BOTH server
        // (dispatch) AND client (Server-mode replay) — so we guard with Context.IsServer
        // to avoid hitting ResolveService<> on the client (which throws by design).

        private Task NotifyContainerRegister()
        {
            if (!Context.IsServer) return Task.CompletedTask;
            return Context.ResolveService<IClanContainerService>().RegisterClanAsync(GetSummary());
        }

        private Task NotifyContainerPowerDelta(int delta)
        {
            if (!Context.IsServer) return Task.CompletedTask;
            return Context.ResolveService<IClanContainerService>().ApplyPowerDeltaAsync(Context.EntityId ?? "", delta);
        }

        private Task NotifyContainerMemberCount(int count)
        {
            if (!Context.IsServer) return Task.CompletedTask;
            return Context.ResolveService<IClanContainerService>().UpdateMemberCountAsync(Context.EntityId ?? "", count);
        }
    }
}
