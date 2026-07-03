using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans;
using SharedMeta.Core;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Orleans.Config;
using SharedMeta.Orleans.Config.Admin;
using SharedMeta.Server.Core.Config;
using SharedMeta.Server.Core.Config.Admin;
using Xunit;

namespace SharedMeta.IntegrationTests;

// One config type per test — grain key is configType.FullName, and the TestCluster fixture is
// shared across the collection, so a shared type would let tests observe each other's versions.
public sealed class HashDiffSeedConfig { }
public sealed class HashDiffNullBranchConfig { }
public sealed class HashDiffNullEmptyConfig { }
public sealed class HashDiffHeldConfig { }

/// <summary>
/// 0.32.0 — <see cref="ConfigSeedStrategy.LoadIfHashDiff"/>: the loader reports a stable
/// Major.Minor branch and the framework owns the patch — identical content is a no-op,
/// changed content auto-publishes Major.Minor.(latestPatch+1). Driven end-to-end through the
/// real grain-backed <see cref="GrainConfigRegistry"/> with a fake bootstrapper + catalog.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ConfigSeedStrategyTests
{
    private readonly TestClusterFixture _fixture;

    public ConfigSeedStrategyTests(TestClusterFixture fixture) => _fixture = fixture;

    [Fact(Timeout = 30_000)]
    public async Task LoadIfHashDiff_SeedsThenAutoBumpsPatchOnContentChange()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var bs = new FakeBootstrapper { Version = new MetaConfigVersion(1, 2, 0) };
        var a = new byte[] { 1, 1, 1 };
        var b = new byte[] { 2, 2, 2 };
        var c = new byte[] { 3, 3, 3 };

        // Empty registry + reported 1.2.0 → first seed at 1.2.0.
        bs.Bytes = a;
        await RunSeed<HashDiffSeedConfig>(bs, registry);
        Assert.Equal(new[] { new MetaConfigVersion(1, 2, 0) },
            await registry.ListVersionsAsync(typeof(HashDiffSeedConfig)));

        // Same bytes on restart → no new version.
        await RunSeed<HashDiffSeedConfig>(bs, registry);
        Assert.Equal(new[] { new MetaConfigVersion(1, 2, 0) },
            await registry.ListVersionsAsync(typeof(HashDiffSeedConfig)));

        // Changed bytes → 1.2.1 (loader still reports 1.2.0 — framework owns the patch).
        bs.Bytes = b;
        await RunSeed<HashDiffSeedConfig>(bs, registry);
        Assert.Equal(
            new[] { new MetaConfigVersion(1, 2, 0), new MetaConfigVersion(1, 2, 1) },
            await registry.ListVersionsAsync(typeof(HashDiffSeedConfig)));

        // Changed again → 1.2.2.
        bs.Bytes = c;
        await RunSeed<HashDiffSeedConfig>(bs, registry);
        Assert.Equal(
            new[] { new MetaConfigVersion(1, 2, 0), new MetaConfigVersion(1, 2, 1), new MetaConfigVersion(1, 2, 2) },
            await registry.ListVersionsAsync(typeof(HashDiffSeedConfig)));

        Assert.Equal(c, await registry.GetAsync(typeof(HashDiffSeedConfig), new MetaConfigVersion(1, 2, 2)));
    }

    [Fact(Timeout = 30_000)]
    public async Task LoadIfHashDiff_NullVersion_TargetsLatestBranchAndBumps()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var x = new byte[] { 9, 9 };
        var y = new byte[] { 8, 8 };

        // Pre-existing latest 1.2.3 with content X (as if published earlier).
        await registry.PublishAsync(typeof(HashDiffNullBranchConfig), new MetaConfigVersion(1, 2, 3), x);

        // Loader reports null → latest branch (1.2). Content differs → 1.2.4.
        var bs = new FakeBootstrapper { Version = null, Bytes = y };
        await RunSeed<HashDiffNullBranchConfig>(bs, registry);

        Assert.Equal(
            new[] { new MetaConfigVersion(1, 2, 3), new MetaConfigVersion(1, 2, 4) },
            await registry.ListVersionsAsync(typeof(HashDiffNullBranchConfig)));
        Assert.Equal(y, await registry.GetAsync(typeof(HashDiffNullBranchConfig), new MetaConfigVersion(1, 2, 4)));
    }

    [Fact(Timeout = 30_000)]
    public async Task LoadIfHashDiff_NullVersion_EmptyRegistry_NoOp()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var bs = new FakeBootstrapper { Version = null, Bytes = new byte[] { 7 } };

        await RunSeed<HashDiffNullEmptyConfig>(bs, registry);

        Assert.Empty(await registry.ListVersionsAsync(typeof(HashDiffNullEmptyConfig)));
    }

    [Fact(Timeout = 30_000)]
    public async Task LoadIfHashDiff_HeldBranch_IsNotBumpedUntilReleased()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var bs = new FakeBootstrapper { Version = new MetaConfigVersion(1, 2, 0), Bytes = new byte[] { 1 } };

        await RunSeed<HashDiffHeldConfig>(bs, registry);
        Assert.Equal(new[] { new MetaConfigVersion(1, 2, 0) },
            await registry.ListVersionsAsync(typeof(HashDiffHeldConfig)));

        // Admin holds the 1.2 branch (as if a manual upload into it should stick across restarts).
        var meta = _fixture.GrainFactory.GetGrain<IConfigMetadataGrain>(typeof(HashDiffHeldConfig).FullName!);
        await meta.SetBranchHoldAsync("1.2", true);
        Assert.True(await meta.IsBranchHeldAsync("1.2"));
        Assert.Contains("1.2", await meta.ListHeldBranchesAsync());

        // Changed content must NOT auto-bump while the branch is held.
        bs.Bytes = new byte[] { 2 };
        await RunSeed<HashDiffHeldConfig>(bs, registry);
        Assert.Equal(new[] { new MetaConfigVersion(1, 2, 0) },
            await registry.ListVersionsAsync(typeof(HashDiffHeldConfig)));

        // Release the hold → the change flows again as 1.2.1.
        await meta.SetBranchHoldAsync("1.2", false);
        Assert.False(await meta.IsBranchHeldAsync("1.2"));
        await RunSeed<HashDiffHeldConfig>(bs, registry);
        Assert.Equal(
            new[] { new MetaConfigVersion(1, 2, 0), new MetaConfigVersion(1, 2, 1) },
            await registry.ListVersionsAsync(typeof(HashDiffHeldConfig)));
    }

    private async Task RunSeed<TConfig>(IConfigBootstrapper bootstrapper, IConfigRegistry registry)
        where TConfig : class
    {
        var svc = new ConfigBootstrapHostedService(
            new NullServiceProvider(),
            bootstrapper,
            new SingleTypeCatalog<TConfig>(),
            registry,
            _fixture.GrainFactory,
            Options.Create(new ConfigsOptions { Strategy = ConfigSeedStrategy.LoadIfHashDiff }));
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);
    }

    private sealed class FakeBootstrapper : IConfigBootstrapper
    {
        public MetaConfigVersion? Version;
        public byte[]? Bytes;

        public Task<MetaConfigVersion?> GetVersionAsync<TConfig>(CancellationToken ct) where TConfig : class
            => Task.FromResult(Version);

        public Task<ConfigBootstrapBytes?> GetBytesAsync<TConfig>(MetaConfigVersion version, CancellationToken ct) where TConfig : class
            => Task.FromResult(Bytes == null ? null : new ConfigBootstrapBytes { Bytes = Bytes });
    }

    // Visits exactly one config type for seeding; empty Entries skips the (irrelevant) warm-up pass.
    private sealed class SingleTypeCatalog<TConfig> : IConfigCatalog where TConfig : class
    {
        public IReadOnlyList<ConfigCatalogEntry> Entries { get; } = Array.Empty<ConfigCatalogEntry>();

        public Task ForEachAsync(IConfigCatalogHandler handler, CancellationToken cancellationToken = default)
            => handler.HandleAsync<TConfig>(typeof(TConfig).FullName!, typeof(TConfig).Name, cancellationToken);

        public Task<bool> TryDispatchAsync(string name, IConfigCatalogHandler handler, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
