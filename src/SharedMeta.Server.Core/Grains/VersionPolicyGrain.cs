using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Server.Core.Grains
{
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class VersionPolicyGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string? MinClientVersion { get; set; }
    }

    /// <summary>
    /// Implementation of <see cref="IVersionPolicyGrain"/>.
    /// One instance per cluster (key "global"). State persisted to the "Default" storage provider.
    /// </summary>
    public class VersionPolicyGrain : Grain, IVersionPolicyGrain
    {
        private readonly IPersistentState<VersionPolicyGrainState> _state;

        public VersionPolicyGrain(
            [PersistentState("versionPolicy", "Default")] IPersistentState<VersionPolicyGrainState> state)
        {
            _state = state;
        }

        public Task<string?> GetMinClientVersionAsync()
            => Task.FromResult(_state.State.MinClientVersion);

        public async Task SetMinClientVersionAsync(string? version)
        {
            _state.State.MinClientVersion = version;
            await _state.WriteStateAsync();
        }
    }
}
