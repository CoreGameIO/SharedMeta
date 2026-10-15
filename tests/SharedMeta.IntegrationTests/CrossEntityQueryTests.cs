using SharedMeta.Client;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// A cross-entity call to a <c>Mode = Query</c> method is a read: the target must not advance its
/// entity sequence or broadcast to its subscribers, while the caller's subscribers still replay
/// the recorded result. Before, every cross-entity call went through the full call path, so a
/// read cost the target a broadcast to everyone subscribed to it.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class CrossEntityQueryTests
{
    private readonly TestClusterFixture _fixture;

    public CrossEntityQueryTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task CrossEntityQuery_TargetNeitherBroadcastsNorAdvancesSequence()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var caller = new TestClientSetup(server, $"caller-{Guid.NewGuid():N}");
        await using var watcher = new TestClientSetup(server, $"watcher-{Guid.NewGuid():N}");
        await caller.ConnectAsync();
        await watcher.ConnectAsync();

        var entityA = $"query_A_{Guid.NewGuid():N}";
        var entityB = $"query_B_{Guid.NewGuid():N}";

        var watcherResolver = watcher.CreateResolver();
        var callerA = await caller.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityA);
        var watcherA = await watcherResolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var watcherB = await watcherResolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        await watcherB.AddValueAsync(5, 1);

        var watcherDispatcher = (ClientDispatcher)watcher.MetaClient.Dispatcher;
        var bStateType = typeof(CounterState).FullName!;
        var bSeqBefore = watcherDispatcher.GetLastKnownEntitySequence(entityB, bStateType);

        int peekReplays = 0;
        using var peekSub = watcherResolver.OnMethodReplayed(
            entityB,
            global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_PeekSum_v0,
            _ => Interlocked.Increment(ref peekReplays));

        var read = await callerA.AddOtherSumViaQueryAsync(entityB);
        Assert.Equal(5, read);

        // Barriers: each Ping response trails every broadcast its entity queued to the watcher.
        await watcherB.PingAsync();
        await watcherA.PingAsync();

        // The query left no trace on B: no replayed op, and only the Ping moved its sequence.
        Assert.Equal(0, peekReplays);
        Assert.Equal(bSeqBefore + 1, watcherDispatcher.GetLastKnownEntitySequence(entityB, bStateType));
        Assert.Equal(5, watcherB.State.Sum);

        // A's subscribers replayed the outer call with the result recorded from B.
        Assert.Equal(5, callerA.State.Sum);
        Assert.Equal(5, watcherA.State.Sum);

        Assert.Empty(caller.DetectedIssues);
        Assert.Empty(watcher.DetectedIssues);
    }
}
