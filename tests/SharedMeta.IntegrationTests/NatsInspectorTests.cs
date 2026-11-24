using SharedMeta.Core;
using SharedMeta.Inspector;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Server;
using Xunit;
using Ids = global::SharedMeta.Test.Meta1.Generated.GameMethodIds;

namespace SharedMeta.IntegrationTests;

/// <summary>Inspector over NATS JetStream with two silos and standalone inspectors.</summary>
[Collection(NatsInspectorClusterCollection.Name)]
public class NatsInspectorTests
{
    private readonly NatsInspectorClusterFixture _fixture;

    public NatsInspectorTests(NatsInspectorClusterFixture fixture) => _fixture = fixture;

    private static string Key<TState>(string entityId) => MetaInspectorEvent.MakeEntityKey(typeof(TState).FullName!, entityId);

    [NatsFact(Timeout = 90_000)]
    public async Task CaptureSettings_ChangedAnywhere_ReachEverySilo()
    {
        var standalone = await _fixture.StartStandaloneAsync(TimeSpan.Zero);
        var entityId = $"nats_watch_{Guid.NewGuid():N}";

        await standalone.UpdateCaptureSettingsAsync(s => s.WatchedEntities[entityId] = InspectorLevel.Args);

        await Eventually(() => _fixture.SiloRuntimes.All(r => r.GetCaptureSettings().WatchedEntities.ContainsKey(entityId)));
        Assert.Equal(InspectorLevel.Args, _fixture.SiloRuntimes[1].GetCaptureSettings().WatchedEntities[entityId]);

        // And back, from a silo this time.
        await _fixture.SiloRuntimes[0].UpdateCaptureSettingsAsync(s => s.WatchedEntities.Remove(entityId));
        await Eventually(() => !standalone.GetCaptureSettings().WatchedEntities.ContainsKey(entityId)
                               && _fixture.SiloRuntimes.All(r => !r.GetCaptureSettings().WatchedEntities.ContainsKey(entityId)));
    }

    [NatsFact(Timeout = 120_000)]
    public async Task EveryInspector_SeesTheWholeClustersEvents_AndCrossSiloHopsJoinUp()
    {
        var standalone = await _fixture.StartStandaloneAsync(TimeSpan.FromMinutes(10));
        var run = Guid.NewGuid().ToString("N").Substring(0, 8);
        var entities = Enumerable.Range(0, 16).Select(i => $"nats_e_{run}_{i}").ToList();
        await WatchAsync(standalone, entities, InspectorLevel.ArgsResult);

        // Half activated through each silo's own factory, so both silos host some of them.
        for (int i = 0; i < entities.Count; i++)
            await CallAsync<CounterState>(entities[i], Ids.ICounterService_AddClamped_v0, new object?[] { 1 }, _fixture.SiloGrainFactories[i % 2]);

        // Every inspector — both silos and the standalone one — sees every entity's call.
        foreach (var runtime in _fixture.SiloRuntimes.Append(standalone))
            await Eventually(() => entities.All(id => SiloOf(runtime, id) != null));
        var siloOf = entities.ToDictionary(id => id, id => SiloOf(standalone, id)!);
        var silos = siloOf.Values.Distinct().ToList();
        Assert.Equal(2, silos.Count);

        // No method table in a standalone inspector: short state type names resolve from the events.
        var inspector = new InspectorService(standalone);
        Assert.Equal(Key<CounterState>(entities[0]), inspector.EntityKey(nameof(CounterState), entities[0]));

        // A hop between silos: call_sent recorded on one, the query it caused on the other.
        var a = entities.First(id => siloOf[id] == silos[0]);
        var b = entities.First(id => siloOf[id] == silos[1]);
        await CallAsync<CounterState>(a, Ids.ICounterService_AddOtherSumViaQuery_v0, b);
        foreach (var runtime in _fixture.SiloRuntimes.Append(standalone))
            await Eventually(() => HasQueryFrom(runtime, a, b));

        var keyA = Key<CounterState>(a);
        var keyB = Key<CounterState>(b);
        var sent = SentTo(standalone, a, b)!;
        var query = QueryFrom(standalone, a, b)!;
        Assert.NotEqual(sent.Silo, query.Silo);
        Assert.Equal(sent.SpanId, query.ParentSpanId);
        var edge = Assert.Single(inspector.Graph(keyA, 1, 100_000)["edges"]!.AsArray(),
            e => e!["from"]!.GetValue<string>() == keyA && e["to"]!.GetValue<string>() == keyB)!;
        Assert.Equal(1, edge["count"]!.GetValue<int>());
    }

