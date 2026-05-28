using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using SharedMeta.Core;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Server.Core.Storage;
using SharedMeta.Core.Framework;
using SharedMeta.Orleans.Framework;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Transport.SignalR;
using SharedMeta.Transport.HttpPolling;
using SharedMeta.Auth;
using SharedMeta.Server.Core.Session;
using Expedition.Shared;
using Expedition.Shared.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Serilog;

// Port configuration: pass as first arg, e.g. `dotnet run -- 5000`
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5000;
var siloPort = 11111 + (port - 5000);
var gatewayPort = 30000 + (port - 5000);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");

builder.Host.UseSerilog((ctx, config) => config
    // Let SharedMeta log at Debug — needed to see [Desync] reports surfaced by
    // MetaConnectionHandler.LogDesync (which writes at Information level when the server is
    // configured with DesyncLogLevel.Debug). Serilog's implicit minimum is Information, so
    // without this override the Desync reports are dropped before reaching the sink.
    .MinimumLevel.Override("SharedMeta", Serilog.Events.LogEventLevel.Debug)
    .WriteTo.Console()
    // File sink: rolling daily file under ./logs. Each process run appends to the same day's file
    // so multiple server sessions in one day are captured in one place — exactly what we need
    // to diagnose cross-restart session-resume bugs by reading both pre-/stop and post-restart
    // traces in a single read.
    .WriteTo.File(
        path: "logs/expedition-.log",
        rollingInterval: Serilog.RollingInterval.Day,
        shared: true,
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 7)
);

var serializer = new MemoryPackMetaSerializer();
builder.Services.AddSingleton<IMetaSerializer>(serializer);

// Orleans Silo
builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering(siloPort, gatewayPort)
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "expedition-cluster";
            options.ServiceId = "expedition-server";
        })
        .AddFileGrainStorage("Default", o => {
            o.UseOrleansSerializer = false;
            o.RootDirectory = "./data";
        })
        .ConfigureServices(services =>
        {
            services.AddSingleton<IMetaSerializer>(serializer);
            services.Configure<EntityGrainOptions>(o =>
            {
                o.SubscriberTtl = TimeSpan.FromMinutes(10);
                // DeepDesyncEnabled left null — controlled per-session via client SetDebugOptions

                // Mix in non-deterministic entropy when seeding fresh entity randoms.
                // Without this, recreating an entity with the same id (profile reset →
                // expedition counter resets to 1 → "expedition-{playerId}-1" recycled) would
                // reseed the random from a deterministic string and produce the SAME map.
                // Replay-safe: the seed is server-only, clients receive the post-seed random
                // state via the subscribe snapshot.
                o.FreshRandomSeedFactory = (entityId, streamName) =>
                    $"{entityId}:{streamName}:{DateTime.UtcNow.Ticks:x}:{Random.Shared.NextInt64():x}";
                o.PersistencePolicy = PersistencePolicy.EveryNRequests(15);
            });

            services.ConfigureMeta(svc =>
            {
                svc.AddTransient<ILobbyRequester>(sp => new OrleansLobbyRequester(sp.GetRequiredService<IGrainFactory>()));
                svc.AddTransient<IRandomService, RandomServiceImpl>();
                svc.AddSingleton<IMetaConfigProvider<ExpeditionConfig>>(new DefaultExpeditionConfigProvider($"http://localhost:{port}"));
            });
        });
});

// SignalR — extended timeouts for game connections (server sends pings every 15 min,
// disconnects after 30 min of no response; defaults are 15s/30s which cause spurious drops)
builder.Services.AddSignalR(hubOptions =>
{
    hubOptions.EnableDetailedErrors = true;
    hubOptions.ClientTimeoutInterval = TimeSpan.FromMinutes(30);
    hubOptions.KeepAliveInterval = TimeSpan.FromMinutes(15);
}).AddMetaMessagePackProtocol();

// MetaConnectionHandlerFactory is registered by the generated ConfigureMeta()
// (passes MetaTransportOptions + IMetaSerializer for desync reporting)

