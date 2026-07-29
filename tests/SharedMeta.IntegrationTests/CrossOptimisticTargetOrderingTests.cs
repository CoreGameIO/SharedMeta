using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// A CrossOptimistic call's target does not broadcast back to the originator — the effect is
/// already inlined in the outer call's replay payload. The session compensates by advancing its
/// own record of the target's entity sequence, and that bookkeeping must land in the same
/// (entityId, stateTypeName) bucket the target's subscription uses.
///
/// When it doesn't, the target's grain sequence runs away from the session's tracked value, one
/// step per cross-call, and the next DIRECT call to that entity reads as a sequence gap: the
/// response is deferred waiting for broadcasts that were never going to arrive, the RPC returns
/// empty, and the client's pending request never resolves.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class CrossOptimisticTargetOrderingTests
{
    private readonly TestClusterFixture _fixture;

    public CrossOptimisticTargetOrderingTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task DirectCallToCrossOptimisticTarget_ResolvesAfterRepeatedCrossCalls()
    {
        const string playerId = "player_crossopt_ordering";

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityA = $"counter_A_{Guid.NewGuid():N}";
        var entityB = $"counter_B_{Guid.NewGuid():N}";

        // Both subscribed by the same client: A runs the outer CrossOptimistic call, B is the
        // cross-entity target whose broadcast is suppressed for us.
        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        // Each one advances B's grain sequence without producing a broadcast we receive. Several
        // in a row so a mis-keyed reservation accumulates a gap wider than the one-step tolerance.
        for (int i = 0; i < 3; i++)
            await apiA.AddCrossEntityAsync(entityB, 5);

        // The regression: this used to hang forever on an empty, never-resolved response.
        await apiB.PingAsync();

        // And the direct call must actually take effect on B, on top of the cross-call sums.
        await apiB.AddValueAsync(7, 1);

        var stateB = resolver.GetState<CounterState>(entityB);
        Assert.Equal(3 * 5 + 7, stateB.Sum);

        Assert.Empty(client.DetectedIssues);
    }
}
