using System.Text.Json;
using System.Text.Json.Nodes;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.Inspector;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using SharedMeta.Test.Meta1.Server;
using Xunit;
using Ids = global::SharedMeta.Test.Meta1.Generated.GameMethodIds;

namespace SharedMeta.IntegrationTests;

/// <summary>Capture, encoding and TimeMachine queries of the inspector on a live cluster.</summary>
[Collection(InspectorClusterCollection.Name)]
public class InspectorTests
{
    private readonly InspectorClusterFixture _fixture;

    public InspectorTests(InspectorClusterFixture fixture) => _fixture = fixture;

    private InspectorService Inspector => _fixture.Service;
    private Task Flush() => _fixture.Runtime.FlushAsync(TimeSpan.FromSeconds(10));

    private static string Key<TState>(string entityId) => MetaInspectorEvent.MakeEntityKey(typeof(TState).FullName!, entityId);

    [Fact(Timeout = 60_000)]
    public async Task NothingIsCaptured_UntilSomethingIsWatched()
    {
        await _fixture.ResetAsync();
        var entityId = $"insp_off_{Guid.NewGuid():N}";
        await BumpAsync(entityId, 1);
        await Flush();

        Assert.Empty(Inspector.Timeline(Key<ForcePatchFixtureState>(entityId), null, null, 0, false));
    }

    [Fact(Timeout = 60_000)]
    public async Task WatchedEntity_TimelineHasTheCallItsTriggerAndTheState()
    {
        var entityId = $"insp_trig_{Guid.NewGuid():N}";
        await _fixture.ResetAsync(s => s.WatchedEntities[entityId] = InspectorLevel.Full);

        await BumpAsync(entityId, 3);
        await Flush();

        var key = Key<ForcePatchFixtureState>(entityId);
        var timeline = Inspector.Timeline(key, null, null, 0, includePayloads: true);
        var bump = Assert.Single(timeline, e => e.Method == "Bump");
        var trigger = Assert.Single(timeline, e => e.CallKind == "trigger");

        Assert.Equal("cross_entity", bump.CallKind);
        Assert.Equal("IForcePatchFixtureService", bump.Service);
        Assert.Equal(3, bump.Args!["amount"]!.GetValue<int>());
        Assert.Equal(3, bump.ResultValue!.GetValue<int>());
        Assert.Equal("OnBumped", trigger.Method);
        Assert.Equal(bump.EntitySequence, trigger.EntitySequence);
        Assert.True(trigger.SubOrder > bump.SubOrder);
        Assert.Equal(bump.SpanId, trigger.ParentSpanId);
        Assert.Equal(bump.CallId, trigger.CallId);
        Assert.Equal(1, trigger.State!["Bumps"]!.GetValue<int>());

        var at = Inspector.StateAt(key, bump.EntitySequence);
        Assert.True(at["isExact"]!.GetValue<bool>());
        Assert.Equal(1, at["state"]!["Bumps"]!.GetValue<int>());

        var tree = Inspector.CallTree(bump.CallId!, includePayloads: false);
        var root = Assert.Single(tree["roots"]!.AsArray());
        Assert.Single(root!["children"]!.AsArray());
    }

