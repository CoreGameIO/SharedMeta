using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.Net;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using SharedMeta.Core;
using SharedMeta.Inspector;
using SharedMeta.Inspector.Nats;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;
using SharedMeta.Test.Server;
using Xunit;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Runs only where a NATS server with JetStream answers (SHAREDMETA_TEST_NATS, default
/// nats://127.0.0.1:4222 — e.g. <c>docker run -p 127.0.0.1:4222:4222 nats:2 -js</c>); skipped otherwise.
/// </summary>
public sealed class NatsFactAttribute : FactAttribute
{
    public NatsFactAttribute()
    {
        if (!NatsInspectorClusterFixture.NatsAvailable)
            Skip = $"No NATS server at {NatsInspectorClusterFixture.NatsUrl} ({NatsInspectorClusterFixture.ProbeFailure}); set SHAREDMETA_TEST_NATS or run nats -js.";
    }
}

/// <summary>
/// Two silos, each with its own inspector on one NATS stream, plus standalone inspectors built
/// like the inspector service. Stream, subjects and bucket are unique per run.
/// </summary>
public sealed class NatsInspectorClusterFixture : IAsyncLifetime
{
    // 127.0.0.1, not localhost: with WSL mirrored networking a published port can hang on ::1.
    public static readonly string NatsUrl = Environment.GetEnvironmentVariable("SHAREDMETA_TEST_NATS") ?? "nats://127.0.0.1:4222";
    internal static string? ProbeFailure;
    public static readonly bool NatsAvailable = Probe(NatsUrl);

    private static readonly string s_run = Guid.NewGuid().ToString("N").Substring(0, 8);
    internal static readonly string StreamName = "SM_INSPECTOR_TEST_" + s_run;
    internal static readonly string SubjectPrefix = "smtest." + s_run;
    internal static readonly string Bucket = "sm-inspector-test-" + s_run;

    private readonly List<IHost> _standalones = new();

    public TestCluster Cluster { get; private set; } = null!;
    public IMetaSerializer Serializer { get; } = new MemoryPackMetaSerializer();
    public IReadOnlyList<InspectorRuntime> SiloRuntimes { get; private set; } = Array.Empty<InspectorRuntime>();

    /// <summary>
    /// Each silo's own grain factory. Placement here is prefer-local, so a grain first called
    /// through silo N's factory is activated on silo N — the cluster client sticks to one gateway.
    /// </summary>
    public IReadOnlyList<IGrainFactory> SiloGrainFactories { get; private set; } = Array.Empty<IGrainFactory>();

    public async Task InitializeAsync()
    {
        if (!NatsAvailable) return;
        var builder = new TestClusterBuilder { Options = { InitialSilosCount = 2 } };
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await Cluster.DeployAsync().WaitAsync(cts.Token);

        var services = Cluster.Silos.Select(s => ((InProcessSiloHandle)s).SiloHost.Services).ToList();
        SiloRuntimes = services.Select(sp => sp.GetRequiredService<InspectorRuntime>()).ToList();
        SiloGrainFactories = services.Select(sp => sp.GetRequiredService<IGrainFactory>()).ToList();
        foreach (var runtime in SiloRuntimes) await WaitConnectedAsync(runtime);
    }

    /// <summary>A standalone inspector (no silo), as the inspector service runs it.</summary>
    public async Task<InspectorRuntime> StartStandaloneAsync(TimeSpan warmStart)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSharedMetaInspector();
        builder.Services.AddSharedMetaInspectorNats(o =>
        {
            Configure(o);
            o.Publish = false;
            o.ManageStream = false;
            o.WarmStart = warmStart;
        });
        var host = builder.Build();
        await host.StartAsync();
        _standalones.Add(host);
        var runtime = host.Services.GetRequiredService<InspectorRuntime>();
        await WaitConnectedAsync(runtime);
        return runtime;
    }

    /// <summary>Consumers currently on the test stream (each live inspector holds one).</summary>
    public async Task<int> ConsumerCountAsync()
    {
        await using var client = new NatsClient(NatsUrl);
        int count = 0;
        await foreach (var _ in client.CreateJetStreamContext().ListConsumerNamesAsync(StreamName)) count++;
        return count;
    }

    public async Task DisposeAsync()
    {
        if (!NatsAvailable) return;
        foreach (var host in _standalones)
        {
            await host.StopAsync();
            host.Dispose();
        }
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await Cluster.StopAllSilosAsync().WaitAsync(cts.Token);

        await using var client = new NatsClient(NatsUrl);
        var js = client.CreateJetStreamContext();
        try { await js.DeleteStreamAsync(StreamName); } catch (NatsJSApiException) { }
        try { await client.CreateKeyValueStoreContext().DeleteStoreAsync(Bucket); } catch (NatsJSApiException) { }
    }

    internal static void Configure(NatsInspectorOptions o)
    {
        o.Url = NatsUrl;
        o.StreamName = StreamName;
        o.SubjectPrefix = SubjectPrefix;
        o.ControlBucket = Bucket;
        o.MaxAge = TimeSpan.FromHours(1);
    }

    private static async Task WaitConnectedAsync(InspectorRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (runtime.Distribution == null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Inspector did not connect to NATS.");
            await Task.Delay(100);
        }
    }

    private static bool Probe(string url)
    {
        try
        {
            var uri = new Uri(url.Split(',')[0]);
            using var tcp = new TcpClient();
            if (tcp.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 4222).Wait(TimeSpan.FromSeconds(2)) && tcp.Connected) return true;
            ProbeFailure = "connect timed out";
            return false;
        }
        catch (Exception ex)
        {
            ProbeFailure = (ex.InnerException ?? ex).Message;
            return false;
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddMemoryGrainStorage("Default")
                .AddMemoryGrainStorage(EntitySubscriptionStorage.ProviderName)
                .ConfigureLogging(logging => logging.ClearProviders().AddSimpleConsole().SetMinimumLevel(LogLevel.Error))
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
                    services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(5));
                    // Tests choose the silo of an entity by the factory they first call it through;
                    // the default (resource-optimized) placement only prefers the caller under even load.
                    services.AddSingleton<PlacementStrategy, PreferLocalPlacement>();
                    // One runtime per silo, as in separate processes.
                    services.AddSharedMetaInspector();
                    services.AddSharedMetaInspectorNats(o =>
                    {
                        NatsInspectorClusterFixture.Configure(o);
                        o.WarmStart = TimeSpan.Zero;
                    });
                    services.ConfigureTestMeta();
                });
        }
    }
}

[CollectionDefinition(Name)]
public class NatsInspectorClusterCollection : ICollectionFixture<NatsInspectorClusterFixture>
{
    public const string Name = "NatsInspectorCluster";
}
