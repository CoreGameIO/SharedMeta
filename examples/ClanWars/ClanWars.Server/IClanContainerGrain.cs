using System.Collections.Generic;
using System.Threading.Tasks;
using ClanWars.Shared;
using Orleans;

namespace ClanWars.Server
{
    /// <summary>
    /// Cluster-wide directory of clans. Single activation (key <c>"global"</c>) shared
    /// across all silos. Backs the per-silo <see cref="ClanContainerService"/> cache:
    /// the cache reads on cold-start + serves recommendations from memory; the grain
    /// is updated periodically with accumulated power deltas + roster changes.
    /// </summary>
    public interface IClanContainerGrain : IGrainWithStringKey
    {
        Task<List<ClanSummary>> GetAllAsync();
        Task RegisterClanAsync(ClanSummary summary);
        Task UnregisterClanAsync(string clanId);
        Task ApplyPowerDeltasAsync(Dictionary<string, int> deltas);
        Task UpdateMemberCountAsync(string clanId, int memberCount);
    }
}
