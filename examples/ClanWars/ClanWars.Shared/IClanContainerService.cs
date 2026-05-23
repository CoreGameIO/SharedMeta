using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClanWars.Shared
{
    /// <summary>
    /// Server-only DI service. Caches the cluster-wide list of clans (id + summary) over
    /// a backing <c>IClanContainerGrain</c> singleton; aggregates per-clan power-deltas
    /// in memory and flushes them to the grain periodically.
    /// <para>
    /// Consumed by <see cref="IProfileService"/> for the "show me clans I could join"
    /// recommendation flow (cross-grain accessor — the service runs on the silo, not
    /// inside the entity grain, and is reached via DI).
    /// </para>
    /// </summary>
    public interface IClanContainerService
    {
        /// <summary>Synchronous snapshot read against the in-memory cache. Used by the
        /// IProfileService.GetRecommendedClans query method, which needs a sync return type
        /// (Query mode local wrapper cannot await a Task).</summary>
        List<ClanSummary> GetRecommended(int limit);
        Task RegisterClanAsync(ClanSummary summary);
        Task UnregisterClanAsync(string clanId);
        ValueTask ApplyPowerDeltaAsync(string clanId, int delta);
        ValueTask UpdateMemberCountAsync(string clanId, int memberCount);
    }
}
