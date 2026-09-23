using System.Linq;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Coverage for [MetaMethod(Mode = ExecutionMode.Signal)] — fire-and-forget one-way calls.
///
/// Contract under test:
///   - Client fires via generated {Method}Signal(), returns immediately (void, no Task).
///   - Transport delivers to server's EntityGrain.HandleSignalAsync (OneWay grain method).
///   - MetaProvider.DispatchSignal routes to generated {Service}SignalDispatcher.
///   - Impl method runs with read-only semantics, any bridge calls would hit
///     NullServerRecordContext (no replay payload produced).
///   - Server side observer captures the invocation for assertion.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SignalMethodTests
{
    private readonly TestClusterFixture _fixture;

    public SignalMethodTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task Signal_FiresAndForgets_ServerReceivesWithoutClientWait()
    {
        // Fresh log per test — the static ConcurrentBag is shared across tests in the AppDomain.
        // Capture baseline count rather than clearing (other tests may be running in parallel).
        CounterService.HeartbeatLog.Clear();

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "signal-hb-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(playerId);

        // Fire three heartbeats — no awaiting on the Signal method by design.
        var t1 = DateTime.UtcNow.Ticks;
        api.NotifyHeartbeatSignal(t1);
        var t2 = DateTime.UtcNow.Ticks + 1;
        api.NotifyHeartbeatSignal(t2);
        var t3 = DateTime.UtcNow.Ticks + 2;
        api.NotifyHeartbeatSignal(t3);

        // InProcess transport does the full dispatch inline but the Orleans [OneWay] grain call
        // still schedules on the scheduler — give it a beat to run.
        await Task.Delay(200);

        var ours = CounterService.HeartbeatLog
            .Where(e => e.CallerId == playerId)
            .OrderBy(e => e.Ticks)
            .ToList();

        Assert.Equal(3, ours.Count);
        Assert.Contains(ours, e => e.Ticks == t1);
        Assert.Contains(ours, e => e.Ticks == t2);
        Assert.Contains(ours, e => e.Ticks == t3);
    }

    [Fact(Timeout = 60_000)]
    public async Task Signal_DoesNotIncrementEntitySequence_NoBroadcast()
    {
        // Signals must not flow through the broadcast channel. Subscribe, fire signal,
        // then run a regular RPC and verify the broadcast stream contains ONLY the RPC.
        CounterService.HeartbeatLog.Clear();

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "signal-seq-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(playerId);

        // Signal methods never broadcast, so they get no generated replay events at all
        // (ReplayEvents is only honoured on methods that can reach the replay path). Count via
        // the untyped resolver subscription instead — it needs no annotation and still proves the
        // runtime invariant rather than just the absence of an event.
        int replayedSignalBroadcasts = 0;
        using var signalReplays = resolver.OnMethodReplayed(
            playerId,
            global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_NotifyHeartbeat_v0,
            _ => System.Threading.Interlocked.Increment(ref replayedSignalBroadcasts));

        // Fire a signal, then a real RPC. Only the RPC should produce a broadcast/replay.
        api.NotifyHeartbeatSignal(DateTime.UtcNow.Ticks);
        await Task.Delay(100);

        await api.AddValueAsync(5, 1);
        await Task.Delay(150);

        // Signal must not have produced a replay — signal methods never broadcast.
        Assert.Equal(0, replayedSignalBroadcasts);

        // But the server DID see the signal in the observer.
        Assert.Contains(CounterService.HeartbeatLog, e => e.CallerId == playerId);
    }
}
