using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// Persisted state for <see cref="ConfigStoreGrain"/>. One blob per (configType, version)
    /// grain. Null <see cref="Bytes"/> means the version was cleared (or never set).
    /// </summary>
    [GenerateSerializer]
    public class ConfigStoreGrainState
    {
        [Id(0)] public byte[]? Bytes { get; set; }
    }

    /// <summary>
    /// Grain implementation of <see cref="IConfigStoreGrain"/>. Backed by Orleans persistent
    /// state under the <c>"ConfigStore"</c> state name and <c>"Default"</c> storage provider —
    /// the host application configures which Orleans storage provider backs the
    /// <c>"Default"</c> name (in-memory for tests, Azure Tables / Redis / Postgres for
    /// production). Single-version-per-grain keeps the persistence write paths small and
    /// avoids contention when admin tools publish multiple versions concurrently.
    /// </summary>
    public class ConfigStoreGrain : Grain, IConfigStoreGrain
    {
        private readonly IPersistentState<ConfigStoreGrainState> _state;

        public ConfigStoreGrain(
            [PersistentState("ConfigStore", "Default")] IPersistentState<ConfigStoreGrainState> state)
        {
            _state = state;
        }

        public Task<byte[]?> GetAsync() => Task.FromResult(_state.State.Bytes);

        public async Task SetAsync(byte[] bytes)
        {
            _state.State.Bytes = bytes;
            await _state.WriteStateAsync();
        }

        public async Task ClearAsync()
        {
            _state.State.Bytes = null;
            await _state.WriteStateAsync();
        }
    }
}
