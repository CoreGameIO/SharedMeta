using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using SharedMeta.Client.Telemetry;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Telemetry;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Regressions found in review of the observability work: failed methods recorded as success at
/// the transport edge, server spans inheriting the ambient activity, Query / Signal coverage.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class TelemetryReviewTests
{
    private readonly TestClusterFixture _fixture;

    public TelemetryReviewTests(TestClusterFixture fixture) => _fixture = fixture;

    private sealed class FixedMonitor(MetaTelemetryOptions value) : IOptionsMonitor<MetaTelemetryOptions>
    {
        public MetaTelemetryOptions CurrentValue => value;
        public MetaTelemetryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<MetaTelemetryOptions, string?> listener) => null;
    }

    private sealed class ServerCalls : IMetaServerObserver
    {
        public readonly ConcurrentQueue<MetaCallInfo> Calls = new();
        public void OnCallCompleted(in MetaCallInfo call) => Calls.Enqueue(call);
        public void OnSessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed) { }
        public void OnSessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs) { }
    }

    private sealed class ClientCalls : IMetaClientCallObserver
    {
        public readonly ConcurrentQueue<MetaCallInfo> Calls = new();
        public void OnCallCompleted(in MetaCallInfo call) => Calls.Enqueue(call);
    }

    private static SharedMetaTelemetry Telemetry(IMetaServerObserver? observer = null, Action<MetaTelemetryOptions>? configure = null)
    {
        var options = new MetaTelemetryOptions { MethodMetrics = MethodMetricsMode.All };
        configure?.Invoke(options);
        return new SharedMetaTelemetry(new FixedMonitor(options), observer == null ? null : new[] { observer }, null);
    }

    /// <summary>Collects (instrument, service, method, result) for one service.</summary>
    private sealed class Points : IDisposable
    {
        private readonly MeterListener _listener = new();
        public readonly ConcurrentQueue<(string Instrument, string? Method, string? Result)> Items = new();

        public Points(string service)
        {
            _listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == SharedMetaMeters.MeterName) l.EnableMeasurementEvents(i); };
            void Add(Instrument i, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                string? s = null, m = null, r = null;
                foreach (var t in tags)
                {
                    if (t.Key == "service") s = t.Value as string;
                    else if (t.Key == "method") m = t.Value as string;
                    else if (t.Key == "result") r = t.Value as string;
                }
                if (s == service) Items.Enqueue((i.Name, m, r));
            }
            _listener.SetMeasurementEventCallback<double>((i, _, tags, _) => Add(i, tags));
            _listener.SetMeasurementEventCallback<long>((i, _, tags, _) => Add(i, tags));
            _listener.Start();
        }

        public bool Has(string instrument, string method, string result)
            => Items.Any(p => p.Instrument == instrument && p.Method == method && p.Result == result);

        // Entity-grain instruments read the silo's telemetry options (default Annotated), so the
        // test service's methods are folded into _other there; the handler uses the test's options.
        public bool HasFolded(string instrument, string method, string result)
            => Items.Any(p => p.Instrument == instrument && (p.Method == method || p.Method == MethodTags.OtherMethod) && p.Result == result);

        public void Dispose() => _listener.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedMethod_IsErrorAtTheEntityAndAtTheTransportEdge_AndForObservers()
    {
        using var points = new Points("IFailureService");
        var observer = new ServerCalls();
        var server = new InProcessServer(_fixture.CreateHandlerFactory(telemetry: Telemetry(observer)));
        await using var client = new TestClientSetup(server, "tel-fail-" + Guid.NewGuid().ToString("N")[..6]);
        await client.ConnectAsync();
        var api = await client.CreateResolver().GetServiceAsync<FailureServiceApiClient>($"tel_fail_{Guid.NewGuid():N}");

        await Assert.ThrowsAnyAsync<Exception>(() => api.AddThenThrowAsync(1));

        Assert.True(points.HasFolded("sharedmeta.entity.rpc.duration", "AddThenThrow", MetaCallResults.Error));
        Assert.True(points.Has("sharedmeta.server.rpc.total_duration", "AddThenThrow", MetaCallResults.Error));
        Assert.False(points.Has("sharedmeta.server.rpc.total_duration", "AddThenThrow", MetaCallResults.Success));
        var call = Assert.Single(observer.Calls, c => c.Method == "AddThenThrow");
        Assert.Equal(MetaCallResults.Error, call.Result);
        Assert.Equal(MetaCallErrorKinds.ProviderError, call.ErrorKind);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerSpan_ContinuesOnlyTheClientTrace_NeverTheAmbientActivity(bool acceptClientTrace)
    {
        var serverSpans = new ConcurrentQueue<Activity>();
        using var outerSource = new ActivitySource("Tel.Review.Outer");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SharedMetaActivities.SourceName || s.Name == outerSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.OperationName == SharedMetaActivities.SpanServerRpc) serverSpans.Enqueue(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var server = new InProcessServer(_fixture.CreateHandlerFactory(
            telemetry: Telemetry(configure: o => o.AcceptClientTraceContext = acceptClientTrace)));
        var player = "tel-amb-" + Guid.NewGuid().ToString("N")[..6];
        await using var client = new TestClientSetup(server, player);
        await client.ConnectAsync();
        var counter = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>($"tel_amb_{Guid.NewGuid():N}");

        // The host's span is ambient on the client flow; InProcess runs the handler on the same
        // flow, the way ASP.NET's request / connection activity is ambient on a real server.
        using (var outer = outerSource.StartActivity("host.operation")!)
        {
            await counter.AddValueAsync(1, 1);
            var span = Assert.Single(serverSpans);
            if (acceptClientTrace)
                Assert.Equal(outer.TraceId, span.TraceId);   // client joined the host trace and sent it
            else
                Assert.NotEqual(outer.TraceId, span.TraceId); // policy: own trace, ambient ignored
            Assert.Same(outer, Activity.Current);             // the flow gets its own activity back
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task QueryAndSignal_AreMeasured_AndQueryReachesTheClientObserver()
    {
        using var points = new Points("ICounterService");
        var clientCalls = new ClientCalls();
        var server = new InProcessServer(_fixture.CreateHandlerFactory(telemetry: Telemetry()));
        await using var client = new TestClientSetup(server, "tel-qs-" + Guid.NewGuid().ToString("N")[..6],
            telemetry: new MetaClientTelemetryOptions { CallObserver = clientCalls });
        await client.ConnectAsync();
        var entityId = $"tel_qs_{Guid.NewGuid():N}";
        var counter = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(2, 1);

        var query = new CounterServiceQueryApi(client.Connection, client.Serializer, clientCalls).EntityApi(entityId);
        Assert.Equal(2, await query.PeekSumAsync());
        counter.NotifyHeartbeatSignal(DateTime.UtcNow.Ticks);

        for (int i = 0; i < 100 && !points.HasFolded("sharedmeta.entity.signal.count", "NotifyHeartbeat", MetaCallResults.Success); i++)
            await Task.Delay(20);

        Assert.True(points.HasFolded("sharedmeta.entity.query.duration", "PeekSum", MetaCallResults.Success));
        Assert.True(points.HasFolded("sharedmeta.entity.signal.count", "NotifyHeartbeat", MetaCallResults.Success));
        var q = Assert.Single(clientCalls.Calls, c => c.Op == MetaCallOps.Query);
        Assert.Equal("PeekSum", q.Method);
        Assert.Equal(32, q.CallId.Length);
        Assert.Contains(clientCalls.Calls, c => c.Op == MetaCallOps.Signal && c.Method == "NotifyHeartbeat");
    }
}
