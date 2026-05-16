using System.Collections.Generic;
using System.Threading.Tasks;
using ClanWars.Shared;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;

namespace ClanWars.Server
{
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class ClanContainerGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public Dictionary<string, ClanSummary> Clans { get; set; } = new();
    }

    /// <summary>
    /// Cluster-wide clan directory. State persisted via <c>"Default"</c> Orleans storage.
    /// Mutations are coarse-grained — power deltas come in batched dictionaries from the
    /// silo-local cache (see <see cref="ClanContainerService"/>) rather than per-event.
    /// </summary>
    public class ClanContainerGrain : Grain, IClanContainerGrain
    {
        private readonly IPersistentState<ClanContainerGrainState> _state;

        public ClanContainerGrain(
            [PersistentState("clanContainer", "Default")] IPersistentState<ClanContainerGrainState> state)
        {
            _state = state;
        }

        public Task<List<ClanSummary>> GetAllAsync()
            => Task.FromResult(new List<ClanSummary>(_state.State.Clans.Values));

        public async Task RegisterClanAsync(ClanSummary summary)
        {
            _state.State.Clans[summary.ClanId] = summary;
            await _state.WriteStateAsync();
        }

        public async Task UnregisterClanAsync(string clanId)
        {
            if (_state.State.Clans.Remove(clanId))
                await _state.WriteStateAsync();
        }

        public async Task ApplyPowerDeltasAsync(Dictionary<string, int> deltas)
        {
            bool any = false;
            foreach (var (clanId, delta) in deltas)
            {
                if (!_state.State.Clans.TryGetValue(clanId, out var s)) continue;
                s.Power = System.Math.Max(0, s.Power + delta);
                any = true;
            }
            if (any) await _state.WriteStateAsync();
        }

        public async Task UpdateMemberCountAsync(string clanId, int memberCount)
        {
            if (!_state.State.Clans.TryGetValue(clanId, out var s)) return;
            s.MemberCount = memberCount;
            await _state.WriteStateAsync();
        }
    }
}
