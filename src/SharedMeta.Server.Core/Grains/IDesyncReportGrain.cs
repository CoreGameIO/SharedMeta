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
    }
}