    [Fact(Timeout = 60_000)]
    public async Task ClientCall_IsFollowedByItsBroadcastAndTheReply()
    {
        var entityId = $"insp_rpc_{Guid.NewGuid():N}";
        await _fixture.ResetAsync(s => s.WatchedEntities[entityId] = InspectorLevel.ArgsResult);
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var bobApi = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        await aliceApi.AddValueAsync(5, 1);
        await bobApi.PingAsync(); // trails the broadcast to bob
        await Flush();

        var key = Key<CounterState>(entityId);
        var timeline = Inspector.Timeline(key, null, null, 0, includePayloads: true);
        var add = Assert.Single(timeline, e => e.Method == "Add" && e.Kind == InspectorEventKinds.CallCompleted);
        Assert.Equal("rpc", add.CallKind);
        Assert.Equal(alice.PlayerId, add.CallerId);
        Assert.Equal(5, add.Args!["value"]!.GetValue<int>());
        Assert.Null(add.State); // ArgsResult: no state

        var broadcast = Assert.Single(timeline, e => e.Kind == InspectorEventKinds.BroadcastSent && e.EntitySequence == add.EntitySequence);
        Assert.Contains(bob.PlayerId, broadcast.TargetPlayers!);

        var reply = Assert.Single(timeline, e => e.Kind == InspectorEventKinds.ClientResponded && e.CallId == add.CallId);
        Assert.Equal("success", reply.Result);
        Assert.Equal(alice.PlayerId, reply.CallerId);

        // Entity order: the call, then its broadcast, then the reply — all in the call's operation.
        Assert.Equal(add.EntitySequence, reply.EntitySequence);
        Assert.True(timeline.IndexOf(add) < timeline.IndexOf(broadcast));
        Assert.True(timeline.IndexOf(broadcast) < timeline.IndexOf(reply));
        var fromAdd = Inspector.Timeline(key, add.EntitySequence, add.EntitySequence, 0, false);
        Assert.Contains(fromAdd, e => e.Kind == InspectorEventKinds.ClientResponded && e.CallId == add.CallId);

        // A folded broadcast is found by any of its recipients; filters combine.
        Assert.Contains(Inspector.Recent(new InspectorQuery { PlayerId = bob.PlayerId }, false),
            e => e.Kind == InspectorEventKinds.BroadcastSent && e.EntityKey == key);
        Assert.DoesNotContain(Inspector.Recent(new InspectorQuery { EntityKey = key, PlayerId = alice.PlayerId }, false),
            e => e.CallerId == bob.PlayerId && e.Kind == InspectorEventKinds.CallCompleted);
        Assert.All(Inspector.Recent(new InspectorQuery { StateType = typeof(CounterState).FullName }, false),
            e => Assert.True(e.StateType == typeof(CounterState).FullName || e.TargetEntity!.StartsWith(typeof(CounterState).FullName + ":")));

        var mermaid = Inspector.TimelineMermaid(key, timeline);
        Assert.Contains("sequenceDiagram", mermaid);
        Assert.DoesNotContain(mermaid.Split('\n'), line => line.Contains("->>") && line.Split(':')[0].Contains('"'));
    }

    [Fact(Timeout = 60_000)]
    public async Task WatchedSender_AlsoCapturesWhatTheTargetDidForIt()
    {
        var a = $"insp_sa_{Guid.NewGuid():N}";
        var b = $"insp_sb_{Guid.NewGuid():N}";
        await _fixture.ResetAsync(s => s.WatchedEntities[a] = InspectorLevel.ArgsResult);
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, $"dave-{Guid.NewGuid():N}");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();
        await (await resolver.GetServiceAsync<CounterServiceApiClient>(b)).AddValueAsync(4, 1);
        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(a);
        Assert.Equal(4, await apiA.AddOtherSumViaQueryAsync(b));
        await Flush();

        var keyA = Key<CounterState>(a);
        var keyB = Key<CounterState>(b);
        var timelineB = Inspector.Timeline(keyB, null, null, 0, false);
        var query = Assert.Single(timelineB, e => e.CallKind == "query");
        Assert.Equal(keyA, query.CallerEntity);
        Assert.DoesNotContain(timelineB, e => e.CallKind == "rpc"); // B's own client call is not A's business

