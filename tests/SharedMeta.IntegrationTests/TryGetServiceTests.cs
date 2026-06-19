using System;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Coverage for the synchronous, allocation-free <c>TryGetService&lt;T&gt;</c> accessor (0.29.3).
///
/// - Returns false before the entity/client has been resolved (no subscribe, no throw).
/// - Returns the same cached instance that GetServiceAsync returned, once resolved.
/// - The hot path (already resolved) allocates nothing.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class TryGetServiceTests
{
    private readonly TestClusterFixture _fixture;

    public TryGetServiceTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task TryGetService_BeforeResolve_ReturnsFalse()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "tryget-miss-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // Never resolved this entity → false, out is null, no subscribe happened, no throw.
        Assert.False(resolver.TryGetService<PartyServiceApiClient>(playerId, out var api));
        Assert.Null(api);
    }

    [Fact(Timeout = 60_000)]
    public async Task TryGetService_AfterResolve_ReturnsSameCachedInstance()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "tryget-hit-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var resolved = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        Assert.True(resolver.TryGetService<PartyServiceApiClient>(playerId, out var api));
        Assert.Same(resolved, api); // identical instance, not a new one
    }

    [Fact(Timeout = 60_000)]
    public async Task TryGetService_HotPath_IsAllocationFree()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "tryget-alloc-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        // Warm up: JIT the generic instantiation and the dictionary lookup path.
        for (int i = 0; i < 1_000; i++)
            resolver.TryGetService<PartyServiceApiClient>(playerId, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            resolver.TryGetService<PartyServiceApiClient>(playerId, out var api);
            // Touch the result so the call can't be optimized away.
            if (api == null) throw new InvalidOperationException("unexpected null on cached hot path");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
