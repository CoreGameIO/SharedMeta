using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Config;
using SharedMeta.Server.Core.Config.Admin;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Cold-start config bootstrap with per-strategy gating. Two-phase from 0.27.1:
    /// asks the bootstrapper for a version first, decides per
    /// <see cref="ConfigsOptions.Strategy"/> whether a publish is needed, only then fetches
    /// bytes.
    ///
    /// <para>
    /// For each <c>[MetaConfig]</c> type exposed via <see cref="IConfigByteSource.Configs"/>:
    /// </para>
    /// <list type="number">
    /// <item><c>version = await bootstrapper.GetVersionAsync(type)</c> — <c>null</c> skips the type.</item>
    /// <item>Strategy gate (<see cref="ConfigSeedStrategy.LoadIfEmpty"/> / <see cref="ConfigSeedStrategy.LoadIfNew"/> / <see cref="ConfigSeedStrategy.LoadAlways"/>) — decides if we need bytes at all.</item>
    /// <item><c>bytes = await bootstrapper.GetBytesAsync(type, version)</c> — only if step 2 said yes.</item>
    /// <item>Publish via <see cref="ConfigRegistryExtensions.PublishIfChangedAsync"/> + audit row.</item>
    /// </list>
    ///
    /// <para>
    /// After all types are processed, warms every <c>BroadcastingConfigProvider&lt;TConfig&gt;</c>.
    /// </para>
    /// </summary>
    public sealed class ConfigBootstrapHostedService : IHostedService
    {
        private readonly IServiceProvider _sp;
        private readonly IConfigBootstrapper _bootstrapper;
        private readonly IConfigByteSource _byteSource;
        private readonly IConfigRegistry _registry;
        private readonly IGrainFactory _grains;
        private readonly ConfigsOptions _options;
        private readonly ILogger<ConfigBootstrapHostedService> _logger;

        public ConfigBootstrapHostedService(
            IServiceProvider sp,
            IConfigBootstrapper bootstrapper,
            IConfigByteSource byteSource,
            IConfigRegistry registry,
            IGrainFactory grains,
            IOptions<ConfigsOptions> options,
            ILogger<ConfigBootstrapHostedService>? logger = null)
        {
            _sp = sp;
            _bootstrapper = bootstrapper;
            _byteSource = byteSource;
            _registry = registry;
            _grains = grains;
            _options = options.Value;
            _logger = logger ?? NullLogger<ConfigBootstrapHostedService>.Instance;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var entry in _byteSource.Configs)
            {
                var version = await _bootstrapper.GetVersionAsync(entry.ConfigType, cancellationToken).ConfigureAwait(false);
                if (version == null)
                {
                    _logger.LogDebug("ConfigBootstrap: bootstrapper has no version for {Name}", entry.Name);
                    continue;
                }

                if (!await ShouldSeedAsync(entry, version.Value).ConfigureAwait(false))
                    continue;

                ConfigBootstrapBytes? seed;
                try
                {
                    seed = await _bootstrapper.GetBytesAsync(entry.ConfigType, version.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ConfigBootstrap: bootstrapper threw on {Name} v{Version} — skipping seed",
                        entry.Name, version);
                    continue;
                }

                if (seed == null || seed.Bytes.Length == 0)
                {
                    _logger.LogDebug("ConfigBootstrap: no bytes for {Name} v{Version}", entry.Name, version);
                    continue;
                }

                var outcome = await _registry.PublishIfChangedAsync(entry.ConfigType, version.Value, seed.Bytes)
                    .ConfigureAwait(false);

                await _grains.GetGrain<IConfigMetadataGrain>(entry.Name)
                    .RecordPublishAsync(
                        version.Value.ToString(),
                        seed.Bytes.Length,
                        seed.Origin,
                        seed.PublishedBy,
                        seed.Notes)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "ConfigBootstrap: {Name} v{Version} ({Size} B, {Origin}, {Strategy}) → {Outcome}",
                    entry.Name, version, seed.Bytes.Length, seed.Origin, _options.Strategy, outcome);
            }

            // Warm up broadcasting providers: subscribe to directory grains + pull initial
            // known-versions snapshot.
            var configTypes = _byteSource.Configs.Select(c => c.ConfigType).ToArray();
            if (configTypes.Length > 0)
            {
                await _sp.WarmUpConfigProvidersAsync(configTypes).ConfigureAwait(false);
                _logger.LogInformation("ConfigBootstrap: warmed up {Count} broadcasting providers", configTypes.Length);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Strategy gate. Cheap when grain already has versions (single list query) — the
        /// expensive bytes fetch happens only when this returns <c>true</c>.
        /// </summary>
        private async Task<bool> ShouldSeedAsync(ConfigTypeEntry entry, SharedMeta.Core.MetaConfigVersion version)
        {
            switch (_options.Strategy)
            {
                case ConfigSeedStrategy.LoadAlways:
                    return true;

                case ConfigSeedStrategy.LoadIfEmpty:
                {
                    var existing = await _registry.ListVersionsAsync(entry.ConfigType).ConfigureAwait(false);
                    if (existing.Count > 0)
                    {
                        _logger.LogDebug(
                            "ConfigBootstrap: {Name} LoadIfEmpty — registry has {Count} version(s), skip",
                            entry.Name, existing.Count);
                        return false;
                    }
                    return true;
                }

                case ConfigSeedStrategy.LoadIfNew:
                {
                    var existing = await _registry.ListVersionsAsync(entry.ConfigType).ConfigureAwait(false);
                    if (existing.Contains(version))
                    {
                        _logger.LogDebug(
                            "ConfigBootstrap: {Name} v{Version} already in registry — skip",
                            entry.Name, version);
                        return false;
                    }
                    return true;
                }

                default:
                    throw new InvalidOperationException($"Unknown ConfigSeedStrategy: {_options.Strategy}");
            }
        }
    }
}