        // Captured at both ends, still one hop in the graph.
        var graph = Inspector.Graph(keyA, depth: 1, window: 5000);
        var edge = Assert.Single(graph["edges"]!.AsArray(), e => e!["from"]!.GetValue<string>() == keyA && e["to"]!.GetValue<string>() == keyB)!;
        Assert.Equal(1, edge["count"]!.GetValue<int>());
    }

    [Fact(Timeout = 60_000)]
    public async Task CrossEntityCall_IsSeenFromBothEnds_AndFormTheGraph()
    {
        var a = $"insp_a_{Guid.NewGuid():N}";
        var b = $"insp_b_{Guid.NewGuid():N}";
        // Only the target is watched: it must still learn which entity called it.
        await _fixture.ResetAsync(s => s.WatchedEntities[b] = InspectorLevel.ArgsResult);
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, $"carol-{Guid.NewGuid():N}");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(b);
        await apiB.AddValueAsync(7, 1);
        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(a);
        Assert.Equal(7, await apiA.AddOtherSumViaQueryAsync(b));
        await Flush();

        var keyA = Key<CounterState>(a);
        var keyB = Key<CounterState>(b);
        var query = Assert.Single(Inspector.Timeline(keyB, null, null, 0, true), e => e.CallKind == "query");
        Assert.Equal(keyA, query.CallerEntity);
        Assert.Equal(7L, query.ResultValue!.GetValue<long>());

        // The sender's side is recorded too (the target is watched), on the sender's entity.
        var sent = Assert.Single(Inspector.Timeline(keyA, null, null, 0, false), e => e.Kind == InspectorEventKinds.CallSent);
        Assert.Equal(keyB, sent.TargetEntity);
        Assert.Equal(sent.SpanId, query.ParentSpanId);

        var graph = Inspector.Graph(keyB, depth: 2, window: 5000);
        var edges = graph["edges"]!.AsArray().Select(e => (e!["from"]!.GetValue<string>(), e["to"]!.GetValue<string>(), e["count"]!.GetValue<int>())).ToList();
        Assert.Contains((keyA, keyB, 1), edges);
        Assert.Contains("flowchart", graph["mermaid"]!.GetValue<string>());
    }

    [Fact(Timeout = 60_000)]
    public async Task Invoke_RunsAllowListedMethods_AndRoutesQueries()
    {
        await _fixture.ResetAsync();
        var entityId = $"insp_inv_{Guid.NewGuid():N}";
        var stateType = nameof(CounterState);

        var added = await Inspector.InvokeAsync(stateType, entityId, "ICounterService", "AddClamped",
            JsonDocument.Parse("{\"value\": 12}").RootElement, null, null, "1.0.0");
        Assert.True(added["success"]!.GetValue<bool>(), added["error"]?.ToString());
        Assert.Equal(12, added["result"]!.GetValue<int>());

        var peek = await Inspector.InvokeAsync(stateType, entityId, "ICounterService", "PeekSum", null, null, null, "1.0.0");
        Assert.Equal("Query", peek["mode"]!.GetValue<string>());
        Assert.Equal(12L, peek["result"]!.GetValue<long>());

        var live = await Inspector.LiveStateAsync(stateType, entityId, "Sum", activate: true);
        Assert.Equal(12L, live["state"]!.GetValue<long>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Inspector.InvokeAsync(
            nameof(FailureState), entityId, "IFailureService", "Add", JsonDocument.Parse("[1]").RootElement, null, null, null));

        // The state type named by the caller must be the method's.
        var wrongState = await Assert.ThrowsAsync<InvalidOperationException>(() => Inspector.InvokeAsync(
            nameof(FailureState), entityId, "ICounterService", "AddClamped", JsonDocument.Parse("{\"value\": 1}").RootElement, null, null, "1.0.0"));
        Assert.Contains(typeof(CounterState).FullName!, wrongState.Message);
    }

    private async Task BumpAsync(string entityId, int amount)
    {
        var bumpId = Ids.IForcePatchFixtureService_Bump_v1;
        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<ForcePatchFixtureState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = bumpId,
            Payload = GeneratedMetaMethodDescriptors.Instance.EncodeArgs(bumpId, new object?[] { amount }, _fixture.Serializer),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        });
        Assert.False(result.HasError, result.Error);
    }
}
