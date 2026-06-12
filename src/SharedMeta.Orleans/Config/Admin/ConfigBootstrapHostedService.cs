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
    /// 0.27.0+ Cold-start config bootstrap with per-strategy gating.
    ///
    /// <para>
    /// For each <c>[MetaConfig]</c> type exposed via <see cref="IConfigByteSource.Configs"/>,
    /// consults <see cref="ConfigsOptions.Strategy"/> to decide whether to invoke the
    /// project's <see cref="IConfigBootstrapper"/>, then publishes via
    /// <see cref="ConfigRegistryExtensions.PublishIfChangedAsync"/>. After all types are
    /// processed, warms up every <c>BroadcastingConfigProvider&lt;TConfig&gt;</c>.
    /// </para>
    ///
    /// <para>
    /// Strategies:
    /// <list type="bullet">
    /// <item><see cref="ConfigSeedStrategy.LoadIfEmpty"/>: skip the loader entirely when the registry already has any version.</item>
    /// <item><see cref="ConfigSeedStrategy.LoadIfNew"/>: call the loader, publish only when the returned version is unknown to the registry.</item>
    /// <item><see cref="ConfigSeedStrategy.LoadAlways"/>: always call the loader and run PublishIfChangedAsync (idempotent on same content).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <see cref="ConfigsOptions.OnBeforeSeed"/> fires once at the very start of
    /// <see cref="StartAsync"/> — typical hook for "dev YAML → bin" compile passes that
    /// must populate the seed directory before the loader scans it.
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
            if (_options.OnBeforeSeed != null)
            {
                try
                {
                    await _options.OnBeforeSeed(_sp, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ConfigBootstrap: OnBeforeSeed threw — continuing with whatever the loader sees");
                }
            }

            foreach (var entry in _byteSource.Configs)
            {
                // LoadIfEmpty: skip the loader entirely when the registry has anything.
                if (_options.Strategy == ConfigSeedStrategy.LoadIfEmpty)
                {
                    var existing = await _registry.ListVersionsAsync(entry.ConfigType).ConfigureAwait(false);
                    if (existing.Count > 0)
                    {
                        _logger.LogDebug(
                            "ConfigBootstrap: {Name} LoadIfEmpty — registry has {Count} version(s), skip",
                            entry.Name, existing.Count);
                        continue;
                    }
                }

                ConfigBootstrapSeed? seed;
                try
                {
                    seed = await _bootstrapper.LoadAsync(entry.ConfigType, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ConfigBootstrap: bootstrapper threw on {Name} — skipping seed", entry.Name);
                    continue;
                }

                if (seed == null || seed.Bytes.Length == 0)
                {
                    _logger.LogDebug("ConfigBootstrap: no seed for {Name}", entry.Name);
                    continue;
                }

                // LoadIfNew: skip publish when the registry already has that exact version.
                if (_options.Strategy == ConfigSeedStrategy.LoadIfNew)
                {
                    var existing = await _registry.ListVersionsAsync(entry.ConfigType).ConfigureAwait(false);
                    if (existing.Contains(seed.Version))
                    {
                        _logger.LogDebug(
                            "ConfigBootstrap: {Name} v{Version} already in registry — skip",
                            entry.Name, seed.Version);
                        continue;
                    }
                }

                var outcome = await _registry.PublishIfChangedAsync(entry.ConfigType, seed.Version, seed.Bytes)
                    .ConfigureAwait(false);

                await _grains.GetGrain<IConfigMetadataGrain>(entry.Name)
                    .RecordPublishAsync(
                        seed.Version.ToString(),
                        seed.Bytes.Length,
                        seed.Origin,
                        seed.PublishedBy,
                        seed.Notes)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "ConfigBootstrap: {Name} v{Version} ({Size} B, {Origin}, {Strategy}) → {Outcome}",
                    entry.Name, seed.Version, seed.Bytes.Length, seed.Origin, _options.Strategy, outcome);
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
    }
}
