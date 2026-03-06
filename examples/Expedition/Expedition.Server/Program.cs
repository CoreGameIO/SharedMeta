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
            services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(10));

            // ServerPatch mode: override CrossOptimistic methods to use server-side patching
            if (useServerPatch)
            {
                var modeProvider = new ExecutionModeProvider();
                modeProvider.SetMode("IExpeditionService", "Move", ExecutionMode.ServerPatch);
                modeProvider.SetMode("IExpeditionService", "RemoveObstacle", ExecutionMode.ServerPatch);
                services.AddSingleton<IExecutionModeProvider>(modeProvider);
            }

            services.ConfigureMeta(svc =>
            {
                svc.AddTransient<IRandomService, RandomServiceImpl>();
                svc.AddSingleton<IMetaConfigProvider<ExpeditionConfig>, ExpeditionConfigProvider>();
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
// SignalR with MessagePack binary protocol
builder.Services.AddSignalR(hubOptions =>
{
    if (builder.Environment.IsDevelopment())
    {
        hubOptions.EnableDetailedErrors = true;
        hubOptions.ClientTimeoutInterval = TimeSpan.FromMinutes(30);
        hubOptions.KeepAliveInterval = TimeSpan.FromMinutes(15);
    }
}).AddMetaMessagePackProtocol();
#endif

// MetaConnectionHandler factory
builder.Services.AddSingleton<IMetaConnectionHandlerFactory>(sp =>
{
    var grainFactory = sp.GetRequiredService<IGrainFactory>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    return new MetaConnectionHandlerFactory(grainFactory, loggerFactory);
});

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

/// <summary>
/// Provides default expedition config. Can be extended to read from DB, per-entity overrides, etc.
/// </summary>
public class ExpeditionConfigProvider : IMetaConfigProvider<ExpeditionConfig>
{
    public ExpeditionConfig GetConfig(string entityId) => new();
}
