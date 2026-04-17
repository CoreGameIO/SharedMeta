using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for [NamedRandom] — validates that named random streams
/// are per-entity, persisted across calls, isolated from each other, and survive
/// client reconnect.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class NamedRandomTests
{
    private readonly TestClusterFixture _fixture;

    public NamedRandomTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task NamedStream_AdvancesIndependentlyPerCall()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"named_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Three draws from stream 0 (Combat) — should be deterministic but different values.
        var draw0a = await api.DrawFromNamedAsync(0, 10_000);
        var draw0b = await api.DrawFromNamedAsync(0, 10_000);
        var draw0c = await api.DrawFromNamedAsync(0, 10_000);

        // Two draws from stream 1 (Loot) — independent stream
        var draw1a = await api.DrawFromNamedAsync(1, 10_000);
        var draw1b = await api.DrawFromNamedAsync(1, 10_000);

        // Each stream should produce varied (non-repeating) values
        Assert.True(draw0a != draw0b || draw0b != draw0c,
            "Combat stream should advance across calls");
        Assert.NotEqual(draw1a, draw1b);

        // Streams must not share state — independent seeds and independent advancement
        Assert.NotEqual(draw0a, draw1a);

        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task NamedStream_PersistsAcrossClientReconnect()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var entityId = $"named_{Guid.NewGuid():N}";

        // First client: draw two values from Combat
        int first0, first1;
        await using (var client1 = new TestClientSetup(server, "player_a"))
        {
            await client1.ConnectAsync();
            var resolver1 = client1.CreateResolver();
            var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);
            first0 = await api1.DrawFromNamedAsync(0, 1_000_000_000);
            first1 = await api1.DrawFromNamedAsync(0, 1_000_000_000);
            // Sync barrier: DrawFromNamed is Optimistic (fire-and-forget to server).
            // AddValue is Server-mode → awaiting it blocks until the grain has FIFO-processed
            // both prior DrawFromNamed RPCs, so the next client's snapshot reflects the advanced state.
            await api1.AddValueAsync(0, 0);
            Assert.Empty(client1.DetectedIssues);
        }

        // Second client: fresh subscribe; server must have persisted the advanced stream state,
        // so the next draw continues from where client1 left off (not re-seeded from entityId).
        await using var client2 = new TestClientSetup(server, "player_b");
        await client2.ConnectAsync();
        var resolver2 = client2.CreateResolver();
        var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);
        var third = await api2.DrawFromNamedAsync(0, 1_000_000_000);

        // The three draws from a pseudorandom stream of 1B range must not collide.
        Assert.NotEqual(first0, first1);
        Assert.NotEqual(first0, third);
        Assert.NotEqual(first1, third);

        Assert.Empty(client2.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task NamedStream_BroadcastAdvancesSubscribers()
    {
        // Client1 makes a call; broadcast reaches Client2; Client2's local named random must
        // advance by the server-reported delta so its subsequent draw matches the server's.
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client1 = new TestClientSetup(server, "p1");
        await using var client2 = new TestClientSetup(server, "p2");
        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var entityId = $"named_bcast_{Guid.NewGuid():N}";
        var resolver1 = client1.CreateResolver();
        var resolver2 = client2.CreateResolver();
        var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Client1 draws once — broadcast fires, Client2 sees NamedRandomScrollDeltas=[1,0] and skips.
        var client1Draw = await api1.DrawFromNamedAsync(0, 1_000_000_000);
        // Sync barrier: DrawFromNamed is Optimistic (fire-and-forget). AddValue is Server-mode,
        // so its await guarantees the grain has processed the prior DrawFromNamed AND broadcast
        // has been dispatched to Client2 before we continue.
        await api1.AddValueAsync(0, 0);

        // Now Client2 draws — must continue from advanced stream, not from stale pre-broadcast state.
        var client2Draw = await api2.DrawFromNamedAsync(0, 1_000_000_000);

        Assert.NotEqual(client1Draw, client2Draw);

        // No desyncs recorded (server-reported delta == local delta for both clients).
        Assert.Empty(client1.DetectedIssues);
        Assert.Empty(client2.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task NamedStream_DifferentEntities_HaveIndependentSeeds()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();

        // Two different entities — default seed derives from entityId, so streams differ.
        var entityA = $"named_a_{Guid.NewGuid():N}";
        var entityB = $"named_b_{Guid.NewGuid():N}";

        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        var drawA = await apiA.DrawFromNamedAsync(0, 1_000_000);
        var drawB = await apiB.DrawFromNamedAsync(0, 1_000_000);

        // Extremely unlikely to collide with a uniform range of 1M.
        Assert.NotEqual(drawA, drawB);

        Assert.Empty(client.DetectedIssues);
    }
}
