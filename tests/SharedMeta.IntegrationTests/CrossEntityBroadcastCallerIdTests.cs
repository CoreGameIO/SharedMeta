using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Regression tests for cross-entity broadcast CallerId attribution (0.34.0).
///
/// A Server-mode cross-entity call reaches the target grain with isClientOriginated=false.
/// The target broadcasts its mutation to subscribers WITHOUT excluding the originator (a
/// non-CrossOptimistic caller did not apply the effect locally — its client-side Replayer
/// no-op'd the inner call — so it needs the broadcast). The replay of that broadcast on every
/// subscriber must attribute the op to the REAL originator via Context.CallerId.
///
/// Before the fix, the server blanked CallerId on cross-entity broadcasts (to slip them past a
/// now-removed client-side CallerId==PlayerId echo filter), so the replayed method observed
/// Context.CallerId == null on every subscriber — including the originator itself, the exact
/// host-mode scenario where one MetaClient owns both the calling role and the target
/// subscription.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class CrossEntityBroadcastCallerIdTests
{
    private readonly TestClusterFixture _fixture;

    public CrossEntityBroadcastCallerIdTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The originator of a Server-mode cross-entity call, while also subscribed to the target
    /// entity, must see the target's broadcast replayed under its own player id — not a blanked
    /// CallerId. CounterService.AddValue records Context.CallerId into state.Operations, giving
    /// a directly observable attribution marker on the receiver's local copy.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerModeCrossEntity_Broadcast_ReplaysUnderRealCallerId_ForOriginatingSubscriber()
    {
        const string playerId = "player_origin";
        const int value = 17;

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityA = $"counter_A_{Guid.NewGuid():N}";
        var entityB = $"counter_B_{Guid.NewGuid():N}";

        // Subscribe to both: A is where the outer Server-mode call runs; B is the cross-entity
        // target AND a subscription of this same client, so its broadcast comes back to us.
        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        // A.SiblingThenCrossEntity → sibling Aux.AuxAddViaOther → real cross-grain RPC to
        // B.AddValue(value, -3). Runs on B with Context.CallerId = this player.
        await apiA.SiblingThenCrossEntityAsync(entityB, value);

        // Barrier: a Server-mode Ping to B drains every broadcast B enqueued to us before the
        // Ping response (per-connection FIFO), so the cross-entity broadcast is applied.
        await apiB.PingAsync();

        var stateB = resolver.GetState<CounterState>(entityB);

        // Sanity: the broadcast reached us and mutated our local copy of B.
        Assert.Equal(value, stateB.Sum);
        var op = Assert.Single(stateB.Operations);
        Assert.Equal(value, op.Value);
        Assert.Equal(-3, op.ClientSequence);

        // The point of the test: the replayed op is attributed to the real originator, not the
        // blanked-CallerId "unknown" fallback that CounterService.AddValue records when
        // Context.CallerId is null.
        Assert.Equal(playerId, op.CallerId);
        Assert.NotEqual("unknown", op.CallerId);

        Assert.Empty(client.DetectedIssues);
    }
}
