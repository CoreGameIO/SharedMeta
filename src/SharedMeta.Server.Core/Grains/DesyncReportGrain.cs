using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Persistent state for the desync report grain.
    /// Holds a bounded list of recent reports per player.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class DesyncReportGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public List<DeepDesyncReport> Reports { get; set; } = new();

        /// <summary>
        /// Deep desync analysis switched on for this player under DeepDesyncMode.PerPlayer.
        /// Persisted rather than held on the connection so it survives reconnects and so admin
        /// tooling and the player's own client write to the same place.
        /// </summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public bool AnalysisEnabled { get; set; }
    }

    /// <summary>
    /// Stores deep desync reports per player as a bounded ring buffer (50 entries by default).
    /// </summary>
    public class DesyncReportGrain : Grain, IDesyncReportGrain
    {
        private const int MaxReports = 50;
        private readonly IPersistentState<DesyncReportGrainState> _state;

        public DesyncReportGrain(
            [PersistentState("desyncReport", "Default")] IPersistentState<DesyncReportGrainState> state)
        {
            _state = state;
        }

        public async Task StoreReportAsync(DeepDesyncReport report)
        {
            _state.State.Reports.Add(report);
            // Trim to MaxReports — keep most recent
            if (_state.State.Reports.Count > MaxReports)
            {
                var excess = _state.State.Reports.Count - MaxReports;
                _state.State.Reports.RemoveRange(0, excess);
            }
            await _state.WriteStateAsync();
        }

        public Task<List<DeepDesyncReport>> GetRecentAsync(int max)
        {
            var count = System.Math.Min(max, _state.State.Reports.Count);
            var skip = _state.State.Reports.Count - count;
            return Task.FromResult(_state.State.Reports.Skip(skip).Take(count).ToList());
        }

        public async Task ClearAsync()
        {
            // Clears history only — AnalysisEnabled survives, so wiping a player's reports
            // does not silently switch their analysis off mid-investigation.
            _state.State.Reports.Clear();
            await _state.WriteStateAsync();
        }

        public Task<bool> IsAnalysisEnabledAsync() => Task.FromResult(_state.State.AnalysisEnabled);

        public async Task SetAnalysisEnabledAsync(bool enabled)
        {
            if (_state.State.AnalysisEnabled == enabled) return;

            _state.State.AnalysisEnabled = enabled;
            await _state.WriteStateAsync();
        }
    }
}
