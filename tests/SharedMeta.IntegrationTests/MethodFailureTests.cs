using Orleans;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
// The server API is compiled into the server project (the shared assembly fences it behind
// SHAREDMETA_SERVER, which shared projects do not define).
using SharedMeta.Test.Meta1.Server;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// A meta method that throws after mutating state is a defect, not a rejection path. What the
/// framework owes: every subscriber ends up on the server's state, the entity keeps working, and
/// the failing call writes nothing — the last persisted state stays restorable.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class MethodFailureTests
{
    private readonly TestClusterFixture _fixture;

    public MethodFailureTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(TestClientSetup Client, FailureServiceApiClient Api)> SubscribeAsync(
        InProcessServer server, string playerId, string entityId)
    {
        var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var api = await client.CreateResolver().GetServiceAsync<FailureServiceApiClient>(entityId);
        return (client, api);
    }

    /// <summary>
    /// The failed call took a sequence number. Unless something is delivered under it, the
    /// other subscriber's session holds every later broadcast waiting for it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerThrow_OtherSubscriberKeepsReceiving()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var entityId = $"failure_stall_{Guid.NewGuid():N}";
        var (alice, aliceApi) = await SubscribeAsync(server, "alice", entityId);
        var (bob, bobApi) = await SubscribeAsync(server, "bob", entityId);
        await using var _a = alice;
        await using var _b = bob;

        await Assert.ThrowsAnyAsync<Exception>(() => aliceApi.AddThenThrowAsync(5));
        await aliceApi.AddAsync(1);

        await WaitForAsync(() => bobApi.State.Value == 6);
        Assert.Equal(6, aliceApi.State.Value);
    }

    /// <summary>
    /// The caller learns about the failure from the exception, and its state already matches the
    /// server's: the partial mutation is a defect, but one everybody sees.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerThrow_CallerHoldsServerStateWhenTheExceptionArrives()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var entityId = $"failure_caller_{Guid.NewGuid():N}";
        var (alice, aliceApi) = await SubscribeAsync(server, "alice", entityId);
        await using var _a = alice;

        await Assert.ThrowsAnyAsync<Exception>(() => aliceApi.AddThenThrowAsync(5));

        Assert.Equal(5, aliceApi.State.Value);
    }

    /// <summary>
    /// An Optimistic caller already applied its own prediction. The server's state and random
    /// position replace it — a later optimistic roll must agree with the server's.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OptimisticServerOnlyThrow_CallerTakesServerStateAndRandom()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var entityId = $"failure_optimistic_{Guid.NewGuid():N}";
        var (alice, aliceApi) = await SubscribeAsync(server, "alice", entityId);
        await using var _a = alice;

        await aliceApi.AddServerDivergesAsync(3);
        await WaitForAsync(() => aliceApi.State.Value == 103);

        var predicted = await aliceApi.RollAsync();
        // Server-mode call behind the roll: its reply arrives after the roll's confirmation.
        await aliceApi.AddAsync(0);

        Assert.Equal(predicted, aliceApi.State.LastRoll);
        Assert.Empty(alice.DetectedIssues);
    }

    /// <summary>
    /// Server-originated calls take the cross-entity entry; its failure must resync subscribers
    /// the same way.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerApiThrow_SubscriberKeepsReceiving()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var entityId = $"failure_srvapi_{Guid.NewGuid():N}";
        var (alice, aliceApi) = await SubscribeAsync(server, "alice", entityId);
        await using var _a = alice;
        var serverApi = _fixture.GrainFactory.GetServerApi<IFailureService>(entityId);

        await Assert.ThrowsAnyAsync<Exception>(() => serverApi.AddThenThrowAsync(5));
        await serverApi.AddAsync(1);

        await WaitForAsync(() => aliceApi.State.Value == 6);
    }

    /// <summary>
    /// Under the default every-call policy the store holds the state before the failed call,
    /// until the next successful call writes as usual.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerThrow_DoesNotWriteState()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var entityId = $"failure_persist_{Guid.NewGuid():N}";
        var (alice, aliceApi) = await SubscribeAsync(server, "alice", entityId);
        await using var _a = alice;
        var grainId = _fixture.GrainFactory.GetGrain<IEntityGrain<FailureState>>(entityId).GetGrainId();

        await aliceApi.AddAsync(1);
        var writesBefore = CountingGrainStorage.WriteCount(grainId, "entity");

        await Assert.ThrowsAnyAsync<Exception>(() => aliceApi.AddThenThrowAsync(5));
        Assert.Equal(writesBefore, CountingGrainStorage.WriteCount(grainId, "entity"));

        await aliceApi.AddAsync(1);
        Assert.Equal(writesBefore + 1, CountingGrainStorage.WriteCount(grainId, "entity"));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "Condition was not met before the timeout.");
    }
}
