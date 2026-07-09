using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Session;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Server-side regression coverage for <c>SessionManagerGrain</c>'s entityId/StateType identity
/// fix. Before this fix, <c>SubscribeToEntityAsync</c>'s "already subscribed" fast path matched
/// on entityId alone — subscribing a second state type under an entityId already subscribed to a
/// different state type silently reused the FIRST grain reference and returned its snapshot,
/// with no error. <c>SendToEntityAsync</c> had the same entityId-only lookup for RPC routing.
/// These tests drive <see cref="ISessionManager"/> directly (no transport), mirroring
/// <see cref="SessionRecoveryFlowTests"/> / <see cref="SessionOrderingTests"/>.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SessionManagerDualStateTypeTests
{
    private readonly TestClusterFixture _fixture;

    public SessionManagerDualStateTypeTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Guid sessionId, ISessionManager grain, string entityId)> SetupSessionAsync(string namePrefix)
    {
        var playerId = $"{namePrefix}-{Guid.NewGuid():N}";
        var grain = _fixture.GrainFactory.GetGrain<ISessionManager>(playerId);

        var sessionId = Guid.NewGuid();
        var connect = await grain.ConnectAsync(sessionId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(connect.Success);

        var entityId = $"dual_{Guid.NewGuid():N}";
        return (sessionId, grain, entityId);
    }

    private RpcCall BuildCounterAddCall(int value, int clientSequence)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, value);
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, clientSequence);
        return new RpcCall
        {
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Add_v0,
            Payload = buffer.WrittenSpan.ToArray(),
            CallerId = "test",
            CallerClientVersion = "1.0.0",
        };
    }

    private RpcCall BuildDesyncAddCall(int amount)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, amount);
        return new RpcCall
        {
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IDesyncTestService_Add_v0,
            Payload = buffer.WrittenSpan.ToArray(),
            CallerId = "test",
            CallerClientVersion = "1.0.0",
        };
    }

    private async Task<CounterState> ReadCounterStateAsync(string entityId)
    {
        var resolver = new SharedMeta.Test.Meta1.Server.GeneratedEntityGrainResolver();
        var entityGrain = resolver.GetEntityGrain(_fixture.GrainFactory, typeof(CounterState).FullName!, entityId);
        var stateBytes = await entityGrain!.GetEntityStateAsync();
        return _fixture.Serializer.Unpack<CounterState>(stateBytes!)!;
    }

    private async Task<DesyncTestState> ReadDesyncStateAsync(string entityId)
    {
        var resolver = new SharedMeta.Test.Meta1.Server.GeneratedEntityGrainResolver();
        var entityGrain = resolver.GetEntityGrain(_fixture.GrainFactory, typeof(DesyncTestState).FullName!, entityId);
        var stateBytes = await entityGrain!.GetEntityStateAsync();
        return _fixture.Serializer.Unpack<DesyncTestState>(stateBytes!)!;
    }

    [Fact(Timeout = 30_000)]
    public async Task SubscribeToEntityAsync_SecondStateTypeSharingEntityId_GetsItsOwnFreshSnapshot()
    {
        var (_, grain, entityId) = await SetupSessionAsync("dual-subscribe");

        var counterSub = await grain.SubscribeToEntityAsync(entityId, typeof(CounterState).FullName!, clientVersion: "1.0.0");
        Assert.True(counterSub.Success, counterSub.Error);

        // Second subscribe, same entityId, DIFFERENT state type. Before the fix, this hit the
        // "already subscribed" fast path keyed by entityId alone and silently returned
        // CounterState's grain/snapshot mislabeled as DesyncTestState.
        var desyncSub = await grain.SubscribeToEntityAsync(entityId, typeof(DesyncTestState).FullName!, clientVersion: "1.0.0");
        Assert.True(desyncSub.Success, desyncSub.Error);

        // A genuinely fresh DesyncTestState snapshot — not corrupted by / aliased to CounterState.
        var desyncState = _fixture.Serializer.Unpack<DesyncTestState>(desyncSub.StateBytes!)!;
        Assert.Equal(0, desyncState.Value);
        Assert.Equal("", desyncState.Label);
    }

    [Fact(Timeout = 30_000)]
    public async Task SendToEntityAsync_RoutesEachStateTypeToItsOwnGrain_WhenSharingEntityId()
    {
        var (sessionId, grain, entityId) = await SetupSessionAsync("dual-rpc-routing");

        await grain.SubscribeToEntityAsync(entityId, typeof(CounterState).FullName!, clientVersion: "1.0.0");
        await grain.SubscribeToEntityAsync(entityId, typeof(DesyncTestState).FullName!, clientVersion: "1.0.0");

        // RPC to CounterState's connection.
        var counterResp = await grain.SendToEntityAsync(
            entityId, typeof(CounterState).FullName!, requestId: 1, BuildCounterAddCall(10, 1), 0, sessionId);
        Assert.False(counterResp.HasError, counterResp.Error);

        // RPC to DesyncTestState's connection — same entityId, different stateTypeName.
        var desyncResp = await grain.SendToEntityAsync(
            entityId, typeof(DesyncTestState).FullName!, requestId: 2, BuildDesyncAddCall(99), 0, sessionId);
        Assert.False(desyncResp.HasError, desyncResp.Error);

        // Each grain must have received exactly its own call — no cross-contamination.
        var counterState = await ReadCounterStateAsync(entityId);
        var desyncState = await ReadDesyncStateAsync(entityId);
        Assert.Equal(10, counterState.Sum);
        Assert.Equal(99, desyncState.Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnsubscribeFromEntityAsync_RemovesOnlyTheMatchingStateType()
    {
        var (sessionId, grain, entityId) = await SetupSessionAsync("dual-unsubscribe");

        await grain.SubscribeToEntityAsync(entityId, typeof(CounterState).FullName!, clientVersion: "1.0.0");
        await grain.SubscribeToEntityAsync(entityId, typeof(DesyncTestState).FullName!, clientVersion: "1.0.0");

        await grain.UnsubscribeFromEntityAsync(entityId, typeof(CounterState).FullName!);

        // CounterState's connection is gone — RPC against it fails "not subscribed".
        var counterResp = await grain.SendToEntityAsync(
            entityId, typeof(CounterState).FullName!, requestId: 1, BuildCounterAddCall(10, 1), 0, sessionId);
        Assert.True(counterResp.HasError);

        // DesyncTestState's sibling connection survives untouched.
        var desyncResp = await grain.SendToEntityAsync(
            entityId, typeof(DesyncTestState).FullName!, requestId: 2, BuildDesyncAddCall(5), 0, sessionId);
        Assert.False(desyncResp.HasError, desyncResp.Error);
    }

    [Fact(Timeout = 30_000)]
    public async Task Resume_TwoStateTypesSharingEntityId_CorrelatesVerdictsToCorrectClaim()
    {
        var (sessionId, grain, entityId) = await SetupSessionAsync("dual-resume");

        var counterSub = await grain.SubscribeToEntityAsync(entityId, typeof(CounterState).FullName!, clientVersion: "1.0.0");
        var desyncSub = await grain.SubscribeToEntityAsync(entityId, typeof(DesyncTestState).FullName!, clientVersion: "1.0.0");
        Assert.True(counterSub.Success);
        Assert.True(desyncSub.Success);

        // Simulate a transport drop (server-side subscription bookkeeping is cleared, exactly
        // as OnTransportDisconnectedAsync does) and Resume with claims for BOTH state types
        // sharing this entityId — the pre-fix SubscriptionResult had no StateTypeName, so the
        // client couldn't tell which verdict belonged to which connection.
        await grain.OnTransportDisconnectedAsync();

        var claims = new List<SubscriptionClaim>
        {
            new() { EntityId = entityId, StateTypeName = typeof(CounterState).FullName!, LastKnownEntitySequence = counterSub.EntitySequenceNumber },
            new() { EntityId = entityId, StateTypeName = typeof(DesyncTestState).FullName!, LastKnownEntitySequence = desyncSub.EntitySequenceNumber },
        };

        var resume = await grain.ConnectAsync(sessionId, 0, SessionConnectMode.Resume, 0, claims, "1.0.0", 0UL);
        Assert.True(resume.Success, resume.Error);
        Assert.NotNull(resume.Subscriptions);
        Assert.Equal(2, resume.Subscriptions!.Count);

        var counterVerdict = resume.Subscriptions!.Single(v => v.StateTypeName == typeof(CounterState).FullName!);
        var desyncVerdict = resume.Subscriptions!.Single(v => v.StateTypeName == typeof(DesyncTestState).FullName!);

        Assert.Equal(entityId, counterVerdict.EntityId);
        Assert.Equal(entityId, desyncVerdict.EntityId);
        Assert.NotEqual(SubscriptionStatus.Failed, counterVerdict.Status);
        Assert.NotEqual(SubscriptionStatus.Failed, desyncVerdict.Status);
    }
}
