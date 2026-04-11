using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Network;
using SharedMeta.Core.Random;

namespace SharedMeta.Client
{
    /// <summary>
    /// Default implementation of IMetaServiceResolver.
    /// Manages connections to entities and provides typed API clients.
    /// Uses INetwork-based architecture with dual-mode (Server/Optimistic) support.
    /// </summary>
    public class MetaServiceResolver : IMetaServiceResolver, ICrossEntityResolver, IDisposable
    {
        private readonly Func<string, string, Task<NetworkSubscribeResult>> _networkFactory;
        private readonly IMetaSerializer _serializer;
        private readonly IExecutionModeProvider _modeProvider;
        private readonly IDesyncDiagnostics? _diagnostics;
        private readonly Dictionary<Type, MetaServiceConfig> _serviceConfigs = new();
        private readonly Dictionary<string, MetaServiceConfig> _configsByStateTypeName = new();
        private readonly Dictionary<string, EntityConnection> _connections = new();
        private readonly object _lock = new();
        private List<CrossEntityLocalResult>? _recordedResults;

        /// <summary>
        /// Optional config cache for versioned configs.
        /// </summary>
        public IMetaConfigCache? ConfigCache { get; set; }

        /// <summary>
        /// Optional config downloader for fetching configs from server.
        /// </summary>
        public IMetaConfigDownloader? ConfigDownloader { get; set; }

        /// <summary>
        /// Factory to request config download URL from server.
        /// Parameters: (stateTypeName, version). Set by MetaClient to call IConnection.GetConfigDownloadUrlAsync.
        /// </summary>
        public Func<string, MetaConfigVersion, Task<string?>>? ConfigDownloadUrlFactory { get; set; }

        /// <summary>
        /// Creates a MetaServiceResolver.
        /// </summary>
        /// <param name="networkFactory">Factory that creates networks and returns initial state. Parameters: (entityId, stateTypeName). Returns: NetworkSubscribeResult</param>
        /// <param name="serializer">Serializer for RPC calls</param>
        /// <param name="modeProvider">Execution mode provider</param>
        /// <param name="diagnostics">Optional diagnostics handler</param>
        public MetaServiceResolver(
            Func<string, string, Task<NetworkSubscribeResult>> networkFactory,
            IMetaSerializer serializer,
            IExecutionModeProvider modeProvider,
            IDesyncDiagnostics? diagnostics = null)
        {
            _networkFactory = networkFactory;
            _serializer = serializer;
            _modeProvider = modeProvider;
            _diagnostics = diagnostics;
        }

        /// <summary>
        /// Register a service configuration.
        /// Called by generated code or during DI setup.
        /// </summary>
        public void RegisterService<TApiClient>(MetaServiceConfig config)
        {
            _serviceConfigs[typeof(TApiClient)] = config;
            var stateTypeName = config.StateType.FullName ?? config.StateType.Name;
            _configsByStateTypeName[stateTypeName] = config;
        }

