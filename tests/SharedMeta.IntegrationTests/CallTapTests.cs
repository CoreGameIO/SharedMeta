using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Diagnostics;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using SharedMeta.Test.Meta1.Server;
using Xunit;
using Ids = global::SharedMeta.Test.Meta1.Generated.GameMethodIds;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The generated call-tap wrappers: every kind of dispatch reaches the tap with its arguments,
/// result, call context and a state snapshot, and the generated method table decodes them.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class CallTapTests
{
    private static readonly GeneratedMetaMethodDescriptors Descriptors = GeneratedMetaMethodDescriptors.Instance;

    private readonly TestClusterFixture _fixture;

    public CallTapTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task ClientRpc_IsCapturedWithDecodedArgsResultAndState()
    {
        var prefix = $"tap_rpc_{Guid.NewGuid():N}";
        using var _ = RecordingCallTap.Instance.Arm(prefix);
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();

        var counter = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(prefix);
        await counter.AddValueAsync(5, 1);

        var rpc = Assert.Single(RecordingCallTap.Instance.Records, r => r.MethodId == Ids.ICounterService_Add_v0);
        Assert.Equal(MetaDispatchKind.Rpc, rpc.Kind);
        Assert.Equal("alice", rpc.CallerId);
        Assert.Equal(typeof(CounterState).FullName, rpc.StateType);
        Assert.True(rpc.Sequence > 0);
        Assert.False(string.IsNullOrEmpty(rpc.CallId));
        Assert.Null(rpc.Error);
        Assert.Equal(new object?[] { 5, 1 }, Descriptors.DecodeArgs(rpc.MethodId, rpc.Payload, _fixture.Serializer));
        Assert.Equal(5, _fixture.Serializer.Unpack<CounterState>(rpc.StateAfter)!.Sum);

        // The fresh entity ran [MetaInit] on the way in.
        var init = Assert.Single(RecordingCallTap.Instance.Records, r => r.Kind == MetaDispatchKind.Init);
        Assert.Equal(0, init.FromVersion);
        Assert.Equal(1, init.ToVersion);
    }

    [Fact(Timeout = 60_000)]
    public async Task Trigger_IsCapturedAfterItsMethod_InTheSameOperation()
    {
        var entityId = $"tap_trigger_{Guid.NewGuid():N}";
        using var _ = RecordingCallTap.Instance.Arm(entityId);
        var bumpId = Ids.IForcePatchFixtureService_Bump_v1;

        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<ForcePatchFixtureState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = bumpId,
            Payload = Descriptors.EncodeArgs(bumpId, new object?[] { 3 }, _fixture.Serializer),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        });
        Assert.False(result.HasError, result.Error);

        var records = RecordingCallTap.Instance.Records;
        Assert.Equal(2, records.Count);
        var (bump, trigger) = (records[0], records[1]);

        Assert.Equal(MetaDispatchKind.CrossEntity, bump.Kind);
        Assert.Equal(bumpId, bump.MethodId);
        Assert.Equal(3, Descriptors.DecodeResult(bumpId, bump.Result, _fixture.Serializer));

        Assert.Equal(MetaDispatchKind.Trigger, trigger.Kind);
        Assert.Equal(Ids.IForcePatchFixtureService_OnBumped_v1, trigger.MethodId);
        Assert.Equal(bump.CallId, trigger.CallId);
        Assert.Equal(bump.Sequence, trigger.Sequence);
        Assert.Equal(1, _fixture.Serializer.Unpack<ForcePatchFixtureState>(trigger.StateAfter)!.Bumps);
    }

    [Fact(Timeout = 60_000)]
    public async Task CrossEntityQuery_TargetHopNamesTheCallerSpanAsParent()
    {
        var prefix = $"tap_query_{Guid.NewGuid():N}";
        var entityA = prefix + "_a";
        var entityB = prefix + "_b";
        using var _ = RecordingCallTap.Instance.Arm(prefix);
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, $"caller-{Guid.NewGuid():N}");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var b = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);
        await b.AddValueAsync(5, 1);
        var a = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        Assert.Equal(5, await a.AddOtherSumViaQueryAsync(entityB));

        var records = RecordingCallTap.Instance.Records;
        var outer = Assert.Single(records, r => r.EntityId == entityA && r.Kind == MetaDispatchKind.Rpc);
        var query = Assert.Single(records, r => r.EntityId == entityB && r.Kind == MetaDispatchKind.Query);

        Assert.Equal(Ids.ICounterService_PeekSum_v0, query.MethodId);
        Assert.Equal(5L, Descriptors.DecodeResult(query.MethodId, query.Result, _fixture.Serializer));
        Assert.Equal(outer.CallId, query.CallId);
        Assert.NotEqual(0UL, outer.SpanId);
        Assert.Equal(outer.SpanId, query.ParentSpanId);
    }

    [Fact(Timeout = 60_000)]
    public async Task Signal_IsCapturedWithItsArguments()
    {
        var entityId = $"tap_signal_{Guid.NewGuid():N}";
        using var _ = RecordingCallTap.Instance.Arm(entityId);
        var signalId = Ids.ICounterService_NotifyHeartbeat_v0;

        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<CounterState>>(entityId);
        await grain.HandleSignalAsync(new RpcCall
        {
            MethodId = signalId,
            Payload = Descriptors.EncodeArgs(signalId, new object?[] { 123L }, _fixture.Serializer),
            CallerId = "bob",
        });

        RecordingCallTap.Record? signal = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (signal == null && DateTime.UtcNow < deadline)
        {
            signal = RecordingCallTap.Instance.Records.FirstOrDefault(r => r.Kind == MetaDispatchKind.Signal);
            if (signal == null) await Task.Delay(20);
        }

        Assert.NotNull(signal);
        Assert.Equal("bob", signal!.CallerId);
        Assert.Null(signal.Error);
        Assert.Equal(new object?[] { 123L }, Descriptors.DecodeArgs(signalId, signal.Payload, _fixture.Serializer));
    }

    public enum TapFailure { ShouldCapture, Begin, End }

    [Theory(Timeout = 60_000)]
    [InlineData(TapFailure.ShouldCapture)]
    [InlineData(TapFailure.Begin)]
    [InlineData(TapFailure.End)]
    public async Task FailingTap_DoesNotFailTheCall(TapFailure failure)
    {
        var prefix = $"tap_fail_{Guid.NewGuid():N}";
        using var _ = RecordingCallTap.Instance.Arm(prefix);
        RecordingCallTap.Instance.ThrowOnShouldCapture = failure == TapFailure.ShouldCapture;
        RecordingCallTap.Instance.ThrowOnBegin = failure == TapFailure.Begin;
        RecordingCallTap.Instance.ThrowOnEnd = failure == TapFailure.End;
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "carol");
        await client.ConnectAsync();

        var counter = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(prefix);
        await counter.AddValueAsync(4, 1);

        Assert.Equal(4, counter.State.Sum);
        Assert.Empty(RecordingCallTap.Instance.Records);
        Assert.Empty(client.DetectedIssues);
        // A failed or declined Begin must not be followed by End.
        if (failure != TapFailure.End) Assert.Equal(0, RecordingCallTap.Instance.EndCalls);
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclinedBegin_SkipsEnd()
    {
        var prefix = $"tap_decline_{Guid.NewGuid():N}";
        using var _ = RecordingCallTap.Instance.Arm(prefix);
        RecordingCallTap.Instance.DeclineInBegin = true;
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "dave");
        await client.ConnectAsync();

        var counter = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(prefix);
        await counter.AddValueAsync(2, 1);

        Assert.Equal(2, counter.State.Sum);
        Assert.Equal(0, RecordingCallTap.Instance.EndCalls);
    }

    [Fact(Timeout = 60_000)]
    public async Task ThrowingMethod_ReachesEndWithItsError()
    {
        var entityId = $"tap_throw_{Guid.NewGuid():N}";
        using var _ = RecordingCallTap.Instance.Arm(entityId);
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "erin");
        await client.ConnectAsync();

        var api = await client.CreateResolver().GetServiceAsync<FailureServiceApiClient>(entityId);
        await Assert.ThrowsAnyAsync<Exception>(() => api.AddThenThrowAsync(5));

        var failed = Assert.Single(RecordingCallTap.Instance.Records, r => r.MethodId == Ids.IFailureService_AddThenThrow_v0);
        Assert.Equal(MetaDispatchKind.Rpc, failed.Kind);
        Assert.Contains("AddThenThrow failed", failed.Error);
        Assert.Empty(failed.Result!);
    }

    /// <summary>
    /// Self-targeted cross-entity call. The generated accessor short-circuits it to the sibling
    /// impl with typed arguments — no dispatch, so the tap sees only the outer call; the inner
    /// call's effect is in the outer call's state.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SelfTargetedCall_IsVisibleThroughTheOuterCall()
    {
        var entityId = $"tap_self_{Guid.NewGuid():N}";
        using var _ = RecordingCallTap.Instance.Arm(entityId);
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "frank");
        await client.ConnectAsync();

        var api = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        Assert.Equal(42, await api.AddCrossEntityAsync(entityId, 42));
        // CrossOptimistic returns the local result; this server-mode call trails it on the session.
        await api.PingAsync();

        var records = RecordingCallTap.Instance.Records.Where(r => r.Kind != MetaDispatchKind.Init).ToList();
        var outer = Assert.Single(records, r => r.MethodId == Ids.ICounterService_AddCrossEntity_v0);
        Assert.Equal(MetaDispatchKind.Rpc, outer.Kind);
        Assert.Equal(42, _fixture.Serializer.Unpack<CounterState>(outer.StateAfter)!.Sum);
        Assert.DoesNotContain(records, r => r.Kind == MetaDispatchKind.Nested);
    }

    [Fact]
    public void MethodTable_DescribesParametersAndRoundTripsArguments()
    {
        var add = Descriptors.Get(Ids.ICounterService_Add_v0)!;
        Assert.Equal("ICounterService", add.ServiceName);
        Assert.Equal("Add", add.Alias);
        Assert.Equal("AddValue", add.MethodName);
        Assert.Equal(typeof(CounterState), add.StateType);
        Assert.Equal(new[] { "value", "clientSequence" }, add.Parameters.Select(p => p.Name));
        Assert.All(add.Parameters, p => Assert.Equal(typeof(int), p.WireType));
        Assert.Null(add.ResultType);

        Assert.Equal(typeof(long), Descriptors.Get(Ids.ICounterService_PeekSum_v0)!.ResultType);
        Assert.Equal(typeof(CounterState), Descriptors.GetStateType(nameof(CounterState)));
        Assert.Equal(typeof(CounterState), Descriptors.GetStateType(typeof(CounterState).FullName!));

        var payload = Descriptors.EncodeArgs(add.MethodId, new object?[] { 7, 2 }, _fixture.Serializer);
        Assert.Equal(new object?[] { 7, 2 }, Descriptors.DecodeArgs(add.MethodId, payload, _fixture.Serializer));
    }
}
