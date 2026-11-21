using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using SharedMeta.Client.Telemetry;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Telemetry;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Call identity across hops: the client's call id (W3C trace id) must be the CallId in the log
/// scope of every server hop of the same call, and per-method metric tags must follow
/// <see cref="MetaTelemetryOptions"/>.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class TelemetryTests
{
    private readonly TestClusterFixture _fixture;

    public TelemetryTests(TestClusterFixture fixture) => _fixture = fixture;

    private sealed class RecordingObserver : IMetaClientCallObserver
    {
        public readonly ConcurrentQueue<MetaCallInfo> Calls = new();
        public void OnCallCompleted(in MetaCallInfo call) => Calls.Enqueue(call);
    }

    private static SharedMetaTelemetry TelemetryWith(Action<MetaTelemetryOptions> configure)
    {
        var options = new MetaTelemetryOptions();
        configure(options);
        return new SharedMetaTelemetry(new FixedMonitor(options), null, null);
    }

    private sealed class FixedMonitor(MetaTelemetryOptions value) : IOptionsMonitor<MetaTelemetryOptions>
    {
        public MetaTelemetryOptions CurrentValue => value;
        public MetaTelemetryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<MetaTelemetryOptions, string?> listener) => null;
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientCallId_IsTheCallIdOfEverySessionAndEntityGrainLog()
    {
        var entityId = $"tel_flow_{Guid.NewGuid():N}";
        var observer = new RecordingObserver();
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "tel-alice",
            telemetry: new MetaClientTelemetryOptions { CallObserver = observer });
        await client.ConnectAsync();
        var counter = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        using (TelemetryLogCapture.Instance.Arm())
        {
            await counter.AddValueAsync(3, 1);

            var call = Assert.Single(observer.Calls, c => c.Method == "Add");
            Assert.Equal(32, call.CallId.Length);
            Assert.Equal(MetaCallResults.Success, call.Result);

            var entries = TelemetryLogCapture.Instance.Entries
                .Where(e => e.Scope.TryGetValue(MetaLogFields.CallId, out var id) && (string?)id == call.CallId)
                .ToList();

            var session = entries.Where(e => e.Category.Contains("SessionManagerGrain")).ToList();
            Assert.NotEmpty(session);
            Assert.All(session, e =>
            {
                Assert.Equal("ICounterService", e.Scope[MetaLogFields.Service]);
                Assert.Equal("Add", e.Scope[MetaLogFields.Method]);
                Assert.Equal("tel-alice", e.Scope[MetaLogFields.PlayerId]);
                Assert.True(e.Scope.ContainsKey(MetaLogFields.RequestId));
            });

            // The entity grain persists inside its own hop: the storage write is logged under the
            // same call id, with the entity hop's span — distinct from the session hop's span.
            Assert.Contains(entries, e => e.Category.StartsWith("Orleans.Storage")
                                          && (string?)e.Scope[MetaLogFields.EntityId] == entityId);
            var spans = entries.Select(e => e.Scope.GetValueOrDefault(MetaLogFields.SpanId)).Distinct().Count();
            Assert.True(spans >= 2, $"expected session and entity hops with distinct spans, got {spans}");
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AcceptClientTraceContextOff_ServerStartsItsOwnTrace()
    {
        var entityId = $"tel_own_{Guid.NewGuid():N}";
        var observer = new RecordingObserver();
        var server = new InProcessServer(_fixture.CreateHandlerFactory(
            telemetry: TelemetryWith(o => o.AcceptClientTraceContext = false)));
        await using var client = new TestClientSetup(server, "tel-bob",
            telemetry: new MetaClientTelemetryOptions { CallObserver = observer });
        await client.ConnectAsync();
        var counter = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        using (TelemetryLogCapture.Instance.Arm())
        {
            await counter.AddValueAsync(1, 1);
            var clientCallId = Assert.Single(observer.Calls, c => c.Method == "Add").CallId;

            var serverIds = TelemetryLogCapture.Instance.Entries
                .Where(e => e.Category.Contains("SessionManagerGrain")
                            && (string?)e.Scope.GetValueOrDefault(MetaLogFields.EntityId) == entityId)
                .Select(e => (string?)e.Scope[MetaLogFields.CallId])
                .Distinct()
                .ToList();

            Assert.NotEmpty(serverIds);
            Assert.DoesNotContain(clientCallId, serverIds);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task MethodTag_FollowsMethodMetricsOption()
    {
        var measurements = new ConcurrentQueue<(string Service, string Method)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == SharedMetaMeters.MeterName
                && instrument.Name == "sharedmeta.server.rpc.total_duration")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            string? service = null, method = null;
            foreach (var t in tags)
            {
                if (t.Key == "service") service = t.Value as string;
                else if (t.Key == "method") method = t.Value as string;
            }
            if (service == "ICounterService") measurements.Enqueue((service, method ?? ""));
        });
        listener.Start();

        async Task CallOnce(SharedMetaTelemetry? telemetry)
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory(telemetry: telemetry));
            await using var client = new TestClientSetup(server, "tel-carol-" + Guid.NewGuid().ToString("N")[..6]);
            await client.ConnectAsync();
            var counter = await client.CreateResolver()
                .GetServiceAsync<CounterServiceApiClient>($"tel_tag_{Guid.NewGuid():N}");
            await counter.AddValueAsync(1, 1);
        }

        // Default (Annotated) and no [MetaMethod(Metrics = On)] on the test service → folded.
        await CallOnce(null);
        Assert.Contains(measurements, m => m.Method == MethodTags.OtherMethod);
        Assert.DoesNotContain(measurements, m => m.Method == "Add");

        measurements.Clear();
        await CallOnce(TelemetryWith(o => o.MethodMetrics = MethodMetricsMode.All));
        Assert.Contains(measurements, m => m.Method == "Add");
    }
}

