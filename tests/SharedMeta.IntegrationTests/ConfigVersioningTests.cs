using MemoryPack;
using SharedMeta.Core;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Orleans.Config;
using SharedMeta.Server.Core.Config;
using Xunit;

namespace SharedMeta.IntegrationTests;

// Per-test config types so each [Fact] gets its own ConfigDirectoryGrain (grain key =
// configType.FullName). The TestCluster fixture is shared across the collection — using a
// single config type would let tests observe each other's published versions and any stale
// observer references would leak into the next test's fan-out. One type per test = one
// freshly-activated grain per test = no cross-test pollution.
[MemoryPackable] public partial class VersioningTestConfigRoundtrip { [MemoryPackOrder(0)] public int Knob { get; set; } }
[MemoryPackable] public partial class VersioningTestConfigCache    { [MemoryPackOrder(0)] public int Knob { get; set; } }
[MemoryPackable] public partial class VersioningTestConfigObserver { [MemoryPackOrder(0)] public int Knob { get; set; } }
[MemoryPackable] public partial class VersioningTestConfigRepublish { [MemoryPackOrder(0)] public int Knob { get; set; } }
[MemoryPackable] public partial class VersioningTestConfigUnpublish { [MemoryPackOrder(0)] public int Knob { get; set; } }
[MemoryPackable] public partial class VersioningTestConfigSyncRepublish { [MemoryPackOrder(0)] public int Knob { get; set; } }
[MemoryPackable] public partial class VersioningTestConfigNewPublish { [MemoryPackOrder(0)] public int Knob { get; set; } }

