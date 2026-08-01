using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Client;
using SharedMeta.Serialization.MemoryPack;
using CardGame.Shared;
using CardGame.Shared.Client;
#if USE_HTTP_POLLING
using SharedMeta.Transport.HttpPolling;
#else
using SharedMeta.Transport.SignalR;
#endif

namespace CardGame.Client
{
    /// <summary>
    /// Example production client setup using MetaClient.
    /// Shows how little code is needed with the framework.
    /// </summary>
    public class ProductionClientSetup : IAsyncDisposable
    {
        private readonly MetaClient _client;

        public MetaClient Client => _client;
        public string PlayerId => _client.PlayerId;
        public string ServerUrl { get; }
        public string ConnectionId => _client.ConnectionId;

        public ProductionClientSetup(string serverUrl, string? playerId = null)
        {
            ServerUrl = serverUrl;

            _client = new MetaClient(
#if USE_HTTP_POLLING
                new HttpPollingConnection(new HttpPollingConnectionOptions { ServerUrl = serverUrl }),
#else
                new SignalRConnection(serverUrl),
#endif
                new MemoryPackMetaSerializer(),
                new MetaClientOptions
                {
                    PlayerId = playerId,
                    Diagnostics = new ConsoleDesyncDiagnostics(),
                    ClientSignature = CardGame.Shared.GameServiceDiscoveryBase.ClientSignature,
                }
            );

            _client.Resolver.RegisterAllServices();
        }

        public async Task ConnectAsync()
        {
            Console.WriteLine($"Connecting to {ServerUrl}...");
            await _client.ConnectAsync();
            Console.WriteLine($"Connected! PlayerId: {PlayerId}, ConnectionId: {ConnectionId}");
        }

        /// <summary>
        /// Create a MetaServiceResolver for this client.
        /// Provided for backward compatibility - prefer using MetaClient.Resolver directly.
        /// </summary>
        public MetaServiceResolver CreateResolver(IExecutionModeProvider? modeProvider = null)
        {
            return (MetaServiceResolver)_client.Resolver;
        }

        private class ConsoleDesyncDiagnostics : IDesyncDiagnostics
        {
            public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
            {
                Console.Error.WriteLine($"[DESYNC] {serviceName}.{methodName}: server={serverResult}, local={localResult}");
            }

            public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes)
            {
                Console.Error.WriteLine($"[CROSS-ENTITY] {entityId} methodId={methodId}: {resultBytes?.Length ?? 0} bytes");
            }

            public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta)
            {
                Console.Error.WriteLine($"[RANDOM DESYNC] {serviceName}.{methodName}: serverDelta={serverDelta}, localDelta={localDelta}");
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
}