// Authentication
builder.Services.AddMetaAuth(options =>
{
    options.SecretKey = "expedition-secret-key-at-least-32-characters!!";
    options.Issuer = "expedition-server";
});
builder.Services.AddSingleton(new MetaTransportOptions
{
    RequireAuthentication = true,
    AllowDebugApi = true,             // example project — debug API always available
    DesyncReportingEnabled = true,    // accept client follow-up reports
    DesyncLogLevel = DesyncLogLevel.Debug,  // log full text diff for inspection
    ServerVersion = "2.0.0",
    MinClientVersion = "1.1.0",
    MaxClientVersion = "2.0.*"
});

// HTTP Polling connection manager (for /meta-http transport)
builder.Services.AddSingleton<HttpPollingConnectionManager>(sp =>
    new HttpPollingConnectionManager(
        sp.GetRequiredService<IMetaConnectionHandlerFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));

// RPC ordering — required for HTTP polling (no wire-level FIFO guarantee).
// Also safe for SignalR (one extra int comparison in the in-order case).
builder.Services.Configure<SessionManagerOptions>(o => o.EnforceRpcOrder = true);

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapMetaAuth("/meta/auth");

app.MapHub<MetaHub>("/meta");
app.MapMetaHttpPolling("/meta-http");
app.MapGet("/", () => "Expedition.Server is running");

// Config download endpoint
app.MapGet("/meta/config/{major:int}/{minor:int}", (int major, int minor, IMetaSerializer ser, IMetaConfigProvider<ExpeditionConfig> provider) =>
{
    var config = provider.GetConfig(new MetaConfigVersion(major, minor));
    return Results.Bytes(ser.Pack(config), "application/octet-stream");
});

app.MapGet("/stop", (
    [FromServices] IServer server,
    [FromServices] IHubContext<MetaHub> hubContext,
    [FromServices] IHostApplicationLifetime appLifetime) =>
{
    _ = Task.Run(async () => {
        await Task.Delay(50);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            await server.StopAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        appLifetime.StopApplication();

    });

    return Results.Ok(new { message = "Shutdown." });
});

app.Logger.LogInformation("=== Expedition.Server ===");
app.Logger.LogInformation("Listening on http://localhost:{Port}", port);
app.Logger.LogInformation("Debug API enabled — clients can toggle deep desync detection at runtime");

await app.RunAsync();

/// <summary>
/// Two-branch demo config:
///   1.x = lean economy (rare treasures, small reward) — for legacy clients on the 1.x line
///   2.x = boosted economy (frequent treasures, big reward) — for current clients on 2.x
///
/// Routing client → branch is decided by <c>[MetaConfigVersion]</c> rules on
/// <see cref="ExpeditionConfig"/>; this provider just produces the config bytes when asked
/// for a specific version. Branch instances are memoized so the per-call config resolution
/// hot path doesn't reallocate.
/// </summary>
public class DefaultExpeditionConfigProvider : IMetaConfigProvider<ExpeditionConfig>
{
    private readonly string _baseUrl;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), ExpeditionConfig> _branchCache = new();

    public DefaultExpeditionConfigProvider(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    /// <summary>Latest deployed branch — what new entities default to.</summary>
    public MetaConfigVersion CurrentVersion => new(2, 0);

    public ExpeditionConfig GetConfig(MetaConfigVersion version)
        => _branchCache.GetOrAdd((version.Major, version.Minor), key => BuildConfig(key.Item1));

    private static ExpeditionConfig BuildConfig(int major)
    {
        if (major >= 2)
        {
            return new ExpeditionConfig
            {
                TreasurePercent = 25,    // ↑ from default 8 (more loot scattered on the map)
                TreasureReward  = 75,    // ↑ from default 25 (3× per chest)
            };
        }
        return new ExpeditionConfig
        {
            TreasurePercent = 5,         // ↓ from default 8 (rare treasures)
            TreasureReward  = 10,        // ↓ from default 25 (small reward per chest)
        };
    }

    public MetaConfigVersion ResolveLatestMatching(int major, int minor) => new(major, minor, 0);

    public string? GetDownloadUrl(MetaConfigVersion version)
        => $"{_baseUrl}/meta/config/{version.Major}/{version.Minor}";
}
