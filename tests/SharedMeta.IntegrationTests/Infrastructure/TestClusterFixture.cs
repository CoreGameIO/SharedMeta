using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MemoryPack;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
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
/// Wires the global client-side <see cref="MetaLog"/> to a console logger at Error level
/// before any test runs. Default <c>NullMetaLogger</c> silently drops every Error write —
/// in production that's fine (the host injects its own logger), but in tests it caused
/// dispatcher/replay deserialization failures to disappear without trace until something
/// asserted on the side-effect (e.g. <c>Sum == 0</c> while expecting 50). xUnit captures
/// <see cref="Console"/> output per test, so any Error written here surfaces in the
/// failing test's output, not as a silent zero.
/// </summary>
internal static class GlobalTestLoggerInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        MetaLog.SetLogger(new ConsoleMetaLogger(MetaLogLevel.Error));
    }
}

/// <summary>
/// Test cluster fixture that sets up an in-memory Orleans cluster
/// with all meta services properly configured.
/// </summary>
public class TestClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public IGrainFactory GrainFactory => Cluster.GrainFactory;
    public IMetaSerializer Serializer { get; } = new MemoryPackMetaSerializer();

    // Real ILoggerFactory feeding the InProcess transport handler. Mirrors the silo's
    // Error-level Console wiring so MetaConnectionHandler diagnostics (rejected RPCs,
    // signature negotiation failures) surface in the test console alongside grain-side errors.
    public ILoggerFactory LoggerFactory { get; } = Microsoft.Extensions.Logging.LoggerFactory.Create(
        b => b.AddSimpleConsole(opt => { opt.SingleLine = true; opt.IncludeScopes = false; })
              .SetMinimumLevel(LogLevel.Error));

    /// <summary>
    /// Shared ExecutionModeProvider registered in the silo.
    /// Tests can set/clear overrides to control execution modes at runtime.
    /// </summary>
    public ExecutionModeProvider ExecutionModeProvider => SiloConfigurator.SharedModeProvider;

    public async Task InitializeAsync()
    {
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
    public IMetaConnectionHandlerFactory CreateHandlerFactory(
        MetaTransportOptions? transportOptions = null,
        IPlayerIdentityValidator? identityValidator = null)
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
            LoggerFactory,
            transportOptions,
            transportOptions != null ? Serializer : null,
            schemaRegistry: null,
            versionPolicy: null,
            signatureRegistry: sigRegistry,
            serverSignature: serverSignature,
            identityValidator: identityValidator);
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
                // Surface Error-level diagnostics from server-side framework code (EntityGrain,
                // MetaProviderBase, SessionManagerGrain) to the test console. Default DI logging
                // returns NullLogger, which silently dropped every ProviderCallError — that's
                // how the signal/replay arg-encoding bugs hid until they tripped a value assert.
                .ConfigureLogging(logging => logging
                    .ClearProviders()
                    .AddSimpleConsole(opt => { opt.SingleLine = true; opt.IncludeScopes = false; })
                    .SetMinimumLevel(LogLevel.Error))
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
