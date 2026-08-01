using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using SharedMeta.Core;
using SharedMeta.Core.Framework;
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
using SharedMeta.Server.Core.Storage;
using CardGame.Shared;
using CardGame.Shared.Server;

using OpenTelemetry.Logs;
using Serilog;

// Port configuration: pass as first arg, e.g. `dotnet run -- 5000`
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5000;
var siloPort = 11111 + (port - 5000);
var gatewayPort = 30000 + (port - 5000);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");

// // --- Alternative: OpenTelemetry ---
// builder.Logging.ClearProviders();
// builder.Logging.AddOpenTelemetry(otel =>
// {
//     otel.AddConsoleExporter();
//     otel.IncludeFormattedMessage = true;
// });

// // --- Alternative: Serilog ---
// // Uncomment and add Serilog.AspNetCore package:
// // <PackageReference Include="Serilog.AspNetCore" Version="9.0.0" />
builder.Host.UseSerilog((ctx, config) => config
    .WriteTo.Console()
    .WriteTo.File("logs/server-.log", rollingInterval: RollingInterval.Day));

// Serializer
var serializer = new MemoryPackMetaSerializer();
builder.Services.AddSingleton<IMetaSerializer>(serializer);

// Orleans Silo Configuration
builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering(siloPort, gatewayPort)
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "cg1-cluster";
            options.ServiceId = "cg1-server";
        })
        // File-based persistent storage for grain state
        .AddFileGrainStorage("Default", o => o.RootDirectory = "./data")
        .ConfigureServices(services =>
        {
            // Serializer available to grains via DI
            services.AddSingleton<IMetaSerializer>(serializer);

            // Entity grain options
            services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(10));

            // Configure meta services using generated code
            // This registers all IMetaProviderFactory<T> implementations
            services.ConfigureMeta(svc =>
            {
                // Register server-side services (IRandomService, etc.)
                svc.AddTransient<IRandomService, RandomServiceImpl>();

                // Register ILobbyRequester for ProfileService to use
                // This gets called when ProfileService.LobbyRequester is accessed
                svc.AddTransient<ILobbyRequester>(sp =>
                    new OrleansLobbyRequester(sp.GetRequiredService<IGrainFactory>()));
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
// SignalR configuration with MessagePack binary protocol
builder.Services.AddSignalR(hubOptions => {
    if (builder.Environment.IsDevelopment()) {
        hubOptions.EnableDetailedErrors = true;
        hubOptions.ClientTimeoutInterval = TimeSpan.FromMinutes(30);
        hubOptions.KeepAliveInterval = TimeSpan.FromMinutes(15);
    }
}).AddMetaMessagePackProtocol();
#endif

// MetaConnectionHandlerFactory is registered by the generated ConfigureMeta() inside the
// silo block — it wires MetaServerSignature + IClientSignatureRegistry + ClientVersionPolicy.
// Do NOT re-register with the bare 3-arg ctor here: that drops signatureRegistry to null,
// and MetaConnectionHandler then rejects every RPC with "client signature not negotiated"
// once the client passes MetaClientOptions.ClientSignature.

// CORS for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();

#if USE_HTTP_POLLING
// Map HTTP polling endpoints
app.MapMetaHttpPolling("/meta");

// Health check endpoint
app.MapGet("/", () => "CardGame Server (Orleans + HTTP Polling) is running");

app.Logger.LogInformation("=== CardGame Server (Orleans + HTTP Polling) ===");
app.Logger.LogInformation("Listening on http://localhost:{Port}", port);
app.Logger.LogInformation("HTTP Polling: /meta");
#else
// Map SignalR hub
app.MapHub<MetaHub>("/meta");

// Health check endpoint
app.MapGet("/", () => "TheFool Server (Orleans) is running");

app.Logger.LogInformation("=== TheFool Server (Orleans) ===");
app.Logger.LogInformation("Listening on http://localhost:{Port}", port);
app.Logger.LogInformation("SignalR Hub: /meta");
#endif

await app.RunAsync();
