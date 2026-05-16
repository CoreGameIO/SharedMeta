using ClanWars.Server;
using ClanWars.Shared;
using ClanWars.Shared.Server;  // generated MetaServiceCollectionExtensions.ConfigureMeta
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using SharedMeta.Core;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Transport.SignalR;
using SharedMeta.Debug.Mux;

// Port can be passed as first arg (default 5050). Silo + gateway shift in lockstep.
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5050;
var siloPort = 11111 + (port - 5050);
var gatewayPort = 30000 + (port - 5050);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");

var serializer = new MemoryPackMetaSerializer();
builder.Services.AddSingleton<IMetaSerializer>(serializer);

// Single shared provider — registered on the silo via ConfigureMeta below.
var configProvider = new ClanConfigProvider();
builder.Services.AddSingleton<IMetaConfigProvider<ClanConfig>>(configProvider);

// Cluster-wide client/server version policy. Both 1.0 and 2.0 clients accepted —
// per-client config branch decided per [MetaConfigVersion] rules on ClanConfig.
builder.Services.AddSingleton(new MetaTransportOptions
{
    ServerVersion    = "2.0.0",
    MinClientVersion = "1.0.0",
    MaxClientVersion = "2.x.*",
});

// Per-silo capability registry (signature → ClientCapabilities cache).
builder.Services.AddSharedMetaClientSignatureRegistry();
// Server signature — hand-built so the boundary on ClanConfig is wired into the
// capability compute even before auto-emit on the concrete GameServiceDiscovery lands.
// Lists every IClanService method bound to ClanConfig + the [MetaConfigStructureBoundary("2.0")]
// declaration. Methods array is intentionally per-service-per-config (no per-method
// MinCompatibleVersion in this example — boundary is what's demonstrated).
var serverSignature = new SharedMeta.Server.Core.Session.MetaServerSignature
{
    Methods = new List<SharedMeta.Server.Core.Session.ServerMethodEntry>
    {
        new() { ServiceName = "IClanService", ConfigTypeFullName = typeof(ClanConfig).FullName! },
        new() { ServiceName = "IProfileService", ConfigTypeFullName = typeof(ClanConfig).FullName! },
    },
    ConfigBoundaries = new List<SharedMeta.Server.Core.Session.ConfigBoundaryEntry>
    {
        new()
        {
            ConfigTypeFullName = typeof(ClanConfig).FullName!,
            MinConfigVersion = "2.0",
            Reason = "Friendship + officer mechanics — v1 method bodies cannot reason about these fields.",
        },
    },
};
builder.Services.AddSingleton(serverSignature);

// ClanContainerService is a per-silo singleton that fronts the cluster-wide
// IClanContainerGrain. Hosted-service lifecycle handles hydration + periodic flush.
builder.Services.AddSingleton<ClanContainerService>();
builder.Services.AddSingleton<IClanContainerService>(sp => sp.GetRequiredService<ClanContainerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClanContainerService>());

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering(siloPort, gatewayPort)
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "clanwars-cluster";
            options.ServiceId = "clanwars-server";
        })
        .AddMemoryGrainStorage("Default")
        .ConfigureServices(services =>
        {
            services.AddSingleton<IMetaSerializer>(serializer);
            services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(10));

            // Make the same registry visible inside the silo (so MetaConnectionHandler
            // can pick it up via DI when constructing per-connection handlers).
            services.AddSharedMetaClientSignatureRegistry();
            services.AddSingleton(serverSignature);

            // ConfigureMeta wires the generated provider factories.
            services.ConfigureMeta(svc =>
            {
                svc.AddSingleton<IMetaConfigProvider<ClanConfig>>(configProvider);
                // ClanService accesses this via Context.ResolveService<IClanContainerService>().
                svc.AddSingleton<IClanContainerService>(sp =>
                    sp.GetRequiredService<ClanContainerService>());
            });
        });
});

// SignalR wire — game grade timeouts.
builder.Services.AddSignalR(o =>
{
    o.EnableDetailedErrors = true;
    o.ClientTimeoutInterval = TimeSpan.FromMinutes(30);
    o.KeepAliveInterval = TimeSpan.FromMinutes(15);
}).AddMetaMessagePackProtocol();

builder.Services.AddSingleton<IMetaConnectionHandlerFactory>(sp =>
{
    var grainFactory = sp.GetRequiredService<IGrainFactory>();
    var loggerFactory = sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
    var entityGrainResolver = sp.GetRequiredService<IEntityGrainResolver>();
    var transportOptions = sp.GetRequiredService<MetaTransportOptions>();
    var signatureRegistry = sp.GetService<IClientSignatureRegistry>();
    return new MetaConnectionHandlerFactory(
        grainFactory, entityGrainResolver, loggerFactory,
        signatureValidator: null,
        transportOptions: transportOptions,
        serializer: serializer,
        signatureRegistry: signatureRegistry);
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.MapHub<MetaHub>("/meta");
// Mux endpoint — one SignalR socket carries N logical client sessions for stress tests.
// Production / regular clients keep using /meta; load tests connect to /meta-mux.
app.MapMetaMuxHub("/meta-mux");

Console.WriteLine($"[ClanWars] Server listening on http://localhost:{port}");
Console.WriteLine($"           Orleans silo: {siloPort}, gateway: {gatewayPort}");
Console.WriteLine($"           Hubs: /meta (regular), /meta-mux (multiplexed stress-test transport)");
Console.WriteLine($"           ConfigBoundary: ClanConfig 2.0 (v1 clients force-ServerPatch on v2-pinned clans)");

await app.RunAsync();
