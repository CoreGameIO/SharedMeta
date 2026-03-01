using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta1;
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
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();

        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await Cluster.StopAllSilosAsync();
    }

    /// <summary>
    /// Create a MetaConnectionHandlerFactory that can be used with InProcessServer.
    /// </summary>
    public IMetaConnectionHandlerFactory CreateHandlerFactory()
    {
        return new MetaConnectionHandlerFactory(
            GrainFactory,
            NullLoggerFactory.Instance,
            MetaMethodSignatureValidator.ValidateClientSignatures);
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
                .ConfigureServices(services =>
                {
                    // Register serializer
                    services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());

                    // Entity grain options
                    services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(5));

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
