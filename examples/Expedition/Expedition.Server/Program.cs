using Expedition.Server;
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
using SharedMeta.Serialization.MemoryPack;
#if USE_HTTP_POLLING
using SharedMeta.Transport.HttpPolling;
#else
using SharedMeta.Transport.SignalR;
#endif
using SharedMeta.Orleans.Framework;
using SharedMeta.Auth;
using SharedMeta.Server.Core.Storage;
using SharedMeta.Core.Network;
using Expedition.Shared;
using Expedition.Shared.Server;

using Serilog;
using SharedMeta.Serialization.MessagePack;

// Port configuration: pass as first arg, e.g. `dotnet run -- 5100`
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5100;
var useServerPatch = args.Contains("--server-patch");
var siloPort = 11111 + (port - 5000);
var gatewayPort = 30000 + (port - 5000);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");

builder.Host.UseSerilog((ctx, config) => config
    .WriteTo.Console());

// Serializer
// MessagePack: configure composite resolver with generated resolvers from all assemblies
GeneratedMetaMessagePackConfiguration.Configure();
var serializer = new MessagePackMetaSerializer();
// MemoryPack:
//var serializer = new MemoryPackMetaSerializer();
builder.Services.AddSingleton<IMetaSerializer>(serializer);

// Config provider (shared between Orleans silo and app-level endpoint)
var configProvider = new ExpeditionConfigProvider($"http://localhost:{port}");
var playerConfigProvider = new PlayerConfigProvider($"http://localhost:{port}");
builder.Services.AddSingleton<IMetaConfigProvider<ExpeditionConfig>>(configProvider);
builder.Services.AddSingleton<IMetaConfigProvider<PlayerConfig>>(playerConfigProvider);

// Version policy:
//   ServerVersion     = 2.0.0   — current server build
//   MinClientVersion  = 1.2.0   — clients below 1.2 rejected at SessionConnect ("client too old")
//   MaxClientVersion  = 2.x.*   — accept any client up to major 2 (no upper-bound rejection yet)
// 1.2 / 2.0 both pass the cluster gate; their config branch is decided per-client by
// [MetaConfigVersion] rules on ExpeditionConfig. Once a profile migrates to schema 2 (after
// connecting with 2.0+), the per-entity Subscribe gate rejects 1.2 clients with "profile migrated."
builder.Services.AddSingleton(new MetaTransportOptions
{
    ServerVersion    = "2.0.0",
    MinClientVersion = "1.2.0",
    MaxClientVersion = "2.x.*",
    // Example project: let the client ask to be analysed (press D) and let its desync follow-up
    // reports through, so the server-side text diff shows up in the log. Both default to false —
    // a production silo would leave them that way.
    AllowDebugApi          = true,
    DesyncReportingEnabled = true,
    DesyncLogLevel         = DesyncLogLevel.Debug,
});

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
        .AddFileGrainStorage("Default", o => o.RootDirectory = "./data")
        .ConfigureServices(services =>
        {
            services.AddSingleton<IMetaSerializer>(serializer);
            services.Configure<EntityGrainOptions>(o =>
            {
                o.SubscriberTtl = TimeSpan.FromMinutes(10);
                // PerPlayer so the example demonstrates the interesting path: nobody is analysed
                // until a player asks (D in the client), and that choice survives their reconnect.
                // Forced would switch it on for everyone from connect — simpler, but then the
                // per-player half is never exercised.
                o.DeepDesyncMode = DeepDesyncMode.PerPlayer;
            });

            // ServerPatch mode: override CrossOptimistic methods to use server-side patching
            if (useServerPatch)
            {
                var modeProvider = new ExecutionModeProvider();
                modeProvider.SetMode(global::Expedition.Shared.Generated.GameMethodIds.IExpeditionService_Move_v0, ExecutionMode.ServerPatch);
                modeProvider.SetMode(global::Expedition.Shared.Generated.GameMethodIds.IExpeditionService_RemoveObstacle_v0, ExecutionMode.ServerPatch);
                services.AddSingleton<IExecutionModeProvider>(modeProvider);
            }

            services.ConfigureMeta(svc =>
            {
                svc.AddTransient<IRandomService, RandomServiceImpl>();
                svc.AddSingleton<IMetaConfigProvider<ExpeditionConfig>>(configProvider);
            });
        });
});

