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
                // LoadIfHashDiff computes the target version from the registry + content diff (not from
                // the reported version) and must always materialize bytes to compare — a different shape
                // than the reported-version-keyed strategies below, so it gets its own path.
                if (_owner._options.Strategy == ConfigSeedStrategy.LoadIfHashDiff)
                {
                    await _owner.HandleHashDiffAsync<TConfig>(fullName, ct).ConfigureAwait(false);
                    return;
                }

                var log = _owner._logger;
                var version = await _owner._bootstrapper.GetVersionAsync<TConfig>(ct).ConfigureAwait(false);
                if (version == null)
                {
                    log.LogDebug("ConfigBootstrap: bootstrapper has no version for {Name}", fullName);
                    return;
                }

                if (await _owner.IsBranchHeldAsync(fullName, version.Value).ConfigureAwait(false))
                {
                    log.LogInformation(
                        "ConfigBootstrap: {Name} branch {Branch} is held — skipping seed", fullName, version.Value.GetBranchKey());
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

                await _owner.RecordAuditAsync(fullName, version.Value, seed).ConfigureAwait(false);

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

        /// <summary>
        /// <see cref="ConfigSeedStrategy.LoadIfHashDiff"/> path. Treats the loader's reported version as
        /// a stable <c>Major.Minor</c> branch (reported patch ignored; <c>null</c> → latest branch in the
        /// registry) and lets the framework own the patch: identical content to the branch's latest patch
        /// is a no-op, changed content publishes <c>Major.Minor.(latestPatch+1)</c>.
        /// </summary>
        private async Task HandleHashDiffAsync<TConfig>(string fullName, CancellationToken ct) where TConfig : class
        {
            // 1. Resolve the target branch (Major.Minor). ListVersionsAsync is ascending.
            var reported = await _bootstrapper.GetVersionAsync<TConfig>(ct).ConfigureAwait(false);
            var existing = await _registry.ListVersionsAsync<TConfig>().ConfigureAwait(false);

            MetaConfigVersion branch;
            if (reported != null)
                branch = reported.Value;
            else if (existing.Count > 0)
                branch = existing[existing.Count - 1]; // latest overall → its Major.Minor is the latest branch
            else
            {
                _logger.LogDebug("ConfigBootstrap: {Name} LoadIfHashDiff — no reported version and empty registry, skip", fullName);
                return;
            }

            // Held branch → admin owns it; never overwrite / auto-bump on restart. Checked before the
            // bytes fetch so a held branch costs nothing extra.
            if (await IsBranchHeldAsync(fullName, branch).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "ConfigBootstrap: {Name} branch {Branch} is held — skipping seed", fullName, branch.GetBranchKey());
                return;
            }

            // 2. Materialize bytes (required to compare). Pass the version the loader knows about.
            ConfigBootstrapBytes? seed;
            try
            {
                seed = await _bootstrapper.GetBytesAsync<TConfig>(reported ?? branch, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConfigBootstrap: bootstrapper threw on {Name} (LoadIfHashDiff) — skipping seed", fullName);
                return;
            }

            if (seed == null || seed.Bytes.Length == 0)
            {
                _logger.LogDebug("ConfigBootstrap: no bytes for {Name} (LoadIfHashDiff)", fullName);
                return;
            }

            // 3. Highest patch already published in this branch (existing is ascending → last match wins).
            MetaConfigVersion? latestInBranch = null;
            foreach (var v in existing)
                if (v.Major == branch.Major && v.Minor == branch.Minor)
                    latestInBranch = v;

            // 4. Decide the target version and publish.
            MetaConfigVersion target;
            if (latestInBranch == null)
            {
                // First seed for this branch — publish at the reported version (patch as-is, e.g. M.m.0).
                target = branch;
            }
            else
            {
                var stored = await _registry.GetAsync<TConfig>(latestInBranch.Value).ConfigureAwait(false);
                if (stored != null
                    && stored.Length == seed.Bytes.Length
                    && new ReadOnlySpan<byte>(stored).SequenceEqual(seed.Bytes))
                {
                    _logger.LogDebug("ConfigBootstrap: {Name} LoadIfHashDiff — content matches v{Version}, no-op", fullName, latestInBranch.Value);
                    return;
                }

                target = new MetaConfigVersion(
                    latestInBranch.Value.Major, latestInBranch.Value.Minor, latestInBranch.Value.Patch + 1);
            }

            await _registry.PublishAsync<TConfig>(target, seed.Bytes).ConfigureAwait(false);
            await RecordAuditAsync(fullName, target, seed).ConfigureAwait(false);

            _logger.LogInformation(
                "ConfigBootstrap: {Name} v{Version} ({Size} B, {Origin}, LoadIfHashDiff) → Published",
                fullName, target, seed.Bytes.Length, seed.Origin);
        }

        /// <summary>
        /// True when the config's <c>Major.Minor</c> branch is held (admin-managed) — the seed must
        /// leave it alone so a manual upload isn't overwritten / auto-bumped on restart.
        /// </summary>
        private Task<bool> IsBranchHeldAsync(string fullName, MetaConfigVersion version)
            => _grains.GetGrain<IConfigMetadataGrain>(fullName).IsBranchHeldAsync(version.GetBranchKey());

        /// <summary>Record the publish audit row through the per-type metadata grain.</summary>
        private Task RecordAuditAsync(string fullName, MetaConfigVersion version, ConfigBootstrapBytes seed)
            => _grains.GetGrain<IConfigMetadataGrain>(fullName)
                .RecordPublishAsync(version.ToString(), seed.Bytes.Length, seed.Origin, seed.PublishedBy, seed.Notes);
    }
}
