using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Server.Core.Config.Admin;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Persisted audit map for one config type — one grain per
    /// <c>configName</c> key (typically <see cref="System.Type.FullName"/>).
    /// Stored under <c>"ConfigMetadata"</c> state name and the <c>"Default"</c> storage
    /// provider — same provider used by the rest of the SharedMeta Orleans subsystem.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class ConfigMetadataGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public Dictionary<string, ConfigVersionInfo> Records { get; set; } = new();

        /// <summary>0.32.0+ Held <c>Major.Minor</c> branch keys — bootstrap seed skips these.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public HashSet<string> HeldBranches { get; set; } = new();
    }

    /// <summary>
    /// 0.27.0+ <see cref="IConfigMetadataGrain"/> implementation. Sidecar to
    /// <c>IConfigRegistry</c> — registry holds bytes, this grain holds audit (who/when/why).
    /// </summary>
    public class ConfigMetadataGrain : Grain, IConfigMetadataGrain
    {
        private readonly IPersistentState<ConfigMetadataGrainState> _state;
        private readonly ILogger<ConfigMetadataGrain> _logger;

        public ConfigMetadataGrain(
            [PersistentState("ConfigMetadata", "Default")] IPersistentState<ConfigMetadataGrainState> state,
            ILogger<ConfigMetadataGrain>? logger = null)
        {
            _state = state;
            _logger = logger ?? NullLogger<ConfigMetadataGrain>.Instance;
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _state.State.HeldBranches ??= [];
            return base.OnActivateAsync(cancellationToken);
        }

        public async Task RecordPublishAsync(string version, int sizeBytes, string origin, string publishedBy, string? notes)
        {
            if (string.IsNullOrWhiteSpace(version))
                throw new ArgumentException("version is required", nameof(version));

            _state.State.Records[version] = new ConfigVersionInfo
            {
                Version = version,
                SizeBytes = sizeBytes,
                PublishedAt = DateTimeOffset.UtcNow,
                PublishedBy = publishedBy ?? "",
                Notes = notes,
                Origin = origin ?? "",
            };
            await _state.WriteStateAsync();
            _logger.LogInformation(
                "ConfigMetadata[{Name}]: recorded {Version} (origin={Origin}, by={By}, {Size} B)",
                this.GetPrimaryKeyString(), version, origin, publishedBy, sizeBytes);
        }

        public async Task RemoveAsync(string version)
        {
            if (_state.State.Records.Remove(version))
            {
                await _state.WriteStateAsync();
                _logger.LogInformation(
                    "ConfigMetadata[{Name}]: removed {Version}",
                    this.GetPrimaryKeyString(), version);
            }
        }

        public Task<ConfigVersionInfo[]> ListAsync()
        {
            var arr = _state.State.Records.Values
                .OrderByDescending(r => r.PublishedAt)
                .ToArray();
            return Task.FromResult(arr);
        }

        public Task<ConfigVersionInfo?> GetAsync(string version)
        {
            _state.State.Records.TryGetValue(version, out var rec);
            return Task.FromResult<ConfigVersionInfo?>(rec);
        }

        public async Task SetBranchHoldAsync(string branchKey, bool held)
        {
            if (string.IsNullOrWhiteSpace(branchKey))
                throw new ArgumentException("branchKey is required", nameof(branchKey));

            var changed = held
                ? _state.State.HeldBranches.Add(branchKey)
                : _state.State.HeldBranches.Remove(branchKey);

            if (!changed) return; // already in the desired state — no write

            await _state.WriteStateAsync();
            _logger.LogInformation(
                "ConfigMetadata[{Name}]: branch {Branch} hold → {Held}",
                this.GetPrimaryKeyString(), branchKey, held);
        }

        public Task<string[]> ListHeldBranchesAsync()
            => Task.FromResult(_state.State.HeldBranches.ToArray());

        public Task<bool> IsBranchHeldAsync(string branchKey)
            => Task.FromResult(!string.IsNullOrEmpty(branchKey) && _state.State.HeldBranches.Contains(branchKey));
    }
}
