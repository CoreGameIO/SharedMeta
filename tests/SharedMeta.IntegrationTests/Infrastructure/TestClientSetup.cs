using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Transport;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Client;
using SharedMeta.Debug.InProcess;
using SharedMeta.Test.Meta1.Client;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Test client setup using in-process transport and MetaClient.
/// </summary>
public class TestClientSetup : IAsyncDisposable
{
    private readonly MetaClient _client;
    private readonly List<string> _issues = new();

    public IMetaSerializer Serializer => _client.Serializer;
    public IConnection Connection => _client.Connection;
    public string PlayerId => _client.PlayerId;
    public string ConnectionId => _client.ConnectionId;
    /// <summary>0.24.0+ Expose the underlying MetaClient so tests can inspect dispatcher
    /// state (e.g. <c>Dispatcher.Annotated</c> after the signature handshake).</summary>
    public MetaClient MetaClient => _client;

    /// <summary>
    /// Issues detected during test execution (desyncs, errors, etc.)
    /// </summary>
    public IReadOnlyList<string> DetectedIssues => _issues;

    public TestClientSetup(InProcessServer server, string? playerId = null,
        IDesyncDiagnostics? diagnostics = null,
        IExecutionModeProvider? modeProvider = null,
        string? clientAppVersion = null)
    {
        diagnostics ??= new TestDesyncDiagnostics(_issues);

        // 0.21.0: server-side ResolveForClient is strict — null/empty clientAppVersion
        // throws. Default test clients to "1.0.0" so every existing test (which doesn't
        // exercise per-client config branches) keeps working. Migration / per-scope tests
        // override this with explicit version strings.
        _client = new MetaClient(
            server.CreateConnection(),
            new SharedMeta.Serialization.MemoryPack.MemoryPackMetaSerializer(),
            new MetaClientOptions
            {
                PlayerId = playerId,
                Diagnostics = diagnostics,
                ModeProvider = modeProvider,
                ClientAppVersion = clientAppVersion ?? "1.0.0",
                // 0.24.0+ Wire the generated client signature so the session handshake
                // exercises the same negotiation path production clients use, and so the
                // server can produce per-method MethodId translation maps.
                ClientSignature = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ClientSignature
            }
        );

        // Register services using generated aggregate method
        _client.Resolver.RegisterAllServices();

        // 0.33.0+ CounterConfig moved from [MetaService(DefaultConfig=true)] (which
        // auto-registered a fallback StaticConfigProvider on both sides) to [ServiceConfig],
        // which has no auto-default by design. Register the client-side equivalent once,
        // centrally, so the ~16 CounterService-dependent test files don't each need to.
        _client.Resolver.RegisterConfigProvider(
            new SharedMeta.Client.StaticConfigProvider<SharedMeta.Test.Meta1.CounterConfig>(new SharedMeta.Test.Meta1.CounterConfig()));
        // Same migration for the EntityScope Shared/Global fixtures — these are
        // version-dependent (per [MetaConfigVersion] rules), so echo Major/Minor/Patch from
        // the resolved version, mirroring the server-side test providers exactly.
        _client.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<SharedMeta.Test.Meta1.SharedScopeConfig>(v => new SharedMeta.Test.Meta1.SharedScopeConfig
            {
                Major = v.Major,
                Minor = v.Minor,
                Patch = v.Patch,
            }));
        _client.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<SharedMeta.Test.Meta1.GlobalScopeConfig>(v => new SharedMeta.Test.Meta1.GlobalScopeConfig
            {
                Major = v.Major,
                Minor = v.Minor,
            }));
        // MigrationTest.cs's config: Query-mode only, so the client's own resolved value is
        // never observed by test assertions (server-authoritative) — a fixed instance is fine.
        _client.Resolver.RegisterConfigProvider(
            new SharedMeta.Client.StaticConfigProvider<SharedMeta.Test.Meta1.MigrationConfig>(new SharedMeta.Test.Meta1.MigrationConfig()));

        // In-process tests run single-threaded — process broadcasts immediately
        _client.Dispatcher.ImmediateMode = true;
    }

    public Task ConnectAsync() => _client.ConnectAsync();

    /// <summary>
    /// Create a MetaServiceResolver for this client.
    /// Provided for backward compatibility.
    /// </summary>
    public MetaServiceResolver CreateResolver(IExecutionModeProvider? modeProvider = null)
    {
        return (MetaServiceResolver)_client.Resolver;
    }

    private class TestDesyncDiagnostics : IDesyncDiagnostics
    {
        private readonly List<string> _issues;

        public TestDesyncDiagnostics(List<string> issues)
        {
            _issues = issues;
        }

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
        {
            var msg = $"[DESYNC] {serviceName}.{methodName}: server={serverResult}, local={localResult}";
            _issues.Add(msg);
            Console.Error.WriteLine(msg);
        }

        public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes)
        {
            // Log cross-entity results for debugging
        }

        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta)
        {
            var msg = $"[RANDOM DESYNC] {serviceName}.{methodName}: serverDelta={serverDelta}, localDelta={localDelta}";
            _issues.Add(msg);
            Console.Error.WriteLine(msg);
        }

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
        {
            return Task.FromResult(new StateComparisonResult { IsMatch = true });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }
}

/// <summary>
/// Shared client-side test config provider — materializes <typeparamref name="T"/> by
/// echoing the resolved <see cref="MetaConfigVersion"/> through a factory. Used for fixtures
/// whose config value is expected to mirror the resolved version (e.g. EntityScope Shared/Global
/// fixtures) rather than a fixed instance.
/// </summary>
public sealed class VersionEchoConfigProvider<T> : IClientMetaConfigProvider<T> where T : class
{
    private readonly Func<MetaConfigVersion, T> _factory;
    public VersionEchoConfigProvider(Func<MetaConfigVersion, T> factory) => _factory = factory;
    public Task<T> GetConfigAsync(MetaConfigVersion version) => Task.FromResult(_factory(version));
}
