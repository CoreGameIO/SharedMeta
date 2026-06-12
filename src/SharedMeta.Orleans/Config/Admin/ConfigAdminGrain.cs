using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Config;
using SharedMeta.Server.Core.Config.Admin;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ <see cref="IConfigAdminGrain"/> implementation. Wraps
    /// <see cref="IConfigRegistry"/> + per-type <see cref="IConfigMetadataGrain"/> +
    /// generator-discovered <see cref="IConfigByteSource.Configs"/>. Project admin tools
    /// join the Orleans cluster as a client and call this grain through
    /// <c>IClusterClient.GetGrain&lt;IConfigAdminGrain&gt;(0)</c>.
    /// </summary>
    public class ConfigAdminGrain : Grain, IConfigAdminGrain
    {
        private readonly IConfigRegistry _registry;
        private readonly IConfigByteSource _byteSource;
        private readonly MetaTransportOptions? _transport;
        private readonly DefaultClientVersionService? _localVersionService;
        private readonly ILogger<ConfigAdminGrain> _logger;

        public ConfigAdminGrain(
            IConfigRegistry registry,
            IConfigByteSource byteSource,
            MetaTransportOptions? transport = null,
            DefaultClientVersionService? localVersionService = null,
            ILogger<ConfigAdminGrain>? logger = null)
        {
            _registry = registry;
            _byteSource = byteSource;
            _transport = transport;
            _localVersionService = localVersionService;
            _logger = logger ?? NullLogger<ConfigAdminGrain>.Instance;
        }

        private IConfigMetadataGrain MetadataGrain(string configName) =>
            GrainFactory.GetGrain<IConfigMetadataGrain>(configName);

        public Task<string[]> ListConfigNamesAsync()
        {
            var names = _byteSource.Configs.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            return Task.FromResult(names);
        }

        public async Task<ConfigOverview[]> ListConfigsAsync()
        {
            var result = new List<ConfigOverview>(_byteSource.Configs.Count);
            foreach (var entry in _byteSource.Configs)
                result.Add(await BuildOverviewAsync(entry));
            return result.ToArray();
        }

        public async Task<ConfigOverview?> GetConfigAsync(string name)
        {
            var entry = ResolveEntry(name);
            if (entry == null) return null;
            return await BuildOverviewAsync(entry);
        }

        public async Task<byte[]> DownloadAsync(string name, string version)
        {
            var entry = ResolveEntry(name)
                ?? throw new InvalidOperationException($"Config '{name}' is not registered.");
            var v = MetaConfigVersion.Parse(version);
            var bytes = await _registry.GetAsync(entry.ConfigType, v);
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException($"{name} v{version} is not published.");
            return bytes;
        }

        public async Task<ConfigOverview> UploadAsync(
            string name, string version, byte[] bytes, string origin, string publishedBy, string? notes = null, bool failOnDrift = false)
        {
            if (bytes is null) throw new ArgumentNullException(nameof(bytes));
            var entry = ResolveEntry(name)
                ?? throw new InvalidOperationException($"Config '{name}' is not registered.");

            var v = MetaConfigVersion.Parse(version);
            var canonical = v.ToString();

            var outcome = await _registry.PublishIfChangedAsync(entry.ConfigType, v, bytes, failOnDrift);
            await MetadataGrain(name).RecordPublishAsync(canonical, bytes.Length, origin ?? "", publishedBy ?? "", notes);

            _logger.LogInformation(
                "ConfigAdmin: upload {Name} v{Version} ({Bytes} B, origin={Origin}, by={By}) → {Outcome}",
                name, canonical, bytes.Length, origin, publishedBy, outcome);

            return await BuildOverviewAsync(entry);
        }

        public async Task<bool> UnpublishAsync(string name, string version, string deletedBy)
        {
            var entry = ResolveEntry(name);
            if (entry == null) return false;
            var v = MetaConfigVersion.Parse(version);
            await _registry.UnpublishAsync(entry.ConfigType, v);
            await MetadataGrain(name).RemoveAsync(v.ToString());
            _logger.LogInformation(
                "ConfigAdmin: unpublished {Name} v{Version} by {By}", name, v.ToString(), deletedBy);
            return true;
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

        /// <summary>
        /// Resolve a config name into its compile-time <see cref="ConfigTypeEntry"/>. Accepts
        /// either the canonical <see cref="ConfigTypeEntry.Name"/> (FullName) or the short
        /// <see cref="ConfigTypeEntry.DisplayName"/> — admin UIs can pass whichever they
        /// have without forcing a server lookup table.
        /// </summary>
        private ConfigTypeEntry? ResolveEntry(string name)
        {
            foreach (var c in _byteSource.Configs)
            {
                if (string.Equals(c.Name, name, StringComparison.Ordinal)
                    || string.Equals(c.DisplayName, name, StringComparison.Ordinal))
                    return c;
            }
            return null;
        }

        private async Task<ConfigOverview> BuildOverviewAsync(ConfigTypeEntry entry)
        {
            var registryVersions = await _registry.ListVersionsAsync(entry.ConfigType);
            var metadataRecords = await MetadataGrain(entry.Name).ListAsync();
            var metadataByVersion = metadataRecords.ToDictionary(r => r.Version, StringComparer.Ordinal);

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
                })
                .ToArray();

            return new ConfigOverview
            {
                ConfigName = entry.Name,
                DisplayName = entry.DisplayName,
                Branches = branches,
            };
        }
    }
}
