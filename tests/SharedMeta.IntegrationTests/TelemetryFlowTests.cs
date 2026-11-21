using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Session;
using SharedMeta.Server.Core.Telemetry;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Telemetry on the paths that are easy to get wrong: a stashed out-of-order call must log under
/// its own call id (not the predecessor's that drains it), and every session must produce exactly
/// one end event with the reason that actually ended it.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class TelemetryFlowTests
{
    private readonly TestClusterFixture _fixture;

    public TelemetryFlowTests(TestClusterFixture fixture) => _fixture = fixture;

    // ── Stash ────────────────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task StashedCall_RunsUnderItsOwnCallId_AndIsCounted()
    {
        long stashed = 0, drained = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == SharedMetaMeters.MeterName && i.Name.StartsWith("sharedmeta.rpc.out_of_order."))
                l.EnableMeasurementEvents(i);
        };
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => Interlocked.Add(ref stashed, v));
        listener.SetMeasurementEventCallback<double>((i, _, _, _) => Interlocked.Increment(ref drained));
        listener.Start();

        var playerId = $"tel-stash-{Guid.NewGuid():N}";
        var grain = _fixture.GrainFactory.GetGrain<ISessionManager>(playerId);
        var sessionId = Guid.NewGuid();
        Assert.True((await grain.ConnectAsync(sessionId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL)).Success);
        var entityId = $"counter_{Guid.NewGuid():N}";
        Assert.True((await grain.SubscribeToEntityAsync(entityId, typeof(CounterState).FullName!, clientVersion: "1.0.0")).Success);

        var traceOf2 = MetaTraceContext.CreateRandom();
        var traceOf1 = MetaTraceContext.CreateRandom();

        using (TelemetryLogCapture.Instance.Arm())
        {
            // req 2 arrives first and is parked; req 1 closes the gap and drains it in its own turn.
            Assert.Empty((await grain.SendToEntityAsync(entityId, typeof(CounterState).FullName!, 2, AddCall(2, traceOf2), 0, sessionId)).Operations);
            await grain.SendToEntityAsync(entityId, typeof(CounterState).FullName!, 1, AddCall(1, traceOf1), 0, sessionId);

            var byRequest = TelemetryLogCapture.Instance.Entries
                .Where(e => e.Category.Contains("SessionManagerGrain") && e.Scope.ContainsKey(MetaLogFields.RequestId))
                .GroupBy(e => (long)e.Scope[MetaLogFields.RequestId]!)
                .ToDictionary(g => g.Key, g => g.Select(e => (string?)e.Scope[MetaLogFields.CallId]).Distinct().ToList());

            Assert.Equal(new[] { traceOf1.TraceIdHex }, byRequest[1]);
            Assert.Equal(new[] { traceOf2.TraceIdHex }, byRequest[2]);
        }

        listener.RecordObservableInstruments();
        Assert.True(Interlocked.Read(ref stashed) >= 1);
        Assert.True(Interlocked.Read(ref drained) >= 1);
        await grain.GracefulDisconnectAsync(sessionId);
    }

    private static RpcCall AddCall(int value, MetaTraceContext trace)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, value);
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, 0);
        return new RpcCall
        {
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Add_v0,
            Payload = buffer.WrittenSpan.ToArray(),
            CallerId = "test",
            CallerClientVersion = "1.0.0",
            TraceParent = trace.ToTraceParent(),
        };
    }

    // ── Session lifecycle ────────────────────────────────────────────────

    private sealed class SessionRecorder : IMetaServerObserver
    {
        public readonly ConcurrentQueue<(string Player, string Session)> Started = new();
        public readonly ConcurrentQueue<(string Player, string Session, MetaSessionEndReason Reason)> Ended = new();
        public void OnCallCompleted(in MetaCallInfo call) { }
        public void OnSessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed) => Started.Enqueue((playerId, sessionId));
        public void OnSessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs) => Ended.Enqueue((playerId, sessionId, reason));
        public List<MetaSessionEndReason> EndsOf(string player) => Ended.Where(e => e.Player == player).Select(e => e.Reason).ToList();
        public string FirstSessionOf(string player) => Started.First(s => s.Player == player).Session;
        // By session id: a client may reconnect after a drop and open sessions of its own.
        public List<MetaSessionEndReason> EndsOfSession(string session) => Ended.Where(e => e.Session == session).Select(e => e.Reason).ToList();
    }

    private sealed class FixedMonitor(MetaTelemetryOptions value) : IOptionsMonitor<MetaTelemetryOptions>
    {
        public MetaTelemetryOptions CurrentValue => value;
        public MetaTelemetryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<MetaTelemetryOptions, string?> listener) => null;
    }

    private InProcessServer ServerWith(SessionRecorder recorder)
        => new(_fixture.CreateHandlerFactory(telemetry: new SharedMetaTelemetry(
            new FixedMonitor(new MetaTelemetryOptions()), new IMetaServerObserver[] { recorder }, null)));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }

    [Fact(Timeout = 30_000)]
    public async Task GracefulDisconnect_EndsSessionOnce_AsGraceful()
    {
        var recorder = new SessionRecorder();
        var server = ServerWith(recorder);
        var player = $"tel-end-g-{Guid.NewGuid():N}";
        var client = new TestClientSetup(server, player);
        await client.ConnectAsync();
        Assert.Single(recorder.Started, s => s.Player == player);

        // Dispose = GracefulDisconnect followed by the transport close.
        await client.DisposeAsync();
        await WaitUntil(() => recorder.EndsOf(player).Count > 0);
        await Task.Delay(200);

        Assert.Equal(new[] { MetaSessionEndReason.Graceful }, recorder.EndsOf(player));
    }

    [Fact(Timeout = 30_000)]
    public async Task TransportDrop_EndsSessionOnce_AsTransportDrop()
    {
        var recorder = new SessionRecorder();
        var server = ServerWith(recorder);
        var player = $"tel-end-d-{Guid.NewGuid():N}";
        await using var client = new TestClientSetup(server, player);
        await client.ConnectAsync();
        var session = recorder.FirstSessionOf(player);

        await server.ForceDisconnectAsync(client.ConnectionId);
        await WaitUntil(() => recorder.EndsOfSession(session).Count > 0);
        await Task.Delay(200); // a duplicate would arrive right behind the first

        Assert.Equal(new[] { MetaSessionEndReason.TransportDrop }, recorder.EndsOfSession(session));
    }

    [Fact(Timeout = 30_000)]
    public async Task SupersededSession_KeepsSupersededReason_ThroughTransportClose()
    {
        var recorder = new SessionRecorder();
        var server = ServerWith(recorder);
        var player = $"tel-end-s-{Guid.NewGuid():N}";
        var first = new TestClientSetup(server, player);
        await first.ConnectAsync();
        var firstConnection = first.ConnectionId;
        var firstSession = recorder.FirstSessionOf(player);

        // Second device, same player: the grain tells the first handler it was superseded.
        await using var second = new TestClientSetup(server, player);
        await second.ConnectAsync();
        await Task.Delay(200); // OneWay notice to the first handler

        // The transport close that follows must not overwrite the reason with transport_drop.
        await server.ForceDisconnectAsync(firstConnection);
        await WaitUntil(() => recorder.EndsOfSession(firstSession).Count > 0);
        await Task.Delay(200);

        Assert.Equal(new[] { MetaSessionEndReason.Superseded }, recorder.EndsOfSession(firstSession));
        await first.DisposeAsync();
    }
}