/// <summary>
/// 0.21.0: grain-backed <see cref="IConfigRegistry"/> + observer-pattern
/// <see cref="BroadcastingConfigProvider{TConfig}"/>. End-to-end exercises:
/// publish → list → get → unpublish + observer fan-out into a live provider so the
/// cache-invalidation path is real (not a unit-test stub).
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ConfigVersioningTests
{
    private readonly TestClusterFixture _fixture;

    public ConfigVersioningTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task Registry_PublishGetList_RoundTrip()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var v1 = new MetaConfigVersion(1, 0, 0);
        var v2 = new MetaConfigVersion(2, 0, 0);

        await registry.PublishAsync(v1, new VersioningTestConfigRoundtrip { Knob = 100 }, _fixture.Serializer);
        await registry.PublishAsync(v2, new VersioningTestConfigRoundtrip { Knob = 200 }, _fixture.Serializer);

        var loaded1 = await registry.GetAsync<VersioningTestConfigRoundtrip>(v1, _fixture.Serializer);
        var loaded2 = await registry.GetAsync<VersioningTestConfigRoundtrip>(v2, _fixture.Serializer);
        Assert.Equal(100, loaded1!.Knob);
        Assert.Equal(200, loaded2!.Knob);

        var versions = await registry.ListVersionsAsync(typeof(VersioningTestConfigRoundtrip));
        Assert.Equal(new[] { v1, v2 }, versions);
    }

    [Fact(Timeout = 30_000)]
    public async Task Provider_GetConfigAsync_FetchesFromRegistryAndCaches()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var v = new MetaConfigVersion(3, 1, 0);
        await registry.PublishAsync(v, new VersioningTestConfigCache { Knob = 42 }, _fixture.Serializer);

        await using var provider = new BroadcastingConfigProvider<VersioningTestConfigCache>(
            registry, _fixture.Serializer, _fixture.GrainFactory);
        await provider.InitializeAsync();

        // First call — cache miss, fetches from registry
        var c1 = await provider.GetConfigAsync(v);
        Assert.Equal(42, c1.Knob);

        // Second call — cache hit, same instance
        var c2 = await provider.GetConfigAsync(v);
        Assert.Same(c1, c2);
    }

    [Fact(Timeout = 30_000)]
    public async Task Provider_ReceivesObserverNotification_OnPublish()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);

        await using var provider = new BroadcastingConfigProvider<VersioningTestConfigObserver>(
            registry, _fixture.Serializer, _fixture.GrainFactory);
        await provider.InitializeAsync();

        var v = new MetaConfigVersion(4, 0, 0);
        await registry.PublishAsync(v, new VersioningTestConfigObserver { Knob = 777 }, _fixture.Serializer);

        // [OneWay] callback is fire-and-forget on the grain side. Observable proof that the
        // observer fired: ResolveLatestMatching(4, 0) lights up only after the provider's
        // known-versions set learns about v (it has to — there is no fallback to "current"
        // anymore in 0.21.0). Cache-miss fallback to the registry would also work for the
        // bytes, but ResolveLatestMatching is purely in-memory so it isolates the observer
        // path from any registry round-trip.
        await WaitUntilAsync(() =>
        {
            try { return provider.ResolveLatestMatching(4, 0) == v; }
            catch { return false; }
        }, TimeSpan.FromSeconds(10));

        Assert.Equal(v, provider.ResolveLatestMatching(4, 0));
        var fetched = await provider.GetConfigAsync(v);
        Assert.Equal(777, fetched.Knob);
    }

    [Fact(Timeout = 30_000)]
    public async Task Provider_RepublishInvalidatesCache_NewBytesServed()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var v = new MetaConfigVersion(5, 0, 0);
        await registry.PublishAsync(v, new VersioningTestConfigRepublish { Knob = 1 }, _fixture.Serializer);

        await using var provider = new BroadcastingConfigProvider<VersioningTestConfigRepublish>(
            registry, _fixture.Serializer, _fixture.GrainFactory);
        await provider.InitializeAsync();
        var first = await provider.GetConfigAsync(v);
        Assert.Equal(1, first.Knob);

        // Republish the SAME version with different bytes — observer fires OnConfigPublished
        // and the provider must invalidate the cache entry so the next read returns the new
        // bytes, not the cached object.
        await registry.PublishAsync(v, new VersioningTestConfigRepublish { Knob = 999 }, _fixture.Serializer);

        // GetConfigAsync polled until the cache is invalidated and refetch returns Knob=999.
        await WaitUntilAsync(async () =>
        {
            try { var c = await provider.GetConfigAsync(v); return c.Knob == 999; }
            catch { return false; }
        }, TimeSpan.FromSeconds(10));

        var second = await provider.GetConfigAsync(v);
        Assert.Equal(999, second.Knob);
        Assert.NotSame(first, second);
    }

    [Fact(Timeout = 30_000)]
    public async Task Provider_Unpublish_DropsFromKnownVersionsAndCache()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var v = new MetaConfigVersion(6, 0, 0);
        await registry.PublishAsync(v, new VersioningTestConfigUnpublish { Knob = 7 }, _fixture.Serializer);

        await using var provider = new BroadcastingConfigProvider<VersioningTestConfigUnpublish>(
            registry, _fixture.Serializer, _fixture.GrainFactory);
        await provider.InitializeAsync();
        await provider.GetConfigAsync(v); // populate cache
        // 0.21.0: ResolveLatestMatching reflects the in-memory known set (no CurrentVersion).
        Assert.Equal(v, provider.ResolveLatestMatching(6, 0));

        await registry.UnpublishAsync(typeof(VersioningTestConfigUnpublish), v);

        // Wait for the unpublish notification — the version is removed from the known set
        // and from the cache. Both consequences are observable: ResolveLatestMatching(6, 0)
        // starts to throw (no patch in that branch), and GetConfigAsync(v) throws too
        // (registry no longer has it).
        await WaitUntilAsync(() =>
        {
            try { provider.ResolveLatestMatching(6, 0); return false; }
            catch (InvalidOperationException) { return true; }
        }, TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.GetConfigAsync(v));
    }

    /// <summary>
    /// Entity grains read the provider through the sync <c>GetConfig</c>, which cannot fetch. A
    /// republish used to drop the entry and leave it empty until something awaited
    /// <c>GetConfigAsync</c>; the sync read must instead keep serving — old bytes until the
    /// refresh lands, new bytes after — and never miss.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Provider_Republish_SyncGetConfigNeverMisses()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var v = new MetaConfigVersion(7, 0, 0);
        await registry.PublishAsync(v, new VersioningTestConfigSyncRepublish { Knob = 1 }, _fixture.Serializer);

        await using var provider = new BroadcastingConfigProvider<VersioningTestConfigSyncRepublish>(
            registry, _fixture.Serializer, _fixture.GrainFactory);
        await provider.InitializeAsync();
        await provider.GetConfigAsync(v);
        var generation = provider.PublishGeneration;

        await registry.PublishAsync(v, new VersioningTestConfigSyncRepublish { Knob = 999 }, _fixture.Serializer);

        // GetConfig throwing fails the test immediately instead of being retried.
        await WaitUntilAsync(() => provider.GetConfig(v).Knob == 999, TimeSpan.FromSeconds(10));
        Assert.True(provider.PublishGeneration > generation);
    }

    /// <summary>
    /// A version published while the provider runs — never fetched on this silo — must be readable
    /// through the sync <c>GetConfig</c> by the time it becomes resolvable. Otherwise an unpinned
    /// entity resolving "latest patch" switches to it and fails every call.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Provider_NewPublish_ResolvableOnlyOnceMaterialized()
    {
        var registry = new GrainConfigRegistry(_fixture.GrainFactory);
        var v1 = new MetaConfigVersion(8, 0, 1);
        var v2 = new MetaConfigVersion(8, 0, 2);
        await registry.PublishAsync(v1, new VersioningTestConfigNewPublish { Knob = 1 }, _fixture.Serializer);

        await using var provider = new BroadcastingConfigProvider<VersioningTestConfigNewPublish>(
            registry, _fixture.Serializer, _fixture.GrainFactory);
        await provider.InitializeAsync();
        await provider.GetConfigAsync(v1);

        await registry.PublishAsync(v2, new VersioningTestConfigNewPublish { Knob = 2 }, _fixture.Serializer);

        await WaitUntilAsync(() =>
        {
            var latest = provider.ResolveLatestMatching(8, 0);
            var config = provider.GetConfig(latest); // must never miss for a resolvable version
            return latest == v2 && config.Knob == 2;
        }, TimeSpan.FromSeconds(10));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50);
        }
        if (!predicate()) throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:0.0}s");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(50);
        }
        if (!await predicate()) throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:0.0}s");
    }
}
