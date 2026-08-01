using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Network;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta2.Generic.Client;
using SharedMeta.Test.Meta2.Generic.Server;
using Xunit;

namespace SharedMeta.GenericSerializerTests;

/// <summary>
/// Cluster for the generic-codegen assembly. Deliberately minimal and deliberately separate from
/// <c>SharedMeta.IntegrationTests</c>: method ids are sequential per assembly and a session carries
/// exactly one ServerSignature, so a second meta assembly cannot share that cluster.
/// </summary>
/// <remarks>
/// The serializer here is still <see cref="MemoryPackMetaSerializer"/> — the generic branch is a
/// codegen choice, not a codec choice. What differs is that every payload goes through
/// <c>IPayloadWriter</c>/<c>IPayloadReader</c> framing instead of MemoryPack's positional layout.
/// </remarks>
public class GenericClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public IMetaSerializer Serializer { get; } = new MemoryPackMetaSerializer();

    public ILoggerFactory LoggerFactory { get; } = Microsoft.Extensions.Logging.LoggerFactory.Create(
        b => b.AddSimpleConsole(opt => { opt.SingleLine = true; opt.IncludeScopes = false; })
              .SetMinimumLevel(LogLevel.Error));

    public async Task InitializeAsync()
    {
        // Without this every framework-side Error write goes to NullMetaLogger, which is how a
        // dispatch failure turns into a bare wrong value instead of a diagnosable message.
        MetaLog.SetLogger(new ConsoleMetaLogger(MetaLogLevel.Error));

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
        var serverSignature = SharedMeta.Test.Meta2.Generic.GameServiceDiscoveryBase.ServerSignature;
        return new MetaConnectionHandlerFactory(
            Cluster.GrainFactory,
            new GeneratedEntityGrainResolver(),
            LoggerFactory,
            transportOptions: null,
            serializer: null,
            schemaRegistry: null,
            versionPolicy: null,
            signatureRegistry: new ClientSignatureRegistry(Cluster.GrainFactory, serverSignature),
            serverSignature: serverSignature);
    }

    public IMetaServerApiFactory ServerApiFactory => new MetaServerApiFactory(Cluster.GrainFactory, Serializer);

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddMemoryGrainStorage("Default")
                .ConfigureLogging(logging => logging
                    .ClearProviders()
                    .AddSimpleConsole(opt => { opt.SingleLine = true; opt.IncludeScopes = false; })
                    .SetMinimumLevel(LogLevel.Error))
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
                    services.Configure<SessionManagerOptions>(o => o.EnforceRpcOrder = true);
                    services.ConfigureMeta();
                });
        }
    }
}

[CollectionDefinition(Name)]
public class GenericClusterCollection : ICollectionFixture<GenericClusterFixture>
{
    public const string Name = "GenericCluster";
}

/// <summary>Client bound to the generic-codegen assembly's signature.</summary>
public sealed class GenericTestClient : IAsyncDisposable
{
    private readonly MetaClient _client;
    private readonly List<string> _issues = new();

    public IReadOnlyList<string> DetectedIssues => _issues;
    public IConnection Connection => _client.Connection;
    public IMetaSerializer Serializer => _client.Serializer;
    public MetaServiceResolver Resolver => (MetaServiceResolver)_client.Resolver;

    public GenericTestClient(InProcessServer server, string? playerId = null)
    {
        _client = new MetaClient(
            server.CreateConnection(),
            new MemoryPackMetaSerializer(),
            new MetaClientOptions
            {
                PlayerId = playerId,
                Diagnostics = new CollectingDiagnostics(_issues),
                ClientAppVersion = "1.0.0",
                ClientSignature = SharedMeta.Test.Meta2.Generic.GameServiceDiscoveryBase.ClientSignature,
            });

        _client.Resolver.RegisterAllServices();
        _client.Dispatcher.ImmediateMode = true;
    }

    public Task ConnectAsync() => _client.ConnectAsync();

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private sealed class CollectingDiagnostics : IDesyncDiagnostics
    {
        private readonly List<string> _issues;
        public CollectingDiagnostics(List<string> issues) => _issues = issues;

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
        {
            var msg = $"[DESYNC] {serviceName}.{methodName}: server={serverResult}, local={localResult}";
            _issues.Add(msg);
            Console.Error.WriteLine(msg);
        }

        public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes) { }

        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta)
        {
            var msg = $"[RANDOM DESYNC] {serviceName}.{methodName}: {serverDelta} vs {localDelta}";
            _issues.Add(msg);
            Console.Error.WriteLine(msg);
        }

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