        public async Task<TApiClient> GetServiceAsync<TApiClient>(string entityId) where TApiClient : class
        {
            var config = GetConfig<TApiClient>();
            EntityConnection? existingConnection = null;

            lock (_lock)
            {
                // Check if already connected
                if (_connections.TryGetValue(entityId, out existingConnection))
                {
                    // Check if this specific API client already exists
                    if (existingConnection.ApiClients.TryGetValue(typeof(TApiClient), out var existingClient))
                    {
                        return (TApiClient)existingClient;
                    }

                    // Verify state type matches (multiple services must use same state)
                    if (existingConnection.StateType != config.StateType)
                    {
                        throw new InvalidOperationException(
                            $"Cannot add service '{typeof(TApiClient).Name}' with state type '{config.StateType.Name}' " +
                            $"to entity '{entityId}' which uses state type '{existingConnection.StateType.Name}'");
                    }
                }
            }

            // Get or create network connection
            INetwork network;
            object state;
            MetaRandom? optimisticRandom = null;
            object? entityConfig = null;

            if (existingConnection != null)
            {
                // Reuse existing network, state, random, and config
                network = existingConnection.Network;
                state = existingConnection.State;
                optimisticRandom = existingConnection.OptimisticRandom;
                entityConfig = existingConnection.Config;
            }
            else
            {
                // Create new connection - pass stateTypeName so factory can call Subscribe
                var stateTypeName = config.StateType.FullName ?? config.StateType.Name;
                var subResult = await _networkFactory(entityId, stateTypeName);
                network = subResult.Network;

                // Deserialize state from server, or create empty if no state bytes
                if (subResult.StateBytes != null && subResult.StateBytes.Length > 0)
                {
                    state = _serializer.Unpack(config.StateType, subResult.StateBytes)
                        ?? throw new InvalidOperationException($"Failed to deserialize state of type '{config.StateType.Name}'");
                }
                else
                {
                    state = Activator.CreateInstance(config.StateType)
                        ?? throw new InvalidOperationException($"Failed to create state of type '{config.StateType.Name}'");
                }

                // Deserialize optimistic random, or create from entityId seed
                if (subResult.OptimisticRandomBytes != null && subResult.OptimisticRandomBytes.Length > 0)
                {
                    optimisticRandom = _serializer.Unpack<MetaRandom>(subResult.OptimisticRandomBytes);
                }
                else
                {
                    optimisticRandom = MetaRandom.FromString(entityId + ":optimistic");
                }

                // Resolve config: check cache → request URL → download → fallback to factory
                entityConfig = await ResolveConfigAsync(config, subResult, entityId);
            }

            // Create API client using factory
            var apiClient = (TApiClient)config.ApiClientFactory(
                network,
                _serializer,
                state,
                _modeProvider,
                _diagnostics,
                this,
                optimisticRandom,
                entityConfig);

            // Cache connection
            lock (_lock)
            {
                if (!_connections.TryGetValue(entityId, out var connection))
                {
                    connection = new EntityConnection
                    {
                        EntityId = entityId,
                        Network = network,
                        StateType = config.StateType,
                        State = state,
                        OptimisticRandom = optimisticRandom,
                        Config = entityConfig
                    };
                    _connections[entityId] = connection;
                }

                connection.ApiClients[typeof(TApiClient)] = apiClient;
            }

            return apiClient;
        }

        public Task DisconnectAsync(string entityId)
        {
            EntityConnection? connection;
            lock (_lock)
            {
                if (!_connections.TryGetValue(entityId, out connection))
                    return Task.CompletedTask;

                _connections.Remove(entityId);
            }

            // Dispose any disposable API clients
            foreach (var apiClient in connection.ApiClients.Values)
            {
                if (apiClient is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            return Task.CompletedTask;
        }

        public TState GetState<TState>(string entityId) where TState : class, ISharedState
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    return (TState)connection.State;
                }
            }

            throw new InvalidOperationException($"Not connected to entity '{entityId}'");
        }