#if USE_HTTP_POLLING
// HTTP Polling connection manager
builder.Services.AddSingleton<HttpPollingConnectionManager>(sp =>
    new HttpPollingConnectionManager(
        sp.GetRequiredService<IMetaConnectionHandlerFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));
#else
// SignalR — extended timeouts for game connections
builder.Services.AddSignalR(hubOptions =>
{
    hubOptions.EnableDetailedErrors = true;
    hubOptions.ClientTimeoutInterval = TimeSpan.FromMinutes(30);
    hubOptions.KeepAliveInterval = TimeSpan.FromMinutes(15);
}).AddMetaMessagePackProtocol();
#endif

// MetaConnectionHandlerFactory is registered by the generated ConfigureMeta() above —
// it wires MetaServerSignature + IClientSignatureRegistry + ClientVersionPolicy etc.
// Do NOT re-register here with the bare 3-arg ctor: that drops signatureRegistry to
// null, which makes MetaConnectionHandler reject every RPC with "client signature not
// negotiated" once a client sends a non-zero ClientSignatureHash.

// Authentication (optional — server works without it too)
builder.Services.AddMetaAuth(options =>
{
    options.SecretKey = "expedition-secret-key-at-least-32-characters!";
    options.Issuer = "expedition-server";
});

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

// Config download endpoint — serves serialized config bytes
app.MapGet("/meta/{configName}/{major:int}/{minor:int}", (string configName, int major, int minor, IMetaSerializer ser) =>
{
    var version = new MetaConfigVersion(major, minor);
    var bytes = configName switch {
        "ExpeditionConfig" => configProvider.GetDownloadData(version, serializer),
        "PlayerConfig" => playerConfigProvider.GetDownloadData(version, serializer),
        _ => throw new Exception($"Unknown config {configName}")
    };

    return Results.Bytes(bytes, "application/octet-stream");
});

// Stored desync reports for one player — the triage half of the workflow. Clients send a follow-up
// report on every mismatch they detect (gated by MetaTransportOptions.DesyncReportingEnabled), the
// server diffs it against its own patch and keeps the last 50 per player. This is what you would
// actually read after flagging a player: the per-field divergence, not just "a CRC differed".
// Unauthenticated here because it is an example; a real deployment puts this behind admin auth.
app.MapGet("/meta/desync/{playerId}", async (string playerId, IGrainFactory grains) =>
{
    var reports = await grains.GetGrain<IDesyncReportGrain>(playerId).GetRecentAsync(20);
    return Results.Json(reports);
});

// Turn the analysis on or off for one player without touching the client — the admin half of
// DeepDesyncMode.PerPlayer. Takes effect on that player's next session.
app.MapPost("/meta/desync/{playerId}/{enabled:bool}", async (string playerId, bool enabled, IGrainFactory grains) =>
{
    await grains.GetGrain<IDesyncReportGrain>(playerId).SetAnalysisEnabledAsync(enabled);
    return Results.Ok(new { playerId, analysisEnabled = enabled, appliesOn = "next session" });
});

#if USE_HTTP_POLLING
app.MapMetaHttpPolling("/meta");
app.MapGet("/", () => "Expedition Server (HTTP Polling) is running");

app.Logger.LogInformation("=== Expedition Server (HTTP Polling) ===");
app.Logger.LogInformation("Listening on http://localhost:{Port}", port);
app.Logger.LogInformation("HTTP Polling: /meta");
if (useServerPatch)
    app.Logger.LogInformation("ServerPatch mode ENABLED for Move and RemoveObstacle");
#else
app.MapHub<MetaHub>("/meta");
app.MapGet("/", () => "Expedition Server is running");

app.Logger.LogInformation("=== Expedition Server ===");
app.Logger.LogInformation("Listening on http://localhost:{Port}", port);
app.Logger.LogInformation("SignalR Hub: /meta");
if (useServerPatch)
    app.Logger.LogInformation("ServerPatch mode ENABLED for Move and RemoveObstacle");
#endif

await app.RunAsync();



