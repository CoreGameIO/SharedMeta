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
using SharedMeta.Auth;
using Expedition.Shared;
using Expedition.Shared.Server;
using Serilog;

// Port configuration: pass as first arg, e.g. `dotnet run -- 5000`
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5000;
var siloPort = 11111 + (port - 5000);
var gatewayPort = 30000 + (port - 5000);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");

builder.Host.UseSerilog((ctx, config) => config
    .WriteTo.Console());

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
        .AddFileGrainStorage("Default", o => o.RootDirectory = "./data")
        .ConfigureServices(services =>
        {
            services.AddSingleton<IMetaSerializer>(serializer);
            services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(10));

            services.ConfigureMeta(svc =>
            {
                svc.AddTransient<ILobbyRequester>(sp => new OrleansLobbyRequester(sp.GetRequiredService<IGrainFactory>()));
                svc.AddTransient<IRandomService, RandomServiceImpl>();
                svc.AddSingleton<IMetaConfigProvider<ExpeditionConfig>>(new DefaultExpeditionConfigProvider());
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

// MetaConnectionHandler factory
builder.Services.AddSingleton<IMetaConnectionHandlerFactory>(sp =>
{
    var grainFactory = sp.GetRequiredService<IGrainFactory>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    return new MetaConnectionHandlerFactory(grainFactory, loggerFactory);
});

// Authentication
builder.Services.AddMetaAuth(options =>
{
    options.SecretKey = "expedition-secret-key-at-least-32-characters!!";
    options.Issuer = "expedition-server";
});
builder.Services.AddSingleton(new MetaTransportOptions { RequireAuthentication = true });

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
app.MapGet("/", () => "Expedition.Server is running");

// Config download endpoint
app.MapGet("/meta/config/{major:int}/{minor:int}", (int major, int minor, IMetaSerializer ser, IMetaConfigProvider<ExpeditionConfig> provider) =>
{
    var config = provider.GetConfig(new MetaConfigVersion(major, minor));
    return Results.Bytes(ser.Pack(config), "application/octet-stream");
});

app.Logger.LogInformation("=== Expedition.Server ===");
app.Logger.LogInformation("Listening on http://localhost:{Port}", port);

await app.RunAsync();

public class DefaultExpeditionConfigProvider : IMetaConfigProvider<ExpeditionConfig>
{
    public MetaConfigVersion CurrentVersion => new(1, 1);
    public ExpeditionConfig GetConfig(MetaConfigVersion version) => new();
    public string? GetDownloadUrl(MetaConfigVersion version) => null;
}