        public TConfig? GetEntityConfig<TConfig>(string entityId) where TConfig : class
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    return connection.Config as TConfig;
                }
            }

            return null;
        }

        /// <summary>
        /// Get the config for a subscribed entity (untyped). ICrossEntityResolver implementation.
        /// </summary>
        object? ICrossEntityResolver.GetEntityConfig(string entityId)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                    return connection.Config;
            }
            return null;
        }

        /// <summary>
        /// Subscribe to a method being replayed from server broadcast.
        /// </summary>
        public IMethodSubscription OnMethodReplayed(string entityId, string serviceName, string methodName, Action<MethodReplayedContext> handler)
        {
            // For INetwork-based architecture, subscriptions are handled via OnBroadcast event
            // Return a subscription that wraps the event handler
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    return new NetworkMethodSubscription(connection.Network, serviceName, methodName, handler);
                }
            }

            throw new InvalidOperationException($"Not connected to entity '{entityId}'. Call GetServiceAsync first.");
        }

        /// <summary>
        /// Subscribe to a method being replayed with strongly-typed arguments.
        /// </summary>
        public IMethodSubscription OnMethodReplayed<TArgs>(string entityId, string serviceName, string methodName, Action<TArgs> handler)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    return new NetworkMethodSubscription<TArgs>(connection.Network, serviceName, methodName, handler, _serializer);
                }
            }

            throw new InvalidOperationException($"Not connected to entity '{entityId}'. Call GetServiceAsync first.");
        }

        private MetaServiceConfig GetConfig<TApiClient>()
        {
            if (!_serviceConfigs.TryGetValue(typeof(TApiClient), out var config))
            {
                throw new InvalidOperationException(
                    $"Service '{typeof(TApiClient).Name}' not registered. " +
                    $"Call RegisterService<{typeof(TApiClient).Name}>() first.");
            }
            return config;
        }

        #region ICrossEntityResolver

        bool ICrossEntityResolver.IsSubscribed(string entityId)
        {
            lock (_lock)
            {
                return _connections.ContainsKey(entityId);
            }
        }

        async Task ICrossEntityResolver.EnsureSubscribedAsync(string entityId, string stateTypeName)
        {
            bool alreadyConnected;
            lock (_lock)
            {
                alreadyConnected = _connections.ContainsKey(entityId);
            }

            if (alreadyConnected) return;

            // Find the config for this state type
            if (!_configsByStateTypeName.TryGetValue(stateTypeName, out var config))
                throw new InvalidOperationException($"No service config registered for state type '{stateTypeName}'");

            // Create connection (subscribe to entity)
            var subResult = await _networkFactory(entityId, stateTypeName);

            object state;
            if (subResult.StateBytes != null && subResult.StateBytes.Length > 0)
            {
                state = _serializer.Unpack(config.StateType, subResult.StateBytes)
                    ?? throw new InvalidOperationException($"Failed to deserialize state of type '{config.StateType.Name}'");
            }
            else
            {
                state = Activator.CreateInstance(config.StateType)
                    ?? throw new InvalidOperationException($"Failed to create state of type '{config.StateType.Name}'");
            }

            MetaRandom? optimisticRandom = null;
            if (subResult.OptimisticRandomBytes != null && subResult.OptimisticRandomBytes.Length > 0)
                optimisticRandom = _serializer.Unpack<MetaRandom>(subResult.OptimisticRandomBytes);
            else
                optimisticRandom = MetaRandom.FromString(entityId + ":optimistic");

            var entityConfig = await ResolveConfigAsync(config, subResult, entityId);

            lock (_lock)
            {
                if (!_connections.ContainsKey(entityId))
                {
                    _connections[entityId] = new EntityConnection
                    {
                        EntityId = entityId,
                        Network = subResult.Network,
                        StateType = config.StateType,
                        State = state,
                        OptimisticRandom = optimisticRandom,
                        Config = entityConfig
                    };
                }
            }
        }

        object ICrossEntityResolver.GetEntityState(string entityId)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                    return connection.State;
            }
            throw new InvalidOperationException($"Not connected to entity '{entityId}'. Call EnsureSubscribedAsync first.");
        }

        void ICrossEntityResolver.UpdateCachedState(string entityId, object newState)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                    connection.State = newState;
            }
        }

        IMetaSerializer ICrossEntityResolver.Serializer => _serializer;

        void ICrossEntityResolver.RecordCrossEntityResult(string entityId, string serviceName, string methodName, object? result)
        {
            _recordedResults ??= new();
            _recordedResults.Add(new CrossEntityLocalResult
            {
                EntityId = entityId,
                ServiceName = serviceName,
                MethodName = methodName,
                Result = result
            });
        }

        List<CrossEntityLocalResult> ICrossEntityResolver.TakeRecordedResults()
        {
            var results = _recordedResults ?? new();
            _recordedResults = null;
            return results;
        }

        #endregion

        /// <summary>
        /// Refresh entity states from server-provided resubscription data.
        /// Called after transport reconnect when the server re-subscribed entities automatically.
        /// Uses StateRefresher to update state in-place — existing API client references stay valid.
        /// </summary>
        public void RefreshEntityStates(List<Core.Transport.ResubscribedEntityInfo> resubscribedEntities)
        {
            lock (_lock)
            {
                foreach (var entity in resubscribedEntities)
                {
                    if (!_connections.TryGetValue(entity.EntityId, out var connection))
                        continue;

                    // Deserialize fresh state from server
                    if (entity.StateBytes is { Length: > 0 })
                    {
                        var newState = _serializer.Unpack(connection.StateType, entity.StateBytes);
                        if (newState != null)
                        {
                            connection.State = newState;

                            // Update optimistic random
                            if (entity.OptimisticRandomBytes is { Length: > 0 })
                            {
                                connection.OptimisticRandom = _serializer.Unpack<MetaRandom>(entity.OptimisticRandomBytes);
                            }

                            // Refresh state in existing API clients via generated RefreshState method.
                            // This fires OnStateRefreshed event so client code can update Views.
                            foreach (var (clientType, apiClient) in connection.ApiClients)
                            {
                                if (_serviceConfigs.TryGetValue(clientType, out var config))
                                {
                                    config.StateRefresher?.Invoke(apiClient, newState, connection.OptimisticRandom);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Clear all cached entity connections. Used for session restart after supersede.
        /// Disposes both API clients and network adapters to clean up broadcast subscriptions.
        /// </summary>
        public void ClearAllConnections()
        {
            lock (_lock)
            {
                foreach (var connection in _connections.Values)
                {
                    foreach (var apiClient in connection.ApiClients.Values)
                    {
                        if (apiClient is IDisposable disposable)
                            disposable.Dispose();
                    }
                    if (connection.Network is IDisposable networkDisposable)
                        networkDisposable.Dispose();
                }
                _connections.Clear();
            }
        }

        /// <summary>
        /// Resolve config for an entity subscription.
        /// Priority: cache → download → bundled factory.
        /// </summary>
        private async Task<object?> ResolveConfigAsync(MetaServiceConfig config, NetworkSubscribeResult subResult, string entityId)
        {
            if (config.ConfigType == null || config.ConfigFactory == null)
                return null;

            var serverVersion = subResult.ConfigVersion;

            // No version from server (0,0) → use bundled config
            if (serverVersion.Major == 0)
                return config.ConfigFactory.Invoke();

            var configTypeName = config.ConfigType.FullName ?? config.ConfigType.Name;

            // Check cache
            if (ConfigCache != null)
            {
                var cached = ConfigCache.TryGet(configTypeName, serverVersion);
                if (cached != null)
                    return cached;
            }

            // Try download: request URL from server, then download
            if (ConfigDownloader != null && ConfigDownloadUrlFactory != null)
            {
                try
                {
                    var downloadUrl = await ConfigDownloadUrlFactory(configTypeName, serverVersion);
                    if (downloadUrl != null)
                    {
                        var bytes = await ConfigDownloader.DownloadAsync(downloadUrl);
                        var downloaded = _serializer.Unpack(config.ConfigType, bytes);
                        if (downloaded != null)
                        {
                            ConfigCache?.Put(configTypeName, serverVersion, downloaded);
                            return downloaded;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Core.Logging.MetaLog.Warning($"[MetaServiceResolver] Config download failed: {ex.Message}. Using bundled config.");
                }
            }

            // Fallback: use bundled config from shared code
            var bundled = config.ConfigFactory.Invoke();
            ConfigCache?.Put(configTypeName, serverVersion, bundled);
            return bundled;
        }

        public void Dispose()
        {
            ClearAllConnections();
        }

        private class EntityConnection
        {
            public string EntityId { get; init; } = "";
            public INetwork Network { get; init; } = null!;
            public Type StateType { get; init; } = null!;
            public object State { get; set; } = null!;
            public MetaRandom? OptimisticRandom { get; set; }
            public object? Config { get; set; }
            public Dictionary<Type, object> ApiClients { get; } = new();
        }

        /// <summary>
        /// Subscription wrapper for INetwork.OnBroadcast events.
        /// </summary>
        private class NetworkMethodSubscription : IMethodSubscription
        {
            private readonly INetwork _network;
            private readonly string _serviceName;
            private readonly string _methodName;
            private readonly Action<MethodReplayedContext> _handler;
            private bool _disposed;

            public NetworkMethodSubscription(
                INetwork network,
                string serviceName,
                string methodName,
                Action<MethodReplayedContext> handler)
            {
                _network = network;
                _serviceName = serviceName;
                _methodName = methodName;
                _handler = handler;
                _network.OnBroadcast += HandleBroadcast;
            }

            private void HandleBroadcast(NetworkBroadcast broadcast)
            {
                if (_disposed) return;
                if (broadcast.ServiceName != _serviceName || broadcast.MethodName != _methodName) return;

                var context = new MethodReplayedContext
                {
                    MethodName = broadcast.MethodName,
                    CallerId = broadcast.CallerId,
                    ArgsBytes = broadcast.ArgsBytes
                };
                _handler(context);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _network.OnBroadcast -= HandleBroadcast;
            }
        }

        /// <summary>
        /// Typed subscription wrapper.
        /// </summary>
        private class NetworkMethodSubscription<TArgs> : IMethodSubscription
        {
            private readonly INetwork _network;
            private readonly string _serviceName;
            private readonly string _methodName;
            private readonly Action<TArgs> _handler;
            private readonly IMetaSerializer _serializer;
            private bool _disposed;

            public NetworkMethodSubscription(
                INetwork network,
                string serviceName,
                string methodName,
                Action<TArgs> handler,
                IMetaSerializer serializer)
            {
                _network = network;
                _serviceName = serviceName;
                _methodName = methodName;
                _handler = handler;
                _serializer = serializer;
                _network.OnBroadcast += HandleBroadcast;
            }

            private void HandleBroadcast(NetworkBroadcast broadcast)
            {
                if (_disposed) return;
                if (broadcast.ServiceName != _serviceName || broadcast.MethodName != _methodName) return;

                var args = _serializer.Unpack<TArgs>(broadcast.ArgsBytes);
                if (args != null)
                    _handler(args);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _network.OnBroadcast -= HandleBroadcast;
            }
        }
    }
}
