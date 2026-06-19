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
        // Indexed by ServiceName ("ICounterService" etc.) so the entity-level broadcast handler
        // can look up a foreign service's EntityReplayDispatcher when no state-data was sent.
        private readonly Dictionary<string, MetaServiceConfig> _configsByServiceName = new();
        // 0.24.0+ Indexed by client-local MethodId — populated from each registered
        // MetaServiceConfig's MethodIds set. The entity-level broadcast handler routes
        // a foreign-service broadcast by looking up the owning service via this map.
        // Replaces the legacy ServiceName-string lookup on the wire.
        private readonly Dictionary<ushort, MetaServiceConfig> _configsByMethodId = new();
        // Per-state-type callbacks. Multiple services on the same state generate functionally
        // equivalent factories; we keep the first non-null one we see, so a hand-rolled config
        // that drops these fields doesn't clobber the generator-emitted ones registered earlier.
        private readonly Dictionary<string, Func<object, IEntityStateContainer>> _stateContainerFactoriesByStateType = new();
        private readonly Dictionary<string, Action<object, byte[], IMetaSerializer>> _patchAppliersByStateType = new();
        private readonly Dictionary<string, EntityConnection> _connections = new();
        // Provider registered per TConfig type — single point of materialization for entity
        // configs at subscribe time. Replaces the pre-0.14.0 cache + downloader + URL factory +
        // bundled-factory chain. Keyed by typeof(TConfig); values are typed closures that the
        // generic Register{Try}ConfigProvider<TConfig> methods build at registration time so the
        // hot path stays reflection-free.
        private readonly Dictionary<Type, Func<MetaConfigVersion, Task<object?>>> _configProviders = new();
        // Per-TConfig cache-wipe hook, populated when a registered provider implements
        // IClearableConfigProvider (DownloadingConfigProvider / CompositeConfigProvider).
        // Invoked by ClearConfigCaches() — the client-side debug/dev command. Keyed by type so
        // re-registration replaces rather than accumulates.
        private readonly Dictionary<Type, Action> _configCacheClearHooks = new();
        // Within-session memo of resolved configs, keyed by (TConfig type, version). A config is
        // immutable per (type, version), and several entities commonly share one config type
        // (e.g. ProfileState + ExpeditionState both use ExpeditionConfig). Without this, each
        // entity subscribe re-invokes the provider — a cache-less DownloadingConfigProvider then
        // re-downloads the same bytes once per entity. This dedups across entities (and dedups
        // concurrent in-flight subscribes by sharing the Task); cleared by ClearConfigCaches.
        private readonly Dictionary<(Type, MetaConfigVersion), Task<object?>> _resolvedConfigByVersion = new();
        private readonly object _lock = new();
        private List<CrossEntityLocalResult>? _recordedResults;

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
            if (!string.IsNullOrEmpty(config.ServiceName))
                _configsByServiceName[config.ServiceName] = config;
            // 0.24.0+ Populate the per-MethodId reverse index used by foreign-service
            // broadcast routing. Method ids are assembly-local and unique within an assembly
            // (canonical sort of [MetaMethod] declarations); collisions across services
            // inside one assembly are a generator invariant violation.
            foreach (var methodId in config.MethodIds)
                _configsByMethodId[methodId] = config;

            // Cache per-state-type callbacks. First non-null wins so a later hand-rolled
            // config with these fields dropped doesn't override an earlier generator-emitted one.
            if (config.StateContainerFactory != null && !_stateContainerFactoriesByStateType.ContainsKey(stateTypeName))
                _stateContainerFactoriesByStateType[stateTypeName] = config.StateContainerFactory;
            if (config.PatchApplier != null && !_patchAppliersByStateType.ContainsKey(stateTypeName))
                _patchAppliersByStateType[stateTypeName] = config.PatchApplier;
        }

        public void RegisterConfigProvider<TConfig>(IClientMetaConfigProvider<TConfig> provider) where TConfig : class
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            _configProviders[typeof(TConfig)] = BuildInvoker(provider);
            CaptureClearHook<TConfig>(provider);
        }

        public void TryRegisterConfigProvider<TConfig>(IClientMetaConfigProvider<TConfig> provider) where TConfig : class
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (!_configProviders.ContainsKey(typeof(TConfig)))
            {
                _configProviders[typeof(TConfig)] = BuildInvoker(provider);
                CaptureClearHook<TConfig>(provider);
            }
        }

        // Record the provider's cache-wipe entry point if it opted in. Keyed by TConfig so a
        // later Register for the same type replaces the hook rather than leaving a stale one.
        private void CaptureClearHook<TConfig>(IClientMetaConfigProvider<TConfig> provider) where TConfig : class
        {
            if (provider is IClearableConfigProvider clearable)
                _configCacheClearHooks[typeof(TConfig)] = clearable.ClearCache;
            else
                _configCacheClearHooks.Remove(typeof(TConfig));
        }

        /// <summary>
        /// Debug/dev command: wipe every registered config provider's cache (e.g. the on-disk
        /// <see cref="FileConfigCache{TConfig}"/>). The next entity subscribe re-downloads the
        /// config. Handy when a config was re-published under the same version during
        /// development — the version-keyed cache would otherwise serve the stale copy.
        /// Providers without a clearable cache (e.g. <see cref="StaticConfigProvider{TConfig}"/>)
        /// are skipped.
        /// </summary>
        public void ClearConfigCaches()
        {
            Action[] hooks;
            lock (_lock)
            {
                hooks = new Action[_configCacheClearHooks.Count];
                _configCacheClearHooks.Values.CopyTo(hooks, 0);
                // Drop the within-session memo too, so a re-published same-version config is
                // re-resolved on the next subscribe rather than served from this dictionary.
                _resolvedConfigByVersion.Clear();
            }
            foreach (var hook in hooks)
            {
                try { hook(); }
                catch (Exception ex)
                {
                    Core.Logging.MetaLog.Warning($"[MetaServiceResolver] ClearConfigCaches hook threw: {ex.Message}");
                }
            }
        }

        // Closure built once per Register call — captures TConfig statically so the resolver's
        // hot path can call provider.GetConfigAsync without any reflection.
        private static Func<MetaConfigVersion, Task<object?>> BuildInvoker<TConfig>(IClientMetaConfigProvider<TConfig> provider) where TConfig : class
        {
            return async version => await provider.GetConfigAsync(version).ConfigureAwait(false);
        }

        private MetaServiceConfig? LookupConfigByServiceName(string serviceName)
        {
            return _configsByServiceName.TryGetValue(serviceName, out var c) ? c : null;
        }

        private MetaServiceConfig? LookupConfigByMethodId(ushort methodId)
        {
            return _configsByMethodId.TryGetValue(methodId, out var c) ? c : null;
        }

        /// <summary>
        /// 0.20.0 client-side sibling resolution. Looks up the registered MetaServiceConfig for
        /// <paramref name="interfaceType"/> and invokes its generator-emitted
        /// <see cref="MetaServiceConfig.ClientSiblingFactory"/> with <paramref name="metaContext"/> —
        /// produces a transient impl bound to the caller's Context, no reflection. Returns null
        /// if no service is registered for the interface or if the registered config didn't
        /// emit a sibling factory (legacy services predating 0.20.0).
        /// </summary>
        public object? ResolveClientSibling(string entityId, Type interfaceType, object metaContext)
        {
            if (!_configsByServiceName.TryGetValue(interfaceType.Name, out var config) ||
                config.ClientSiblingFactory == null)
                return null;
            return config.ClientSiblingFactory(metaContext);
        }

        /// <summary>
        /// Pick the best PatchApplier for the config's state type — calling service's first,
        /// else any service registered against the same state. Lets a hand-rolled config that
        /// drops PatchApplier still benefit from a generator-emitted applier on a sibling service.
        /// </summary>
        private Action<object, byte[], IMetaSerializer>? ResolvePatchApplier(MetaServiceConfig config)
        {
            if (config.PatchApplier != null) return config.PatchApplier;
            var stateTypeName = config.StateType.FullName ?? config.StateType.Name;
            return _patchAppliersByStateType.TryGetValue(stateTypeName, out var applier) ? applier : null;
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
            IEntityStateContainer stateContainer;
            MetaRandom? optimisticRandom = null;
            MetaRandom[]? namedRandoms = null;
            object? entityConfig = null;
            EntityConnection? newConnection = null;

            if (existingConnection != null)
            {
                // Reuse existing network, container, random, and config — every API client on this
                // entity must observe the same state, even after a wholesale ServerReplace.
                network = existingConnection.Network;
                stateContainer = existingConnection.StateContainer;
                optimisticRandom = existingConnection.OptimisticRandom;
                namedRandoms = existingConnection.NamedRandoms;
                entityConfig = existingConnection.Config;
            }
            else
            {
                // Create new connection - pass stateTypeName so factory can call Subscribe
                var stateTypeName = config.StateType.FullName ?? config.StateType.Name;
                var subResult = await _networkFactory(entityId, stateTypeName);
                network = subResult.Network;

                object initialState;
                if (subResult.StateBytes != null && subResult.StateBytes.Length > 0)
                {
                    initialState = _serializer.Unpack(config.StateType, subResult.StateBytes)
                        ?? throw new InvalidOperationException($"Failed to deserialize state of type '{config.StateType.Name}'");
                }
                else
                {
                    initialState = Activator.CreateInstance(config.StateType)
                        ?? throw new InvalidOperationException($"Failed to create state of type '{config.StateType.Name}'");
                }

                stateContainer = CreateContainer(config, initialState);

                // Deserialize optimistic random, or create from entityId seed
                if (subResult.OptimisticRandomBytes != null && subResult.OptimisticRandomBytes.Length > 0)
                {
                    optimisticRandom = _serializer.Unpack<MetaRandom>(subResult.OptimisticRandomBytes);
                }
                else
                {
                    optimisticRandom = MetaRandom.FromString(entityId + ":optimistic");
                }

                // Deserialize named randoms (null when state has no [NamedRandom] declarations)
                if (subResult.NamedRandomsBytes is { Length: > 0 } nrBytes)
                {
                    namedRandoms = _serializer.Unpack<MetaRandom[]>(nrBytes);
                }

                // Resolve config: check cache → request URL → download → fallback to factory
                entityConfig = await ResolveConfigAsync(config, subResult, entityId);

                newConnection = new EntityConnection
                {
                    EntityId = entityId,
                    Network = network,
                    StateType = config.StateType,
                    StateContainer = stateContainer,
                    OptimisticRandom = optimisticRandom,
                    NamedRandoms = namedRandoms,
                    Config = entityConfig,
                    ConfigType = config.ConfigType,
                    ConfigVersion = subResult.ConfigVersion,
                };

                // 0.21.0: hook the version-specific config fetcher so drift detection
                // (broadcasts carrying a different ExecutedConfigVersion than the session)
                // can lazily materialize the right config for replay.
                if (config.ConfigType != null && _configProviders.TryGetValue(config.ConfigType, out var versionFetcher))
                    newConnection.VersionedConfigFetcher = versionFetcher;

                // Entity-level broadcast handler: applies state-data (StateBytes / PatchBytes) to the
                // shared container regardless of which service the broadcast came from. This is what
                // lets a client that subscribed to only one of several services on the entity still
                // receive every state update.
                newConnection.SubscribeBroadcasts(_serializer, ResolvePatchApplier(config), LookupConfigByMethodId, this);
            }

            // Create API client using factory — pass the container as the third positional arg.
            // The generated client casts it to EntityStateContainer<TState> internally.
            // 0.21.0: pass connection.ResolveConfigForBroadcast as configResolver so the client's
            // broadcast-replay path can materialize the right config when the server's
            // ExecutedConfigVersion drifts from the session pin. existingConnection / newConnection
            // exposes it; if neither (rare race), we fall back to no resolver (session config only).
            var activeConnectionForResolver = existingConnection ?? newConnection;
            Func<MetaConfigVersion, object?>? configResolver = activeConnectionForResolver != null
                ? activeConnectionForResolver.ResolveConfigForBroadcast
                : null;
            var apiClient = (TApiClient)config.ApiClientFactory(
                network,
                _serializer,
                stateContainer,
                _modeProvider,
                _diagnostics,
                this,
                optimisticRandom,
                entityConfig,
                namedRandoms,
                configResolver);

            // Cache connection
            lock (_lock)
            {
                if (!_connections.TryGetValue(entityId, out var connection))
                {
                    connection = newConnection ?? throw new InvalidOperationException(
                        "Internal: connection went missing between subscribe and cache.");
                    _connections[entityId] = connection;
                }
                else if (newConnection != null && !ReferenceEquals(connection, newConnection))
                {
                    // Lost a race — another caller cached a connection while we were subscribing.
                    // Tear down our orphan handler so the network doesn't get a duplicate listener.
                    newConnection.Dispose();
                }

                connection.ApiClients[typeof(TApiClient)] = apiClient;
                if (!string.IsNullOrEmpty(config.ServiceName))
                    connection.LocalServiceNames.Add(config.ServiceName);
                foreach (var methodId in config.MethodIds)
                    connection.LocalMethodIds.Add(methodId);
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

            connection.Dispose();
            return Task.CompletedTask;
        }

        public TState GetState<TState>(string entityId) where TState : class, ISharedState
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    return (TState)connection.StateContainer.State;
                }
            }

            throw new InvalidOperationException($"Not connected to entity '{entityId}'");
        }

        /// <summary>
        /// Synchronous, allocation-free accessor for an already-resolved API client. Returns true
        /// and the cached <typeparamref name="TApiClient"/> when the entity is subscribed AND that
        /// client type has been created (by a prior <see cref="GetServiceAsync{TApiClient}"/> call);
        /// otherwise returns false. Unlike <see cref="GetServiceAsync{TApiClient}"/> this never
        /// subscribes, never creates the client, and never allocates a Task — the hot path is a lock
        /// plus two dictionary lookups. Use it on frame-critical paths where the entity is known to
        /// be subscribed; fall back to <see cref="GetServiceAsync{TApiClient}"/> for the first call.
        /// </summary>
        public bool TryGetService<TApiClient>(string entityId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TApiClient? api) where TApiClient : class
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection)
                    && connection.ApiClients.TryGetValue(typeof(TApiClient), out var existing))
                {
                    api = (TApiClient)existing;
                    return true;
                }
            }

            api = null;
            return false;
        }

        /// <summary>
        /// Returns the shared state container for an entity, exposing the joint
        /// <see cref="EntityStateContainer{TState}.MutationCount"/> /
        /// <see cref="EntityStateContainer{TState}.OnMutated"/> surface even when no
        /// API client for the entity has been requested yet (or when only one of
        /// several services on the entity has been resolved).
        /// </summary>
        public EntityStateContainer<TState> GetStateContainer<TState>(string entityId) where TState : class, ISharedState
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    if (connection.StateContainer is EntityStateContainer<TState> typed)
                        return typed;
                    throw new InvalidOperationException(
                        $"Entity '{entityId}' state container is of type '{connection.StateType.Name}', not '{typeof(TState).Name}'.");
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
        /// 0.20.3: snapshot every currently subscribed entity for debug inspection. Use to
        /// answer "which entities is this client tracking, which config branch got pinned,
        /// which services are wired locally?" — the kind of question that comes up when a
        /// desync or NRE deep in user code needs context. Snapshot is taken at call time
        /// and is not kept live; do not branch production logic on the returned records
        /// (use <see cref="GetState{TState}"/> + state container <c>OnMutated</c> for that).
        /// </summary>
        public IReadOnlyList<SubscribedEntityInfo> GetSubscribedEntities()
        {
            lock (_lock)
            {
                var result = new List<SubscribedEntityInfo>(_connections.Count);
                foreach (var (entityId, connection) in _connections)
                {
                    result.Add(new SubscribedEntityInfo
                    {
                        EntityId = entityId,
                        StateType = connection.StateType,
                        ConfigType = connection.ConfigType,
                        ConfigVersion = connection.ConfigVersion,
                        ServiceNames = new List<string>(connection.LocalServiceNames),
                        State = connection.StateContainer.State,
                        Config = connection.Config,
                    });
                }
                return result;
            }
        }

        /// <summary>
        /// 0.20.3: convenience one-liner for logs / status panels. Produces a multi-line
        /// summary of <see cref="GetSubscribedEntities"/>:
        /// <code>
        /// alice (ProfileState, ExpeditionConfig@2.0, [IProfileService])
        /// expedition-alice-1 (ExpeditionState, ExpeditionConfig@2.0, [IExpeditionService])
        /// </code>
        /// Format is debug-only and may change; do not parse.
        /// </summary>
        public string DescribeSubscriptions()
        {
            var snapshot = GetSubscribedEntities();
            if (snapshot.Count == 0) return "(no subscribed entities)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Count; i++)
            {
                var e = snapshot[i];
                var configPart = e.ConfigType != null
                    ? $"{e.ConfigType.Name}@{e.ConfigVersion.Major}.{e.ConfigVersion.Minor}.{e.ConfigVersion.Patch}"
                    : "no config";
                var services = e.ServiceNames.Count == 0
                    ? "(no local API clients)"
                    : "[" + string.Join(", ", e.ServiceNames) + "]";
                sb.Append(e.EntityId).Append(" (").Append(e.StateType.Name)
                  .Append(", ").Append(configPart).Append(", ").Append(services).Append(')');
                if (i < snapshot.Count - 1) sb.AppendLine();
            }
            return sb.ToString();
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
        public IMethodSubscription OnMethodReplayed(string entityId, ushort methodId, Action<MethodReplayedContext> handler)
        {
            // For INetwork-based architecture, subscriptions are handled via OnBroadcast event
            // Return a subscription that wraps the event handler
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    return new NetworkMethodSubscription(connection.Network, methodId, handler);
                }
            }

            throw new InvalidOperationException($"Not connected to entity '{entityId}'. Call GetServiceAsync first.");
        }

        /// <summary>
        /// Subscribe to a method being replayed with strongly-typed arguments.
        /// </summary>
        public IMethodSubscription OnMethodReplayed<TArgs>(string entityId, ushort methodId, Action<TArgs> handler)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                {
                    return new NetworkMethodSubscription<TArgs>(connection.Network, methodId, handler, _serializer);
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

        private IEntityStateContainer CreateContainer(MetaServiceConfig config, object initialState)
        {
            // Prefer the calling service's factory; fall back to any other service registered
            // for the same state type. All such factories are functionally equivalent (they all
            // wrap the state in the same EntityStateContainer<TState>), so picking any non-null
            // one works. No reflection fallback — strictly typed lambdas, AOT-safe.
            var factory = config.StateContainerFactory;
            if (factory == null)
            {
                var stateTypeName = config.StateType.FullName ?? config.StateType.Name;
                _stateContainerFactoriesByStateType.TryGetValue(stateTypeName, out factory);
            }
            return factory!(initialState);
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

            object initialState;
            if (subResult.StateBytes != null && subResult.StateBytes.Length > 0)
            {
                initialState = _serializer.Unpack(config.StateType, subResult.StateBytes)
                    ?? throw new InvalidOperationException($"Failed to deserialize state of type '{config.StateType.Name}'");
            }
            else
            {
                initialState = Activator.CreateInstance(config.StateType)
                    ?? throw new InvalidOperationException($"Failed to create state of type '{config.StateType.Name}'");
            }

            var stateContainer = CreateContainer(config, initialState);

            MetaRandom? optimisticRandom = null;
            if (subResult.OptimisticRandomBytes != null && subResult.OptimisticRandomBytes.Length > 0)
                optimisticRandom = _serializer.Unpack<MetaRandom>(subResult.OptimisticRandomBytes);
            else
                optimisticRandom = MetaRandom.FromString(entityId + ":optimistic");

            MetaRandom[]? namedRandoms = null;
            if (subResult.NamedRandomsBytes is { Length: > 0 } nrBytes)
                namedRandoms = _serializer.Unpack<MetaRandom[]>(nrBytes);

            var entityConfig = await ResolveConfigAsync(config, subResult, entityId);

            EntityConnection? newConnection = new EntityConnection
            {
                EntityId = entityId,
                Network = subResult.Network,
                StateType = config.StateType,
                StateContainer = stateContainer,
                OptimisticRandom = optimisticRandom,
                NamedRandoms = namedRandoms,
                Config = entityConfig,
                ConfigType = config.ConfigType,
                ConfigVersion = subResult.ConfigVersion,
            };
            newConnection.SubscribeBroadcasts(_serializer, config.PatchApplier, LookupConfigByMethodId, this);
            if (!string.IsNullOrEmpty(config.ServiceName))
                newConnection.LocalServiceNames.Add(config.ServiceName);
            foreach (var methodId in config.MethodIds)
                newConnection.LocalMethodIds.Add(methodId);

            lock (_lock)
            {
                if (!_connections.ContainsKey(entityId))
                {
                    _connections[entityId] = newConnection;
                    newConnection = null;
                }
            }

            // Lost the race — another caller already cached a connection. Drop the orphan.
            newConnection?.Dispose();
        }

        object ICrossEntityResolver.GetEntityState(string entityId)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                    return connection.StateContainer.State;
            }
            throw new InvalidOperationException($"Not connected to entity '{entityId}'. Call EnsureSubscribedAsync first.");
        }

        void ICrossEntityResolver.UpdateCachedState(string entityId, object newState)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(entityId, out var connection))
                    connection.StateContainer.ReplaceObject(newState);
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

        object? ICrossEntityResolver.ResolveSibling(System.Type interfaceType, object metaContext)
        {
            // Look up by interface name — _configsByServiceName is the only registry keyed by
            // interface (the others are by ApiClient type or state type).
            if (!_configsByServiceName.TryGetValue(interfaceType.Name, out var config) ||
                config.ClientSiblingFactory == null)
                return null;
            return config.ClientSiblingFactory(metaContext);
        }

        #endregion

        /// <summary>
        /// 0.24.0+ Apply per-entity subscription verdicts produced by the server on Resume.
        /// <list type="bullet">
        /// <item><see cref="Core.Transport.SubscriptionStatus.Continued"/> — no state shipped,
        /// nothing to do; the local view is already current.</item>
        /// <item><see cref="Core.Transport.SubscriptionStatus.Refreshed"/> — full snapshot
        /// attached; replace the container's state, update optimistic / named randoms, and
        /// fire StateRefresher hooks on every registered API client.</item>
        /// <item><see cref="Core.Transport.SubscriptionStatus.Failed"/> — not applied here;
        /// reaches this method only if SessionManagerGrain didn't abort the Resume (it should
        /// have, but defensive). Logged and skipped; game-level recovery handles it.</item>
        /// </list>
        /// </summary>
        public void ApplySubscriptionVerdicts(List<Core.Transport.SubscriptionResult> verdicts)
        {
            lock (_lock)
            {
                foreach (var v in verdicts)
                {
                    if (v.Status == Core.Transport.SubscriptionStatus.Continued)
                        continue; // server confirmed our view; nothing to apply

                    if (v.Status == Core.Transport.SubscriptionStatus.Failed)
                        continue; // defensive — Resume should have aborted upstream

                    if (!_connections.TryGetValue(v.EntityId, out var connection))
                        continue;

                    if (v.StateBytes is not { Length: > 0 })
                        continue; // Refreshed without bytes — malformed, skip

                    var newState = _serializer.Unpack(connection.StateType, v.StateBytes);
                    if (newState == null) continue;

                    // Diagnostic: Refreshed verdict ships fresh server-side state that overwrites
                    // ANY local optimistic mutations. Logs the moment of state replacement so a
                    // visible "state rollback on reconnect" can be traced to the exact verdict.
                    Core.Logging.MetaLog.Info(
                        $"[MetaServiceResolver] State REPLACED for entity {v.EntityId} via Refreshed verdict — seq={v.EntitySequenceNumber}, stateBytes={v.StateBytes.Length}");
                    connection.StateContainer.ReplaceObject(newState);

                    if (v.OptimisticRandomBytes is { Length: > 0 })
                        connection.OptimisticRandom = _serializer.Unpack<MetaRandom>(v.OptimisticRandomBytes);

                    if (v.NamedRandomsBytes is { Length: > 0 } nrBytes)
                        connection.NamedRandoms = _serializer.Unpack<MetaRandom[]>(nrBytes);

                    foreach (var (clientType, apiClient) in connection.ApiClients)
                    {
                        if (_serviceConfigs.TryGetValue(clientType, out var config))
                        {
                            config.StateRefresher?.Invoke(apiClient, newState, connection.OptimisticRandom, connection.NamedRandoms);
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
                    connection.Dispose();
                }
                _connections.Clear();
            }
        }

        /// <summary>
        /// Resolve config for an entity subscription via the registered
        /// <see cref="IClientMetaConfigProvider{TConfig}"/>. The provider owns caching,
        /// downloading, and fallback logic — the resolver just hands it the server-pinned
        /// version and awaits the materialized config.
        /// </summary>
        private Task<object?> ResolveConfigAsync(MetaServiceConfig config, NetworkSubscribeResult subResult, string entityId)
        {
            if (config.ConfigType == null) return Task.FromResult<object?>(null);

            if (!_configProviders.TryGetValue(config.ConfigType, out var invoker))
            {
                // Fail loud at the first subscribe instead of silently propagating a null
                // config and detonating later inside service code with an opaque NRE on
                // Context.Config.X. Pre-0.17.0 returned null + Warning, and the generator
                // also auto-emitted a default-constructed StaticConfigProvider for any
                // service with a ConfigType — together those two hid wiring bugs across
                // entire game projects. The auto-default is now opt-in via
                // [MetaService(DefaultConfig = true)] only, and a missing provider is an
                // error, not a warning.
                throw new InvalidOperationException(
                    $"[SharedMeta] No IClientMetaConfigProvider<{config.ConfigType.Name}> registered " +
                    $"for service '{config.ServiceName}' on entity '{entityId}'. Register before subscribing:\n\n" +
                    $"    resolver.RegisterConfigProvider<{config.ConfigType.Name}>(\n" +
                    $"        new StaticConfigProvider<{config.ConfigType.Name}>(yourLoadedConfig));\n\n" +
                    $"or for server-driven configs (recommended for real servers):\n\n" +
                    $"    resolver.RegisterConfigProvider<{config.ConfigType.Name}>(\n" +
                    $"        new DownloadingConfigProvider<{config.ConfigType.Name}>(\n" +
                    $"            urlResolver: client.ConfigDownloadUrlResolver(stateTypeName),\n" +
                    $"            downloader:  UnityConfigDownloader.DownloadAsync,\n" +
                    $"            serializer:  client.Serializer));\n\n" +
                    $"As of SharedMeta 0.17.0 the generator no longer silently registers a default-" +
                    $"constructed StaticConfigProvider; opt in by adding DefaultConfig = true on the " +
                    $"[MetaService] attribute if an empty {config.ConfigType.Name}() is intentionally " +
                    $"acceptable as a fallback.");
            }

            // Memoize per (TConfig, version): share one provider invocation across every entity
            // that uses this config at this version, and across concurrent in-flight subscribes.
            var key = (config.ConfigType, subResult.ConfigVersion);
            Task<object?> task;
            lock (_lock)
            {
                if (!_resolvedConfigByVersion.TryGetValue(key, out task!))
                {
                    task = invoker(subResult.ConfigVersion);
                    _resolvedConfigByVersion[key] = task;
                }
            }
            return AwaitConfigEvictingOnFailure(key, task);
        }

        // Evict a failed resolution so a transient download error doesn't poison the memo —
        // the next subscribe retries instead of replaying the cached failed Task.
        private async Task<object?> AwaitConfigEvictingOnFailure((Type, MetaConfigVersion) key, Task<object?> task)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch
            {
                lock (_lock)
                {
                    if (_resolvedConfigByVersion.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                        _resolvedConfigByVersion.Remove(key);
                }
                throw;
            }
        }

        public void Dispose()
        {
            ClearAllConnections();
        }

        private class EntityConnection : IDisposable
        {
            public string EntityId { get; init; } = "";
            public INetwork Network { get; init; } = null!;
            public Type StateType { get; init; } = null!;
            public IEntityStateContainer StateContainer { get; init; } = null!;
            public MetaRandom? OptimisticRandom { get; set; }
            public MetaRandom[]? NamedRandoms { get; set; }
            public object? Config { get; set; }
            /// <summary>0.20.3: tracked for <see cref="GetSubscribedEntities"/> debug snapshot.</summary>
            public Type? ConfigType { get; init; }
            /// <summary>0.20.3: tracked for <see cref="GetSubscribedEntities"/> debug snapshot.</summary>
            public MetaConfigVersion ConfigVersion { get; init; }

            // 0.21.0 — per-call config drift detection. Server sends ExecutedConfigVersion on every
            // RpcResponse / EntityBroadcast. When it differs from this entity's session ConfigVersion
            // (e.g. a mid-session admin rollout of a Global config, or another player's call in a
            // mixed-version session), the client lazily fetches and caches the right TConfig instance
            // here so subsequent broadcasts at that version replay under it.
            //
            // The cache is populated by a background fetch fired from ResolveConfigForBroadcast.
            // The first broadcast at a new version is replayed under the session Config (best effort,
            // with a one-line diagnostic) — subsequent broadcasts at that version use the cached
            // entry. Cache is dropped when the entity is unsubscribed.
            private readonly Dictionary<MetaConfigVersion, object> _configByVersion = new();
            // Single in-flight fetch per version — avoids stampedes when broadcasts arrive in bursts.
            private readonly Dictionary<MetaConfigVersion, Task> _configFetchTasks = new();
            // Resolves a TConfig for a specific version. Set at subscribe time from the registered
            // IClientMetaConfigProvider<TConfig>. Null for entities without a config (Config == null).
            internal Func<MetaConfigVersion, Task<object?>>? VersionedConfigFetcher { get; set; }
            // One-line diagnostic emitted on first drift detection per version, to surface the
            // condition without spamming logs on every broadcast at the same drifted version.
            private readonly HashSet<MetaConfigVersion> _diagnostedDrifts = new();

            /// <summary>
            /// Resolve the right <c>Config</c> object to use for replaying a specific broadcast.
            /// Returns:
            /// <list type="bullet">
            ///   <item>The session <see cref="Config"/> when <paramref name="executedVersion"/>
            ///         matches <see cref="ConfigVersion"/> or is <c>default</c> (no config system).</item>
            ///   <item>The cached version-specific config when available.</item>
            ///   <item>The session <see cref="Config"/> as best-effort fallback when no cache
            ///         entry exists — and kicks off a fire-and-forget background fetch so the
            ///         next broadcast at this version hits the cache. Emits a one-time diagnostic
            ///         per drifted version.</item>
            /// </list>
            /// </summary>
            public object? ResolveConfigForBroadcast(MetaConfigVersion executedVersion)
            {
                // Default(0,0,0) = "no config system" sentinel from the server. Use session.
                if (executedVersion.Major == 0 && executedVersion.Minor == 0 && executedVersion.Patch == 0)
                    return Config;
                if (executedVersion == ConfigVersion) return Config;
                if (_configByVersion.TryGetValue(executedVersion, out var cached)) return cached;

                // Cache miss: fire fetch (idempotent if already in flight) and emit one-time diag.
                if (VersionedConfigFetcher != null && !_configFetchTasks.ContainsKey(executedVersion))
                {
                    var fetch = VersionedConfigFetcher(executedVersion);
                    _configFetchTasks[executedVersion] = fetch.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully && t.Result != null)
                            lock (_configByVersion) _configByVersion[executedVersion] = t.Result;
                        lock (_configFetchTasks) _configFetchTasks.Remove(executedVersion);
                    }, TaskScheduler.Default);
                }
                if (_diagnostedDrifts.Add(executedVersion))
                {
                    Core.Logging.MetaLog.Debug(
                        $"[MetaServiceResolver] Entity '{EntityId}' broadcast carries ExecutedConfigVersion " +
                        $"{executedVersion.Major}.{executedVersion.Minor}.{executedVersion.Patch} but session is pinned at " +
                        $"{ConfigVersion.Major}.{ConfigVersion.Minor}.{ConfigVersion.Patch}. Replaying under session config; " +
                        $"future broadcasts at this version will use the fetched config.");
                }
                return Config;
            }
            public Dictionary<Type, object> ApiClients { get; } = new();
            // 0.24.0+ Client-local method ids whose owning ApiClient is registered locally —
            // entity handler skips EntityReplayDispatcher for these to avoid double-application
            // (the matching ApiClient's own DispatchServiceBroadcast already replays the method).
            // Populated from each registered MetaServiceConfig's MethodIds set.
            public HashSet<ushort> LocalMethodIds { get; } = new();
            // 0.24.0+ Service names retained for the debug/snapshot surface (GetSubscribedEntities).
            // Not used for routing — that's MethodId-based now.
            public HashSet<string> LocalServiceNames { get; } = new();

            private Action<NetworkBroadcast>? _broadcastHandler;
            private IMetaSerializer? _serializer;
            private Action<object, byte[], IMetaSerializer>? _patchApplier;
            private Func<ushort, MetaServiceConfig?>? _configByMethodId;
            private ICrossEntityResolver? _crossEntityResolver;

            /// <summary>
            /// Registers a single entity-level handler that applies state mutations to the
            /// shared container before any API client's own broadcast handler runs. Three
            /// paths in priority order:
            /// <list type="number">
            ///   <item>StateBytes (ServerReplace) → wholesale container replace.</item>
            ///   <item>PatchBytes (ServerPatch) → apply patch in place via PatchApplier.</item>
            ///   <item>Pure replay (Optimistic / Server / CrossOptimistic) → look up the
            ///   broadcast's service config and run its <see cref="MetaServiceConfig.EntityReplayDispatcher"/>
            ///   to spin up the impl class on the fly and replay the method on the shared state.
            ///   Without this third path, broadcasts from services the receiver doesn't
            ///   subscribe to (e.g. SocialService.SendGift while the client only holds
            ///   ProfileServiceApiClient) would silently disappear.</item>
            /// </list>
            /// </summary>
            public void SubscribeBroadcasts(
                IMetaSerializer serializer,
                Action<object, byte[], IMetaSerializer>? patchApplier,
                Func<ushort, MetaServiceConfig?> configByMethodId,
                ICrossEntityResolver? crossEntityResolver)
            {
                _serializer = serializer;
                _patchApplier = patchApplier;
                _configByMethodId = configByMethodId;
                _crossEntityResolver = crossEntityResolver;
                _broadcastHandler = HandleEntityBroadcast;
                Network.OnBroadcast += _broadcastHandler;
            }

            private void HandleEntityBroadcast(NetworkBroadcast broadcast)
            {
                // Echoed broadcast for our own RPC — server-side replay already happened locally
                // through the optimistic / cross-optimistic / server-replace path.
                if (broadcast.CallerId == Network.PlayerId) return;

                if (broadcast.StateBytes is { Length: > 0 } sb)
                {
                    var newState = _serializer!.Unpack(StateType, sb);
                    if (newState != null)
                    {
                        // Diagnostic: ServerReplace broadcast ships fresh state that overwrites
                        // local optimistic mutations. Logs when this happens so reconnect-time
                        // state rollback can be traced to the exact broadcast.
                        Core.Logging.MetaLog.Info(
                            $"[MetaServiceResolver] State REPLACED for entity {EntityId} via ServerReplace broadcast — methodId={broadcast.MethodId}, stateBytes={sb.Length}");
                        StateContainer.ReplaceObject(newState);
                    }
                    return;
                }

                if (broadcast.PatchBytes is { Length: > 0 } pb)
                {
                    if (_patchApplier != null)
                    {
                        // ChangeTracker has to be active or [Tracked] field setters touched
                        // by the patch applier won't notify subscribers — UIs wired to
                        // Tracked{State}.OnChanged would silently miss ServerPatch broadcasts.
                        var tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();
                        try
                        {
                            _patchApplier(StateContainer.State, pb, _serializer!);
                            tracker.FlushAndNotify();
                        }
                        catch
                        {
                            tracker.Discard();
                            throw;
                        }
                        StateContainer.NotifyMutated();
                    }
                    return;
                }

                // No state-data — pure replay broadcast (Optimistic / Server / CrossOptimistic).
                // 0.24.0+ Routing is MethodId-based: if the broadcast's MethodId belongs to a
                // service whose ApiClient is registered locally, that client's own
                // DispatchServiceBroadcast will replay the method (per-method events fire,
                // _service field is reused). Skip here to avoid double-application.
                if (LocalMethodIds.Contains(broadcast.MethodId)) return;

                // Foreign service — look up its config by MethodId and use the
                // EntityReplayDispatcher to instantiate the impl class on the fly and
                // invoke the method against our state. Closes the gap pre-0.14.0 where
                // foreign-service Optimistic / Server broadcasts were silently dropped
                // by the per-ApiClient filter.
                var foreignConfig = _configByMethodId?.Invoke(broadcast.MethodId);
                if (foreignConfig?.EntityReplayDispatcher == null) return;
                if (foreignConfig.StateType != StateType) return; // service targets different state type

                try
                {
                    // 0.21.0: replay under the broadcast's ExecutedConfigVersion when it differs
                    // from the session (e.g. mid-session config rollout). Falls back to session
                    // Config on cache miss + kicks off a background fetch so the next broadcast
                    // at this version uses the right config.
                    var replayConfig = ResolveConfigForBroadcast(broadcast.ExecutedConfigVersion);
                    foreignConfig.EntityReplayDispatcher(
                        StateContainer.State,
                        broadcast.MethodId,
                        broadcast.ArgsBytes ?? Array.Empty<byte>(),
                        broadcast.ReplayContext ?? Array.Empty<byte>(),
                        broadcast.CallerId,
                        broadcast.ServerTimeTicks,
                        _serializer!,
                        OptimisticRandom,
                        NamedRandoms,
                        replayConfig,
                        _crossEntityResolver);
                    StateContainer.NotifyMutated();
                }
                catch (Exception ex)
                {
                    Core.Logging.MetaLog.Error(
                        $"[EntityReplay] methodId={broadcast.MethodId} on entity '{EntityId}' failed: {ex.Message}",
                        ex);
                }
            }

            public void Dispose()
            {
                if (_broadcastHandler != null && Network != null)
                {
                    try { Network.OnBroadcast -= _broadcastHandler; } catch { }
                    _broadcastHandler = null;
                }
                foreach (var apiClient in ApiClients.Values)
                {
                    if (apiClient is IDisposable disposable)
                    {
                        try { disposable.Dispose(); } catch { }
                    }
                }
                ApiClients.Clear();
                if (Network is IDisposable networkDisposable)
                {
                    try { networkDisposable.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Subscription wrapper for INetwork.OnBroadcast events. 0.24.0+ filters by
        /// client-local <c>MethodId</c> — the wire no longer carries service/method names.
        /// </summary>
        private class NetworkMethodSubscription : IMethodSubscription
        {
            private readonly INetwork _network;
            private readonly ushort _methodId;
            private readonly Action<MethodReplayedContext> _handler;
            private bool _disposed;

            public NetworkMethodSubscription(
                INetwork network,
                ushort methodId,
                Action<MethodReplayedContext> handler)
            {
                _network = network;
                _methodId = methodId;
                _handler = handler;
                _network.OnBroadcast += HandleBroadcast;
            }

            private void HandleBroadcast(NetworkBroadcast broadcast)
            {
                if (_disposed) return;
                if (broadcast.MethodId != _methodId) return;

                var context = new MethodReplayedContext
                {
                    MethodId = broadcast.MethodId,
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
        /// Typed subscription wrapper. 0.24.0+ filters by client-local <c>MethodId</c>.
        /// </summary>
        private class NetworkMethodSubscription<TArgs> : IMethodSubscription
        {
            private readonly INetwork _network;
            private readonly ushort _methodId;
            private readonly Action<TArgs> _handler;
            private readonly IMetaSerializer _serializer;
            private bool _disposed;

            public NetworkMethodSubscription(
                INetwork network,
                ushort methodId,
                Action<TArgs> handler,
                IMetaSerializer serializer)
            {
                _network = network;
                _methodId = methodId;
                _handler = handler;
                _serializer = serializer;
                _network.OnBroadcast += HandleBroadcast;
            }

            private void HandleBroadcast(NetworkBroadcast broadcast)
            {
                if (_disposed) return;
                if (broadcast.MethodId != _methodId) return;

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
