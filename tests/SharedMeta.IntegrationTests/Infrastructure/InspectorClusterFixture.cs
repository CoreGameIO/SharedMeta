using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using SharedMeta.Core;
using SharedMeta.Inspector;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;
using SharedMeta.Server.Core.Telemetry;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta1.Server;
using SharedMeta.Test.Server;
using Xunit;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// A cluster of its own with the inspector registered as the call tap (the shared cluster's tap
/// is the recording test tap — a silo takes one).
/// </summary>
public class InspectorClusterFixture : IAsyncLifetime
{
    /// <summary>Created before the silo so the test and the silo share one runtime.</summary>
    internal static readonly InspectorRuntime SharedRuntime = new(new InspectorOptions
    {
        AllowInvoke = true,
        InvokeMethods = { "ICounterService.*" },
        RedactMembers = { "*secret*" },
    });

    public TestCluster Cluster { get; private set; } = null!;
    public IGrainFactory GrainFactory => Cluster.GrainFactory;
    public IMetaSerializer Serializer { get; } = new MemoryPackMetaSerializer();
    public InspectorRuntime Runtime => SharedRuntime;
    public InspectorService Service { get; } = new(SharedRuntime);

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await Cluster.DeployAsync().WaitAsync(cts.Token);
    }

    public async Task DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Cluster.StopAllSilosAsync().WaitAsync(cts.Token);
    }

    public IMetaConnectionHandlerFactory CreateHandlerFactory()
    {
        var serverSignature = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ServerSignature;
        return new MetaConnectionHandlerFactory(
            GrainFactory,
            new GeneratedEntityGrainResolver(),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            signatureRegistry: new ClientSignatureRegistry(GrainFactory, serverSignature),
            serverSignature: serverSignature,
            entitlements: new SharedMeta.Server.Core.Permissions.GrainPlayerEntitlements(GrainFactory),
            telemetry: new SharedMetaTelemetry(null, new[] { SharedRuntime.ServerObserver }, null));
    }

    /// <summary>Clears capture settings and waits for the encoder, so tests start from a known state.</summary>
    public async Task ResetAsync(Action<InspectorCaptureSettings>? configure = null)
    {
        await Runtime.FlushAsync(TimeSpan.FromSeconds(5));
        var settings = new InspectorCaptureSettings();
        configure?.Invoke(settings);
        Runtime.SetCaptureSettings(settings);
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
                    services.AddSharedMetaInspector(SharedRuntime);
                    services.ConfigureTestMeta();
                });
        }
    }
}

[CollectionDefinition(Name)]
public class InspectorClusterCollection : ICollectionFixture<InspectorClusterFixture>
{
    public const string Name = "InspectorCluster";
}
