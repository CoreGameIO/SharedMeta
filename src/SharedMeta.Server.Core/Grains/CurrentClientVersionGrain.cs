using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Server.Core.Grains
{
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class CurrentClientVersionGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string? Version { get; set; }
        [Id(1), Key(1), MemoryPackOrder(1)] public string? ChangedBy { get; set; }
    }

    /// <summary>
    /// Implementation of <see cref="ICurrentClientVersionGrain"/>. Single activation (key 0).
    /// Persisted to the "Default" storage provider.
    /// </summary>
    public class CurrentClientVersionGrain : Grain, ICurrentClientVersionGrain
    {
        private readonly IPersistentState<CurrentClientVersionGrainState> _state;
        private readonly ILogger<CurrentClientVersionGrain> _logger;

        public CurrentClientVersionGrain(
            [PersistentState("currentClientVersion", "Default")] IPersistentState<CurrentClientVersionGrainState> state,
            ILogger<CurrentClientVersionGrain>? logger = null)
        {
            _state = state;
            _logger = logger ?? NullLogger<CurrentClientVersionGrain>.Instance;
        }

        public Task<string?> GetAsync() => Task.FromResult(_state.State.Version);

        public async Task SetAsync(string version, string changedBy)
        {
            var prev = _state.State.Version;
            _state.State.Version = version;
            _state.State.ChangedBy = changedBy;
            await _state.WriteStateAsync();
            _logger.LogInformation(
                "CurrentClientVersion: '{Prev}' → '{New}' by {By}",
                prev ?? "(unset)", version, changedBy);
        }
    }
}