    [NatsFact(Timeout = 120_000)]
    public async Task EntityHistory_IsReadBackFromTheStream_BeyondTheStore()
    {
        var first = await _fixture.StartStandaloneAsync(TimeSpan.Zero);
        var entityId = $"nats_hist_{Guid.NewGuid():N}";
        await WatchAsync(first, new[] { entityId }, InspectorLevel.Full);

        for (int i = 1; i <= 3; i++) await CallAsync<ForcePatchFixtureState>(entityId, Ids.IForcePatchFixtureService_Bump_v1, i);
        var key = Key<ForcePatchFixtureState>(entityId);
        await Eventually(() => first.Store.Query(new InspectorQuery { EntityKey = key }).Count(e => e.Method == "Bump") == 3);

        // A fresh inspector that loads nothing on start: everything comes from the stream's history.
        var fresh = await _fixture.StartStandaloneAsync(TimeSpan.Zero);
        var inspector = new InspectorService(fresh);
        // The Bumps were published before it started (only trailing trigger events may still arrive).
        Assert.DoesNotContain(fresh.Store.Query(new InspectorQuery { EntityKey = key }), e => e.Method == "Bump");

        var timeline = await inspector.TimelineAsync(key, null, null, 0, includePayloads: true);
        var bumps = timeline.Events.Where(e => e.Method == "Bump").ToList();
        Assert.Equal(new[] { 1, 2, 3 }, bumps.Select(e => e.Args!["amount"]!.GetValue<int>()));
        Assert.True(timeline.History!["olderThanStore"]!.GetValue<int>() >= 3);

        var lastSeq = bumps[^1].EntitySequence;
        var at = await inspector.StateAtAsync(key, lastSeq);
        Assert.True(at["isExact"]!.GetValue<bool>());
        Assert.Equal(3, at["state"]!["Bumps"]!.GetValue<int>());

        // Asked by the short type name an inspector without a method table cannot resolve: the
        // history finds the entity under any state type and answers with the full key.
        var byShortName = await inspector.TimelineAsync(nameof(ForcePatchFixtureState) + ":" + entityId, null, null, 0, includePayloads: false);
        Assert.Equal(key, byShortName.EntityKey);
        Assert.Equal(3, byShortName.Events.Count(e => e.Method == "Bump"));

        // Each history read opens a consumer of its own and removes it.
        var consumers = await _fixture.ConsumerCountAsync();
        for (int i = 0; i < 3; i++) await inspector.TimelineAsync(key, null, null, 0, includePayloads: false);
        Assert.Equal(consumers, await _fixture.ConsumerCountAsync());
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private async Task WatchAsync(InspectorRuntime via, IEnumerable<string> entityIds, InspectorLevel level)
    {
        var ids = entityIds.ToList();
        await via.UpdateCaptureSettingsAsync(s => { foreach (var id in ids) s.WatchedEntities[id] = level; });
        await Eventually(() => _fixture.SiloRuntimes.All(r => ids.All(id => r.GetCaptureSettings().WatchedEntities.ContainsKey(id))));
    }

    private Task CallAsync<TState>(string entityId, ushort methodId, params object?[] args) where TState : class, ISharedState, new()
        => CallAsync<TState>(entityId, methodId, args, _fixture.Cluster.GrainFactory);

    private async Task CallAsync<TState>(string entityId, ushort methodId, object?[] args, global::Orleans.IGrainFactory via) where TState : class, ISharedState, new()
    {
        var grain = via.GetGrain<IEntityGrain<TState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = methodId,
            Payload = GeneratedMetaMethodDescriptors.Instance.EncodeArgs(methodId, args, _fixture.Serializer),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
            CallerClientVersion = "1.0.0",
        });
        Assert.False(result.HasError, result.Error);
    }

    private static MetaInspectorEvent? SentTo(InspectorRuntime runtime, string a, string b)
        => runtime.Store.Query(new InspectorQuery { EntityKey = Key<CounterState>(a), Kind = InspectorEventKinds.CallSent })
            .FirstOrDefault(e => e.EntityKey == Key<CounterState>(a) && e.TargetEntity == Key<CounterState>(b));

    private static MetaInspectorEvent? QueryFrom(InspectorRuntime runtime, string a, string b)
        => runtime.Store.Query(new InspectorQuery { EntityKey = Key<CounterState>(b), CallKind = "query" })
            .FirstOrDefault(e => e.EntityKey == Key<CounterState>(b) && e.CallerEntity == Key<CounterState>(a));

    private static string? SiloOf(InspectorRuntime runtime, string entityId)
        => runtime.Store.Query(new InspectorQuery { EntityKey = Key<CounterState>(entityId), Kind = InspectorEventKinds.CallCompleted })
            .FirstOrDefault(e => e.Method == "AddClamped")?.Silo;

    private static bool HasQueryFrom(InspectorRuntime runtime, string a, string b)
        => SentTo(runtime, a, b) != null && QueryFrom(runtime, a, b) != null;

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Condition not met in time.");
            await Task.Delay(100);
        }
    }
}
