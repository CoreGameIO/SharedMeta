using SharedMeta.Core;
using SharedMeta.Core.Network;
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
    public string PlayerId => _client.PlayerId;
    public string ConnectionId => _client.ConnectionId;

    /// <summary>
    /// Issues detected during test execution (desyncs, errors, etc.)
    /// </summary>
    public IReadOnlyList<string> DetectedIssues => _issues;

    public TestClientSetup(InProcessServer server, string? playerId = null)
    {
        var diagnostics = new TestDesyncDiagnostics(_issues);

        _client = new MetaClient(
            server.CreateConnection(),
            new SharedMeta.Serialization.MemoryPack.MemoryPackMetaSerializer(),
            new MetaClientOptions
            {
                PlayerId = playerId,
                Diagnostics = diagnostics
            }
        );

        // Register services using generated aggregate method
        _client.Resolver.RegisterAllServices();

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

        public void OnCrossEntityResult(string entityId, string serviceName, string methodName, byte[]? resultBytes)
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
