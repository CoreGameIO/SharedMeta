using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MemoryPack;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Server;
using SharedMeta.Test.Server;
using Xunit;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Test cluster fixture that sets up an in-memory Orleans cluster
/// with all meta services properly configured.
/// </summary>
public class TestClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public IGrainFactory GrainFactory => Cluster.GrainFactory;
    public IMetaSerializer Serializer { get; } = new MemoryPackMetaSerializer();

    /// <summary>
    /// Shared ExecutionModeProvider registered in the silo.
    /// Tests can set/clear overrides to control execution modes at runtime.
    /// </summary>
    public ExecutionModeProvider ExecutionModeProvider => SiloConfigurator.SharedModeProvider;

    public async Task InitializeAsync()
    {
        // Opt the registry into per-slot stack-trace history so a double-release / use-after-free
        // throw includes the full Acquire/IncrementRef/Release chain. Tests-only — too expensive
        // for production.
        SharedMeta.Server.Core.Memory.PooledPayloadRegistry.EnableHistory = true;

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();

        Cluster = builder.Build();
        using var deployCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await Cluster.DeployAsync().WaitAsync(deployCts.Token);
    }

    public async Task DisposeAsync()
    {
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Cluster.StopAllSilosAsync().WaitAsync(stopCts.Token);
    }

    /// <summary>
    /// Create a MetaConnectionHandlerFactory that can be used with InProcessServer.
    /// Server-side RPC reordering is configured globally on the silo via
    /// <see cref="SiloConfigurator.Configure"/> (<c>SessionManagerOptions.EnforceRpcOrder = true</c>).
    /// </summary>
    public IMetaConnectionHandlerFactory CreateHandlerFactory(MetaTransportOptions? transportOptions = null)
    {
        // 0.24.0+ Construct IClientSignatureRegistry + MetaServerSignature outside the silo
        // DI graph so MetaConnectionHandler can translate client→server MethodId on every RPC.
        // (TestCluster's SiloHandle does not expose ServiceProvider in Orleans 8, so we recreate
        // the same wiring ConfigureMeta would have done.) The shared GameServiceDiscoveryBase
        // is the codegen singleton for the SharedMeta.Test.Meta1 assembly.
        var serverSignature = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ServerSignature;
        var sigRegistry = new SharedMeta.Server.Core.Session.ClientSignatureRegistry(GrainFactory, serverSignature);

        return new MetaConnectionHandlerFactory(
            GrainFactory,
            new GeneratedEntityGrainResolver(),
            NullLoggerFactory.Instance,
            MetaMethodSignatureValidator.ValidateClientSignatures,
            transportOptions,
            transportOptions != null ? Serializer : null,
            schemaRegistry: null,
            versionPolicy: null,
            signatureRegistry: sigRegistry,
            serverSignature: serverSignature);
    }

    /// <summary>
    /// Silo configurator that registers all meta services.
    /// </summary>
    private class SiloConfigurator : ISiloConfigurator
    {
        /// <summary>
        /// Static shared instance so tests can set/clear overrides.
        /// Safe because TestCluster is shared via xunit collection fixture (single instance).
        /// </summary>
        internal static readonly ExecutionModeProvider SharedModeProvider = new();

        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddMemoryGrainStorage("Default")
                .AddStartupTask<SharedMeta.Server.Core.Memory.PooledPayloadRegistryStartupTask>()
                .ConfigureServices(services =>
                {
                    // Register serializer
                    services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());

                    // Entity grain options. Force-enable deep desync globally so the
                    // DeepDesyncTests / DesyncReportingTests don't need a per-test toggle —
                    // the runtime opt-in logic is covered separately by the toggle in
                    // production code paths.
                    services.Configure<EntityGrainOptions>(o =>
                    {
                        o.SubscriberTtl = TimeSpan.FromMinutes(5);
                        o.DeepDesyncEnabled = true;
                    });

                    // Force-enable RPC reordering at the session manager so the in-process
                    // tests exercise the same path that production HTTP transports use.
                    // Stall thresholds are kept reasonable: heavy concurrent test runs can
                    // starve the threadpool for several seconds on .NET 10, so MaxStallDuration
                    // is generous. Notification timeouts are still small so stall-notification
                    // tests don't take long to assert.
                    services.Configure<SessionManagerOptions>(o =>
                    {
                        o.EnforceRpcOrder = true;
                        o.SoftStallNotifyTimeout = TimeSpan.FromMilliseconds(200);
                        o.HardStallNotifyTimeout = TimeSpan.FromMilliseconds(800);
                        o.MaxStallDuration = TimeSpan.FromSeconds(30);
                        o.StallTickInterval = TimeSpan.FromMilliseconds(100);
                    });

                    // Register execution mode provider (shared with tests)
                    services.AddSingleton<IExecutionModeProvider>(SharedModeProvider);

                    // Per-silo pool that backs broadcast payload buffers. Constructed with an
                    // unbound SiloId; PooledPayloadRegistryStartupTask (registered above) calls
                    // the cluster-singleton coordinator grain on silo startup and pins a unique
                    // id, so multi-silo Ref encodings cannot collide on slot-index interpretation.
                    services.AddSingleton<SharedMeta.Server.Core.Memory.PooledPayloadRegistry>(_ =>
                        new SharedMeta.Server.Core.Memory.PooledPayloadRegistry());

                    // Configure test meta services
                    services.ConfigureTestMeta();
                });
        }
    }
}

/// <summary>
/// Collection definition for sharing the test cluster across tests.
/// </summary>
[CollectionDefinition(Name)]
public class TestClusterCollection : ICollectionFixture<TestClusterFixture>
{
    public const string Name = "TestCluster";
}
