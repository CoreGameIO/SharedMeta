using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Server.Core.Config;
using SharedMeta.Server.Core.Config.Admin;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ <see cref="IConfigAdminGrain"/> implementation. 0.28.0: driven by the
    /// generator-emitted <see cref="IConfigCatalog"/> (no more <c>IConfigByteSource.Configs</c>
    /// list); per-type operations close <c>TConfig</c> at compile time through
    /// <see cref="IConfigCatalogHandler"/> callbacks and use the typed
    /// <see cref="IConfigRegistry"/> generic extensions — zero reflection.
    /// Project admin tools join the Orleans cluster as a client and call this grain through
    /// <c>IClusterClient.GetGrain&lt;IConfigAdminGrain&gt;(0)</c>.
    /// </summary>
    public class ConfigAdminGrain : Grain, IConfigAdminGrain
    {
        private readonly IConfigRegistry _registry;
        private readonly IConfigCatalog _catalog;
        private readonly MetaTransportOptions? _transport;
        private readonly DefaultClientVersionService? _localVersionService;
        private readonly ILogger<ConfigAdminGrain> _logger;

        public ConfigAdminGrain(
            IConfigRegistry registry,
            IConfigCatalog catalog,
            MetaTransportOptions? transport = null,
            DefaultClientVersionService? localVersionService = null,
            ILogger<ConfigAdminGrain>? logger = null)
        {
            _registry = registry;
            _catalog = catalog;
            _transport = transport;
            _localVersionService = localVersionService;
            _logger = logger ?? NullLogger<ConfigAdminGrain>.Instance;
        }

        private IConfigMetadataGrain MetadataGrain(string configName) =>
            GrainFactory.GetGrain<IConfigMetadataGrain>(configName);

        public Task<string[]> ListConfigNamesAsync()
        {
            var names = _catalog.Entries.Select(c => c.FullName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            return Task.FromResult(names);
        }

        public async Task<ConfigOverview[]> ListConfigsAsync()
        {
            var collector = new OverviewBuilder(_registry, GrainFactory);
            await _catalog.ForEachAsync(collector, CancellationToken.None);
            return collector.Build();
        }

        public async Task<ConfigOverview?> GetConfigAsync(string name)
        {
            var single = new SingleOverviewBuilder(_registry, GrainFactory);
            var found = await _catalog.TryDispatchAsync(name, single, CancellationToken.None);
            return found ? single.Result : null;
        }

        public async Task<byte[]> DownloadAsync(string name, string version)
        {
            var v = MetaConfigVersion.Parse(version);
            var op = new DownloadHandler(_registry, v);
            var found = await _catalog.TryDispatchAsync(name, op, CancellationToken.None);
            if (!found)
                throw new InvalidOperationException($"Config '{name}' is not registered.");
            if (op.Bytes is null || op.Bytes.Length == 0)
                throw new InvalidOperationException($"{name} v{version} is not published.");
            return op.Bytes;
        }

        public async Task<ConfigOverview> UploadAsync(
            string name, string version, byte[] bytes, string origin, string publishedBy, string? notes = null, bool failOnDrift = false)
        {
            if (bytes is null) throw new ArgumentNullException(nameof(bytes));
            var v = MetaConfigVersion.Parse(version);
            var canonical = v.ToString();
            var op = new UploadHandler(_registry, GrainFactory, v, bytes, origin ?? "", publishedBy ?? "", notes, failOnDrift, _logger);
            var found = await _catalog.TryDispatchAsync(name, op, CancellationToken.None);
            if (!found || op.Overview is null)
                throw new InvalidOperationException($"Config '{name}' is not registered.");
            return op.Overview;
        }

        public async Task<bool> UnpublishAsync(string name, string version, string deletedBy)
        {
            var v = MetaConfigVersion.Parse(version);
            var op = new UnpublishHandler(_registry, GrainFactory, v, deletedBy ?? "", _logger);
            var found = await _catalog.TryDispatchAsync(name, op, CancellationToken.None);
            return found;
        }

        public async Task<ConfigOverview?> SetBranchHoldAsync(string name, string branch, bool held, string changedBy)
        {
            if (string.IsNullOrWhiteSpace(branch))
                throw new ArgumentException("branch is required", nameof(branch));

            // Resolve the caller's name (FullName or short DisplayName) to the canonical FullName the
            // metadata grain + bootstrap seed are keyed by. Normalize the branch to a "Major.Minor" key
            // so it matches what the bootstrap computes via MetaConfigVersion.GetBranchKey().
            var entry = _catalog.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, name, StringComparison.Ordinal) ||
                string.Equals(e.DisplayName, name, StringComparison.Ordinal));
            if (entry == null) return null;

            var branchKey = MetaConfigVersion.Parse(branch).GetBranchKey();
            await MetadataGrain(entry.FullName).SetBranchHoldAsync(branchKey, held);
            _logger.LogInformation(
                "ConfigAdmin: {Name} branch {Branch} hold → {Held} by {By}",
                entry.FullName, branchKey, held, changedBy);
            return await GetConfigAsync(entry.FullName);
        }

        public Task<ClientVersionSnapshot> GetClientVersionsAsync()
            => BuildVersionSnapshotAsync();

        public async Task<ClientVersionSnapshot> SetCurrentClientVersionAsync(string version, string changedBy)
        {
            if (string.IsNullOrWhiteSpace(version))
                throw new ArgumentException("Version must not be empty.", nameof(version));

            await GrainFactory.GetGrain<ICurrentClientVersionGrain>(0)
                .SetAsync(version.Trim(), changedBy ?? "");
            _localVersionService?.SetCurrentLocally(version);

            _logger.LogInformation(
                "ConfigAdmin: set Current client version → '{Version}' by {By}", version, changedBy);
            return await BuildVersionSnapshotAsync();
        }

        public async Task<ClientVersionSnapshot> SetMinClientVersionAsync(string? version, string changedBy)
        {
            await GrainFactory.GetGrain<IVersionPolicyGrain>("global")
                .SetMinClientVersionAsync(string.IsNullOrWhiteSpace(version) ? null : version.Trim());
            _logger.LogInformation(
                "ConfigAdmin: set Min client version → '{Version}' by {By}",
                version ?? "(cleared)", changedBy);
            return await BuildVersionSnapshotAsync();
        }

        public async Task<ClientVersionSnapshot> SetMaxClientVersionAsync(string? version, string changedBy)
        {
            await GrainFactory.GetGrain<IVersionPolicyGrain>("global")
                .SetMaxClientVersionAsync(string.IsNullOrWhiteSpace(version) ? null : version.Trim());
            _logger.LogInformation(
                "ConfigAdmin: set Max client version → '{Version}' by {By}",
                version ?? "(cleared)", changedBy);
            return await BuildVersionSnapshotAsync();
        }

        private async Task<ClientVersionSnapshot> BuildVersionSnapshotAsync()
        {
            var current = await GrainFactory.GetGrain<ICurrentClientVersionGrain>(0).GetAsync();
            var policy = GrainFactory.GetGrain<IVersionPolicyGrain>("global");
            var min = await policy.GetMinClientVersionAsync();
            var max = await policy.GetMaxClientVersionAsync();
            return new ClientVersionSnapshot
            {
                Current = current,
                Min = min,
                Max = max,
                Server = _transport?.ServerVersion,
            };
        }

        // ── catalog visitor implementations ──────────────────────────────────────────────

        private sealed class OverviewBuilder : IConfigCatalogHandler
        {
            private readonly IConfigRegistry _registry;
            private readonly IGrainFactory _grains;
            private readonly List<ConfigOverview> _items = new();

            public OverviewBuilder(IConfigRegistry registry, IGrainFactory grains)
            { _registry = registry; _grains = grains; }

            public async Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken ct) where TConfig : class
                => _items.Add(await BuildOverviewAsync<TConfig>(_registry, _grains, fullName, displayName));

            public ConfigOverview[] Build() => _items.ToArray();
        }

        private sealed class SingleOverviewBuilder : IConfigCatalogHandler
        {
            private readonly IConfigRegistry _registry;
            private readonly IGrainFactory _grains;
            public ConfigOverview? Result { get; private set; }

            public SingleOverviewBuilder(IConfigRegistry registry, IGrainFactory grains)
            { _registry = registry; _grains = grains; }

            public async Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken ct) where TConfig : class
                => Result = await BuildOverviewAsync<TConfig>(_registry, _grains, fullName, displayName);
        }

        private sealed class DownloadHandler : IConfigCatalogHandler
        {
            private readonly IConfigRegistry _registry;
            private readonly MetaConfigVersion _version;
            public byte[]? Bytes { get; private set; }

            public DownloadHandler(IConfigRegistry registry, MetaConfigVersion version)
            { _registry = registry; _version = version; }

            public async Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken ct) where TConfig : class
                => Bytes = await _registry.GetAsync<TConfig>(_version);
        }

        private sealed class UploadHandler : IConfigCatalogHandler
        {
            private readonly IConfigRegistry _registry;
            private readonly IGrainFactory _grains;
            private readonly MetaConfigVersion _version;
            private readonly byte[] _bytes;
            private readonly string _origin;
            private readonly string _publishedBy;
            private readonly string? _notes;
            private readonly bool _failOnDrift;
            private readonly ILogger _logger;

            public ConfigOverview? Overview { get; private set; }

            public UploadHandler(IConfigRegistry registry, IGrainFactory grains, MetaConfigVersion version, byte[] bytes,
                string origin, string publishedBy, string? notes, bool failOnDrift, ILogger logger)
            {
                _registry = registry; _grains = grains; _version = version; _bytes = bytes;
                _origin = origin; _publishedBy = publishedBy; _notes = notes; _failOnDrift = failOnDrift; _logger = logger;
            }

            public async Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken ct) where TConfig : class
            {
                var outcome = await _registry.PublishIfChangedAsync<TConfig>(_version, _bytes, _failOnDrift);
                await _grains.GetGrain<IConfigMetadataGrain>(fullName)
                    .RecordPublishAsync(_version.ToString(), _bytes.Length, _origin, _publishedBy, _notes)
                    ;
                _logger.LogInformation(
                    "ConfigAdmin: upload {Name} v{Version} ({Bytes} B, origin={Origin}, by={By}) → {Outcome}",
                    fullName, _version, _bytes.Length, _origin, _publishedBy, outcome);
                Overview = await BuildOverviewAsync<TConfig>(_registry, _grains, fullName, displayName);
            }
        }

        private sealed class UnpublishHandler : IConfigCatalogHandler
        {
            private readonly IConfigRegistry _registry;
            private readonly IGrainFactory _grains;
            private readonly MetaConfigVersion _version;
            private readonly string _deletedBy;
            private readonly ILogger _logger;

            public UnpublishHandler(IConfigRegistry registry, IGrainFactory grains, MetaConfigVersion version, string deletedBy, ILogger logger)
            { _registry = registry; _grains = grains; _version = version; _deletedBy = deletedBy; _logger = logger; }

            public async Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken ct) where TConfig : class
            {
                await _registry.UnpublishAsync<TConfig>(_version);
                await _grains.GetGrain<IConfigMetadataGrain>(fullName).RemoveAsync(_version.ToString());
                _logger.LogInformation(
                    "ConfigAdmin: unpublished {Name} v{Version} by {By}", fullName, _version, _deletedBy);
            }
        }

        private static async Task<ConfigOverview> BuildOverviewAsync<TConfig>(
            IConfigRegistry registry, IGrainFactory grains, string fullName, string displayName) where TConfig : class
        {
            var metadataGrain = grains.GetGrain<IConfigMetadataGrain>(fullName);
            var registryVersions = await registry.ListVersionsAsync<TConfig>();
            var metadataRecords = await metadataGrain.ListAsync();
            var metadataByVersion = metadataRecords.ToDictionary(r => r.Version, StringComparer.Ordinal);
            var heldBranches = new HashSet<string>(
                await metadataGrain.ListHeldBranchesAsync(), StringComparer.Ordinal);

            var allVersions = registryVersions
                .Select(v => v.ToString())
                .Select(versionStr =>
                {
                    if (metadataByVersion.TryGetValue(versionStr, out var rec))
                        return rec;
                    return new ConfigVersionInfo
                    {
                        Version = versionStr,
                        SizeBytes = 0,
                        PublishedAt = DateTimeOffset.MinValue,
                        PublishedBy = "(unknown)",
                        Origin = "",
                    };
                })
                .ToList();

            var branches = allVersions
                .GroupBy(v => MetaConfigVersion.Parse(v.Version).GetBranchKey())
                .OrderByDescending(g => MetaConfigVersion.Parse(g.Key + ".0"))
                .Select(g => new ConfigBranchInfo
                {
                    Branch = g.Key,
                    Versions = g.OrderByDescending(v => MetaConfigVersion.Parse(v.Version)).ToArray(),
                    Held = heldBranches.Contains(g.Key),
                })
                .ToArray();

            return new ConfigOverview
            {
                ConfigName = fullName,
                DisplayName = displayName,
                Branches = branches,
            };
        }
    }
}
