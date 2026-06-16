using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Server.Core.Config;
using SharedMeta.Server.Core.Config.Admin;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Cold-start config bootstrap with per-strategy gating. 0.28.0: catalog-driven —
    /// the generator-emitted <see cref="IConfigCatalog"/> closes <c>TConfig</c> at compile time
    /// for each <c>[MetaConfig]</c> entry, so this hosted service uses typed dispatch
    /// (<see cref="IConfigBootstrapper.GetVersionAsync{TConfig}"/> / <c>GetBytesAsync</c>) and
    /// the typed <see cref="IConfigRegistry"/> generic extensions — no reflection, no
    /// <c>Type</c> arguments end-to-end.
    ///
    /// <para>Per <c>[MetaConfig]</c> entry the catalog visits:</para>
    /// <list type="number">
    /// <item><c>version = await bootstrapper.GetVersionAsync&lt;TConfig&gt;()</c> — <c>null</c> skips the type.</item>
    /// <item>Strategy gate (<see cref="ConfigSeedStrategy.LoadIfEmpty"/> / <see cref="ConfigSeedStrategy.LoadIfNew"/> / <see cref="ConfigSeedStrategy.LoadAlways"/>) — decides if we need bytes at all.</item>
    /// <item><c>bytes = await bootstrapper.GetBytesAsync&lt;TConfig&gt;(version)</c> — only if step 2 said yes.</item>
    /// <item>Publish via <see cref="ConfigRegistryExtensions.PublishIfChangedAsync"/> + audit row through <see cref="IConfigMetadataGrain"/>.</item>
    /// </list>
    ///
    /// <para>After all entries are processed, warms every <c>BroadcastingConfigProvider&lt;TConfig&gt;</c>
    /// through the same catalog visitor.</para>
    /// </summary>
    public sealed class ConfigBootstrapHostedService : IHostedService
    {
        private readonly IServiceProvider _sp;
        private readonly IConfigBootstrapper _bootstrapper;
        private readonly IConfigCatalog _catalog;
        private readonly IConfigRegistry _registry;
        private readonly IGrainFactory _grains;
        private readonly ConfigsOptions _options;
        private readonly ILogger<ConfigBootstrapHostedService> _logger;

        public ConfigBootstrapHostedService(
            IServiceProvider sp,
            IConfigBootstrapper bootstrapper,
            IConfigCatalog catalog,
            IConfigRegistry registry,
            IGrainFactory grains,
            IOptions<ConfigsOptions> options,
            ILogger<ConfigBootstrapHostedService>? logger = null)
        {
            _sp = sp;
            _bootstrapper = bootstrapper;
            _catalog = catalog;
            _registry = registry;
            _grains = grains;
            _options = options.Value;
            _logger = logger ?? NullLogger<ConfigBootstrapHostedService>.Instance;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var seeder = new SeedHandler(this);
            await _catalog.ForEachAsync(seeder, cancellationToken).ConfigureAwait(false);

            if (_catalog.Entries.Count > 0)
            {
                var warmer = new WarmupHandler(_sp);
                await _catalog.ForEachAsync(warmer, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("ConfigBootstrap: warmed up {Count} broadcasting providers", _catalog.Entries.Count);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Typed seed visitor — one <c>HandleAsync&lt;TConfig&gt;</c> call per catalog entry.
        /// Bootstrapper invocations and registry queries are routed through generic methods
        /// closed at the catalog dispatch site.
        /// </summary>
        private sealed class SeedHandler : IConfigCatalogHandler
        {
            private readonly ConfigBootstrapHostedService _owner;
            public SeedHandler(ConfigBootstrapHostedService owner) => _owner = owner;

            public async Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken ct) where TConfig : class
            {
                var log = _owner._logger;
                var version = await _owner._bootstrapper.GetVersionAsync<TConfig>(ct).ConfigureAwait(false);
                if (version == null)
                {
                    log.LogDebug("ConfigBootstrap: bootstrapper has no version for {Name}", fullName);
                    return;
                }

                if (!await _owner.ShouldSeedAsync<TConfig>(fullName, version.Value).ConfigureAwait(false))
                    return;

                ConfigBootstrapBytes? seed;
                try
                {
                    seed = await _owner._bootstrapper.GetBytesAsync<TConfig>(version.Value, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogError(ex,
                        "ConfigBootstrap: bootstrapper threw on {Name} v{Version} — skipping seed",
                        fullName, version);
                    return;
                }

                if (seed == null || seed.Bytes.Length == 0)
                {
                    log.LogDebug("ConfigBootstrap: no bytes for {Name} v{Version}", fullName, version);
                    return;
                }

                var outcome = await _owner._registry.PublishIfChangedAsync<TConfig>(version.Value, seed.Bytes).ConfigureAwait(false);

                await _owner._grains.GetGrain<IConfigMetadataGrain>(fullName)
                    .RecordPublishAsync(
                        version.Value.ToString(),
                        seed.Bytes.Length,
                        seed.Origin,
                        seed.PublishedBy,
                        seed.Notes)
                    .ConfigureAwait(false);

                log.LogInformation(
                    "ConfigBootstrap: {Name} v{Version} ({Size} B, {Origin}, {Strategy}) → {Outcome}",
                    fullName, version, seed.Bytes.Length, seed.Origin, _owner._options.Strategy, outcome);
            }
        }

        /// <summary>
        /// Typed warm-up visitor — resolves <c>BroadcastingConfigProvider&lt;TConfig&gt;</c>
        /// directly through DI (no reflection) and calls <c>InitializeAsync</c>.
        /// </summary>
        private sealed class WarmupHandler : IConfigCatalogHandler
        {
            private readonly IServiceProvider _sp;
            public WarmupHandler(IServiceProvider sp) => _sp = sp;

            public async Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken ct) where TConfig : class
            {
                var provider = _sp.GetService<BroadcastingConfigProvider<TConfig>>();
                if (provider == null) return;
                await provider.InitializeAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Strategy gate. Cheap when grain already has versions (single list query) — the
        /// expensive bytes fetch happens only when this returns <c>true</c>.
        /// </summary>
        private async Task<bool> ShouldSeedAsync<TConfig>(string fullName, MetaConfigVersion version) where TConfig : class
        {
            switch (_options.Strategy)
            {
                case ConfigSeedStrategy.LoadAlways:
                    return true;

                case ConfigSeedStrategy.LoadIfEmpty:
                {
                    var existing = await _registry.ListVersionsAsync<TConfig>().ConfigureAwait(false);
                    if (existing.Count > 0)
                    {
                        _logger.LogDebug(
                            "ConfigBootstrap: {Name} LoadIfEmpty — registry has {Count} version(s), skip",
                            fullName, existing.Count);
                        return false;
                    }
                    return true;
                }

                case ConfigSeedStrategy.LoadIfNew:
                {
                    var existing = await _registry.ListVersionsAsync<TConfig>().ConfigureAwait(false);
                    if (existing.Contains(version))
                    {
                        _logger.LogDebug(
                            "ConfigBootstrap: {Name} v{Version} already in registry — skip",
                            fullName, version);
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
