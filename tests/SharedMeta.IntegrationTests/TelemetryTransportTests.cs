using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Auth;
using SharedMeta.Core.Transport;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Telemetry;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>Records <c>sharedmeta.transport.*</c> / <c>sharedmeta.auth.requests</c> measurements.</summary>
internal sealed class TransportMeasurements : IDisposable
{
    public sealed record Point(string Instrument, IReadOnlyDictionary<string, object?> Tags);

    private readonly MeterListener _listener = new();
    public readonly ConcurrentQueue<Point> Points = new();

    public TransportMeasurements()
    {
        _listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == SharedMetaMeters.MeterName
                && (i.Name.StartsWith("sharedmeta.transport.") || i.Name == "sharedmeta.auth.requests"))
                l.EnableMeasurementEvents(i);
        };
        _listener.SetMeasurementEventCallback<long>((i, _, tags, _) => Add(i, tags));
        _listener.SetMeasurementEventCallback<double>((i, _, tags, _) => Add(i, tags));
        _listener.Start();
    }

    private void Add(Instrument i, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var d = new Dictionary<string, object?>();
        foreach (var t in tags) d[t.Key] = t.Value;
        Points.Enqueue(new Point(i.Name, d));
    }

    public List<Point> For(string instrument, string endpointPrefix) => Points
        .Where(p => p.Instrument == instrument && (p.Tags.GetValueOrDefault("endpoint") as string)?.StartsWith(endpointPrefix) == true)
        .ToList();

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// Opt-in transport metrics: HTTP endpoint filters on a real Kestrel host (port 0), the hub
/// filter driven directly, and the off switch that keeps filters out of the pipeline.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class TelemetryTransportTests
{
    private readonly TestClusterFixture _fixture;

    public TelemetryTransportTests(TestClusterFixture fixture) => _fixture = fixture;

    private async Task<(WebApplication App, HttpClient Http)> StartHostAsync(bool transportMetrics, string prefix)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSharedMetaTelemetry(o => o.TransportMetrics = transportMetrics);
        builder.Services.AddSingleton(_fixture.GrainFactory);
        builder.Services.AddSingleton(new JwtTokenService(new MetaAuthOptions { SecretKey = new string('k', 48) }));
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthorization();

        var group = app.MapGroup(prefix);
        group.MapGet("/ok", () => Results.Ok("ok"));
        group.MapGet("/bad", () => Results.BadRequest());
        group.MapGet("/boom", (Func<IResult>)(() => throw new InvalidOperationException("boom")));
        group.MapGet("/poll", () => Results.Ok());
        group.WithMetaTransportTelemetry(app.Services, TransportTelemetry.HttpPolling,
            skipDuration: route => route.EndsWith("/poll", StringComparison.Ordinal));
        app.MapMetaAuth(prefix + "/auth");

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    [Fact(Timeout = 60_000)]
    public async Task EndpointFilter_RecordsResultPerRoute_AndSkipsLongPollDuration()
    {
        using var m = new TransportMeasurements();
        var prefix = "/t" + Guid.NewGuid().ToString("N")[..8];
        var (app, http) = await StartHostAsync(transportMetrics: true, prefix);
        await using var _ = app;
        using var __ = http;

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(prefix + "/ok")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(prefix + "/bad")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await http.GetAsync(prefix + "/boom")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(prefix + "/poll")).StatusCode);

        var counts = m.For("sharedmeta.transport.requests", prefix);
        string ResultOf(string route) => (string)counts.Single(p => (string)p.Tags["endpoint"]! == prefix + route).Tags["result"]!;
        Assert.Equal(TransportTelemetry.Success, ResultOf("/ok"));
        Assert.Equal(TransportTelemetry.BadRequest, ResultOf("/bad"));
        Assert.Equal(TransportTelemetry.Error, ResultOf("/boom"));
        Assert.All(counts, p => Assert.Equal(TransportTelemetry.HttpPolling, p.Tags["transport"]));

        var timed = m.For("sharedmeta.transport.request.duration", prefix).Select(p => (string)p.Tags["endpoint"]!).ToList();
        Assert.Contains(prefix + "/ok", timed);
        Assert.DoesNotContain(prefix + "/poll", timed);
    }

    [Fact(Timeout = 60_000)]
    public async Task AuthEndpoints_RecordProvider_FromResolvedValue()
    {
        using var m = new TransportMeasurements();
        var prefix = "/t" + Guid.NewGuid().ToString("N")[..8];
        var (app, http) = await StartHostAsync(transportMetrics: true, prefix);
        await using var _ = app;
        using var __ = http;

        var login = await http.PostAsJsonAsync(prefix + "/auth/login", new { deviceId = "tel-dev-" + Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var invalid = await http.PostAsJsonAsync(prefix + "/auth/login-platform", new { platform = "made-up", platformToken = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var auth = m.For("sharedmeta.auth.requests", prefix);
        var ok = auth.Single(p => ((string)p.Tags["endpoint"]!).EndsWith("/login"));
        Assert.Equal("device", ok.Tags["provider"]);
        Assert.Equal(TransportTelemetry.Success, ok.Tags["result"]);
        // An unsupported platform never becomes a tag value.
        var rejected = auth.Single(p => ((string)p.Tags["endpoint"]!).EndsWith("/login-platform"));
        Assert.Equal("none", rejected.Tags["provider"]);
        Assert.Equal(TransportTelemetry.BadRequest, rejected.Tags["result"]);
    }

    [Fact(Timeout = 60_000)]
    public async Task TransportMetricsOff_NoFilterInPipeline()
    {
        using var m = new TransportMeasurements();
        var prefix = "/t" + Guid.NewGuid().ToString("N")[..8];
        var (app, http) = await StartHostAsync(transportMetrics: false, prefix);
        await using var _ = app;
        using var __ = http;

        await http.GetAsync(prefix + "/ok");
        await http.PostAsJsonAsync(prefix + "/auth/login", new { deviceId = "tel-dev-" + Guid.NewGuid().ToString("N") });

        Assert.Empty(m.For("sharedmeta.transport.requests", prefix));
        Assert.Empty(m.For("sharedmeta.auth.requests", prefix));
    }

    // ── Hub filter through real SignalR (registration is the part that can silently fail) ──

    private static async Task<int> HubInvocationsRecordedAsync(bool transportMetrics, string method)
    {
        using var m = new TransportMeasurements();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSharedMetaTelemetry(o => o.TransportMetrics = transportMetrics);
        builder.Services.AddSignalR().AddMetaHubTelemetry<TelemetryEchoHub>(TransportTelemetry.SignalR);
        await using var app = builder.Build();
        app.MapHub<TelemetryEchoHub>("/tel-hub");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        await using var connection = Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilderHttpExtensions
            .WithUrl(new Microsoft.AspNetCore.SignalR.Client.HubConnectionBuilder(), address + "/tel-hub")
            .Build();
        await connection.StartAsync();
        Assert.Equal("hi", await Microsoft.AspNetCore.SignalR.Client.HubConnectionExtensions.InvokeAsync<string>(connection, method, "hi"));
        await connection.StopAsync();

        return m.For("sharedmeta.transport.requests", method)
            .Count(p => (string?)p.Tags["transport"] == TransportTelemetry.SignalR && (string?)p.Tags["result"] == TransportTelemetry.Success);
    }

    [Fact(Timeout = 60_000)]
    public async Task HubFilter_IsActiveOnARealHub_WhenTransportMetricsOn()
        => Assert.Equal(1, await HubInvocationsRecordedAsync(true, nameof(TelemetryEchoHub.TelEchoOn)));

    [Fact(Timeout = 60_000)]
    public async Task HubFilter_IsAbsent_WhenTransportMetricsOff()
        => Assert.Equal(0, await HubInvocationsRecordedAsync(false, nameof(TelemetryEchoHub.TelEchoOff)));

    // ── Hub filter, driven directly ──────────────────────────────────────

    private sealed class DummyHub : Hub
    {
        public string TelPing() => "pong";
        public string TelThrow() => throw new InvalidOperationException();
    }

    private sealed class FakeCallerContext : HubCallerContext
    {
        public override string ConnectionId { get; } = "conn-" + Guid.NewGuid().ToString("N")[..6];
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; } = new(new ClaimsIdentity(new[] { new Claim("sub", "hub-player") }));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    [Fact]
    public async Task HubFilter_RecordsPerHubMethod_AndOpensPlayerScope()
    {
        using var m = new TransportMeasurements();
        var filter = new HubTelemetryFilter(TransportTelemetry.Mux, NullLoggerFactory.Instance, logScopes: true);
        var hub = new DummyHub();
        var caller = new FakeCallerContext();
        HubInvocationContext Ctx(string method) => new(caller, new ServiceCollection().BuildServiceProvider(), hub,
            typeof(DummyHub).GetMethod(method)!, Array.Empty<object?>());

        var result = await filter.InvokeMethodAsync(Ctx(nameof(DummyHub.TelPing)), c => new ValueTask<object?>("pong"));
        Assert.Equal("pong", result);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await filter.InvokeMethodAsync(Ctx(nameof(DummyHub.TelThrow)), c => throw new InvalidOperationException()));

        var counts = m.For("sharedmeta.transport.requests", "TelP").Concat(m.For("sharedmeta.transport.requests", "TelT")).ToList();
        Assert.Equal(TransportTelemetry.Success, counts.Single(p => (string)p.Tags["endpoint"]! == nameof(DummyHub.TelPing)).Tags["result"]);
        Assert.Equal(TransportTelemetry.Error, counts.Single(p => (string)p.Tags["endpoint"]! == nameof(DummyHub.TelThrow)).Tags["result"]);
        Assert.All(counts, p => Assert.Equal(TransportTelemetry.Mux, p.Tags["transport"]));
    }
}

public class TelemetryEchoHub : Hub
{
    public string TelEchoOn(string s) => s;
    public string TelEchoOff(string s) => s;
}

/// <summary>
/// The new optional fields (<c>TraceParent</c>, <c>SupportsTraceContext</c>) must be invisible to
/// peers built before them, both ways, on the codecs that actually cross the wire: int-keyed
/// MessagePack (SignalR) and JSON (HTTP polling / Unity HTTP).
/// </summary>
public class TelemetryWireCompatTests
{
    // Pre-0.47 shapes, keys as shipped.
    [MessagePackObject]
    public class OldRpcCallRequest
    {
        [Key(0)] public string EntityId { get; set; } = "";
        [Key(1)] public long RequestId { get; set; }
        [Key(4)] public ReadOnlyMemory<byte> Payload { get; set; }
        [Key(5)] public long LastAcknowledgedSequence { get; set; }
        [Key(6)] public bool IsCrossOptimistic { get; set; }
        [Key(7)] public long ServerTimeTicks { get; set; }
        [Key(9)] public ushort MethodId { get; set; }
        [Key(11)] public ushort StateTypeId { get; set; }
    }

    [MessagePackObject]
    public class OldSessionConnectResponse
    {
        [Key(0)] public bool Success { get; set; }
        [Key(1)] public string? Error { get; set; }
        [Key(17)] public bool DeepDesyncActive { get; set; }
    }

    private static readonly MessagePackSerializerOptions Msgpack = MessagePackSerializerOptions.Standard;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static RpcCallRequest NewRequest() => new()
    {
        EntityId = "e1", RequestId = 7, Payload = new byte[] { 1, 2, 3 }, MethodId = 4, StateTypeId = 2,
        ServerTimeTicks = 42, TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
    };

    [Fact]
    public void MessagePack_NewRequest_ReadsOnOldPeer()
    {
        var old = MessagePackSerializer.Deserialize<OldRpcCallRequest>(MessagePackSerializer.Serialize(NewRequest(), Msgpack), Msgpack);
        Assert.Equal("e1", old.EntityId);
        Assert.Equal(7, old.RequestId);
        Assert.Equal(4, old.MethodId);
        Assert.Equal(2, old.StateTypeId);
        Assert.Equal(new byte[] { 1, 2, 3 }, old.Payload.ToArray());
    }

    [Fact]
    public void MessagePack_OldRequest_ReadsOnNewPeer_WithoutTrace()
    {
        var bytes = MessagePackSerializer.Serialize(new OldRpcCallRequest { EntityId = "e1", RequestId = 7, MethodId = 4 }, Msgpack);
        var current = MessagePackSerializer.Deserialize<RpcCallRequest>(bytes, Msgpack);
        Assert.Equal(7, current.RequestId);
        Assert.Null(current.TraceParent);
    }

    [Fact]
    public void MessagePack_NewConnectResponse_ReadsOnOldClient()
    {
        var bytes = MessagePackSerializer.Serialize(new SessionConnectResponse { Success = true, DeepDesyncActive = true, SupportsTraceContext = true }, Msgpack);
        var old = MessagePackSerializer.Deserialize<OldSessionConnectResponse>(bytes, Msgpack);
        Assert.True(old.Success);
        Assert.True(old.DeepDesyncActive);
    }

    [Fact]
    public void Json_BothDirections()
    {
        var newJson = JsonSerializer.Serialize(NewRequest(), Json);
        var old = JsonSerializer.Deserialize<OldRpcCallRequest>(newJson, Json)!;
        Assert.Equal(7, old.RequestId);

        var oldJson = JsonSerializer.Serialize(new OldRpcCallRequest { EntityId = "e1", RequestId = 9 }, Json);
        var current = JsonSerializer.Deserialize<RpcCallRequest>(oldJson, Json)!;
        Assert.Equal(9, current.RequestId);
        Assert.Null(current.TraceParent);
    }
}