/// <summary>Pure-logic checks for the telemetry building blocks.</summary>
public class TelemetryUnitTests
{
    [Fact]
    public void TraceParent_RoundTrips()
    {
        var trace = MetaTraceContext.CreateRandom();
        var text = trace.ToTraceParent();
        Assert.NotNull(text);
        Assert.Equal(MetaTraceContext.TraceParentLength, text!.Length);
        Assert.True(MetaTraceContext.TryParse(text, out var parsed));
        Assert.Equal(trace, parsed);
        Assert.Equal(trace.TraceIdHex, parsed.TraceIdHex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01-extra")]
    [InlineData("00-0AF7651916CD43DD8448EB211C80319C-B7AD6B7169203331-01")]   // upper case
    [InlineData("00-00000000000000000000000000000000-b7ad6b7169203331-01")]   // zero trace
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-0000000000000000-01")]   // zero span
    [InlineData("ff-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")]   // other version
    [InlineData("00-0af7651916cd43dd8448eb211c80319g-b7ad6b7169203331-01")]   // non-hex
    [InlineData("00_0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")]   // separator
    public void TraceParent_RejectsMalformed(string? input)
    {
        Assert.False(MetaTraceContext.TryParse(input, out var parsed));
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public void TraceParent_AcceptsW3CSample()
    {
        Assert.True(MetaTraceContext.TryParse("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", out var t));
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", t.TraceIdHex);
        Assert.Equal("b7ad6b7169203331", t.SpanIdHex);
        Assert.True(t.IsSampled);
    }

    [Fact]
    public void RandomIds_AreNonZeroAndDistinct()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 10_000; i++)
        {
            var t = MetaTraceContext.CreateRandom();
            Assert.True(t.IsValid);
            Assert.True(seen.Add(t.TraceIdHex));
        }
    }

    [Fact]
    public void CallContext_ExposesOnlyPresentFieldsAsScope()
    {
        var ctx = new MetaCallContext(MetaTraceContext.CreateRandom(), MetaCallOps.Rpc,
            playerId: "p1", service: "IShop", method: "Buy", requestId: 7);
        var fields = ctx.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(ctx.CallId, fields[MetaLogFields.CallId]);
        Assert.Equal("p1", fields[MetaLogFields.PlayerId]);
        Assert.Equal(7L, fields[MetaLogFields.RequestId]);
        Assert.False(fields.ContainsKey(MetaLogFields.EntityId));
        Assert.Contains("IShop.Buy", ctx.ToString());
    }

    [Fact]
    public async Task CallScope_RestoresPreviousContext()
    {
        Assert.Null(MetaCallContext.Current);
        using (var outer = MetaCallScope.Begin("IA", "A", MetaCallOps.Rpc, "e1"))
        {
            Assert.Same(outer.Context, MetaCallContext.Current);
            using (var inner = MetaCallScope.Begin("IB", "B", MetaCallOps.Rpc, "e2"))
            {
                // Nested call continues the outer trace with its own span.
                Assert.Equal(outer.Context.CallId, inner.Context.CallId);
                Assert.NotEqual(outer.Context.Trace.SpanId, inner.Context.Trace.SpanId);
            }
            Assert.Same(outer.Context, MetaCallContext.Current);
            await Task.Yield();
            Assert.Same(outer.Context, MetaCallContext.Current);
        }
        Assert.Null(MetaCallContext.Current);
    }

    private static MetaServerSignature Signature() => new()
    {
        Methods = new List<ServerMethodEntry>
        {
            new() { ServiceName = "IShop", Alias = "Buy", GlobalIndex = 0, StateTypeName = "Game.ShopState", Mode = ExecutionMode.Server, Metrics = MetaMetrics.On },
            new() { ServiceName = "IShop", Alias = "Peek", GlobalIndex = 1, StateTypeName = "Game.ShopState", Mode = ExecutionMode.Query },
            new() { ServiceName = "IShop", Alias = "Spam", GlobalIndex = 2, StateTypeName = "Game.ShopState", Metrics = MetaMetrics.Off },
            new() { ServiceName = "IChat", Alias = "Say", GlobalIndex = 3, StateTypeName = "Game.ChatState" },
        }
    };

    private static string MethodTag(MethodTelemetryTable table, ushort id) => (string)table.Get(id).MethodTag.Value!;

    [Fact]
    public void MethodTable_AnnotatedMode_OnlyOnMethodsHaveSeries()
    {
        var table = new MethodTelemetryTable(Signature(), new MetaTelemetryOptions());
        Assert.Equal("Buy", MethodTag(table, 0));
        Assert.Equal(MethodTags.OtherMethod, MethodTag(table, 1));
        Assert.Equal(MethodTags.OtherMethod, MethodTag(table, 3));
        Assert.Equal("ShopState", table.Get(0).StateType);
        Assert.Equal("server", table.Get(0).Mode);
        Assert.Equal("query", table.Get(1).Mode);
    }

    [Fact]
    public void MethodTable_AllMode_RespectsOff()
    {
        var table = new MethodTelemetryTable(Signature(), new MetaTelemetryOptions { MethodMetrics = MethodMetricsMode.All });
        Assert.Equal("Peek", MethodTag(table, 1));
        Assert.Equal(MethodTags.OtherMethod, MethodTag(table, 2));
    }

    [Fact]
    public void MethodTable_PatternsOverrideAttributes_ExcludeWins()
    {
        var table = new MethodTelemetryTable(Signature(), new MetaTelemetryOptions
        {
            MethodMetrics = MethodMetricsMode.Off,
            IncludeMethods = { "IChat.*", "IShop.Spam", "IShop.Buy" },
            ExcludeMethods = { "IShop.Buy" },
        });
        Assert.Equal(MethodTags.OtherMethod, MethodTag(table, 0));
        Assert.Equal("Spam", MethodTag(table, 2));
        Assert.Equal("Say", MethodTag(table, 3));
    }

    [Fact]
    public void MethodTable_UnknownIdsAndStrings_NeverEchoInput()
    {
        var table = new MethodTelemetryTable(Signature(), new MetaTelemetryOptions());
        Assert.Same(MethodTags.UnknownMethod, table.Get(999));
        Assert.Same(MethodTags.UnknownMethod, table.Find("IShop", "<script>"));
        Assert.Equal("Buy", table.Find("IShop", "Buy").MethodName);
    }
}
