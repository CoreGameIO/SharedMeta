using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Grain that stores deep desync reports for a player.
    /// Grain key = playerId.
    /// </summary>
    public interface IDesyncReportGrain : IGrainWithStringKey
    {
        /// <summary>Append a report to this player's history (bounded ring).</summary>
        Task StoreReportAsync(DeepDesyncReport report);

        /// <summary>Get up to <paramref name="max"/> most recent reports.</summary>
        Task<List<DeepDesyncReport>> GetRecentAsync(int max);

        /// <summary>Clear all reports for this player.</summary>
        Task ClearAsync();

        /// <summary>
        /// Whether deep desync analysis is switched on for this player. Consulted once per session,
        /// and only under <see cref="DeepDesyncMode.PerPlayer"/> — the other modes answer without
        /// touching this grain.
        /// </summary>
        Task<bool> IsAnalysisEnabledAsync();

        /// <summary>
        /// Turn deep desync analysis on or off for this player. Written both by the player's own
        /// client (through the debug API, when the silo permits it) and by admin tooling, so the
        /// two paths cannot disagree about the current value.
        /// </summary>
        Task SetAnalysisEnabledAsync(bool enabled);
    }
}
