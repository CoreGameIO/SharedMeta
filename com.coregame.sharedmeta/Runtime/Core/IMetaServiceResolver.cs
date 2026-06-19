using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Network;
using SharedMeta.Core.Random;

namespace SharedMeta.Core
{
    /// <summary>
    /// Resolves and connects to meta services by entity ID.
    /// Provides a DI-friendly way to get typed API clients.
    /// </summary>
    public interface IMetaServiceResolver
    {
        /// <summary>
        /// Get or create a connected API client for the specified entity.
        /// Auto-creates the entity on server if it doesn't exist.
        /// </summary>
        /// <typeparam name="TApiClient">The API client type (e.g., ProfileServiceApiClient)</typeparam>
        /// <param name="entityId">The entity ID to connect to</param>
        /// <returns>A connected and configured API client</returns>
        Task<TApiClient> GetServiceAsync<TApiClient>(string entityId) where TApiClient : class;

        /// <summary>
        /// Disconnect from an entity.
        /// </summary>
        /// <param name="entityId">Entity to disconnect from</param>
        Task DisconnectAsync(string entityId);

        /// <summary>
        /// Get the current state for a connected entity.
        /// </summary>
        /// <typeparam name="TState">State type</typeparam>
        /// <param name="entityId">Entity ID</param>
        TState GetState<TState>(string entityId) where TState : class, ISharedState;

        /// <summary>
        /// Synchronous, allocation-free accessor for an already-resolved API client. Returns true and
        /// the cached client when the entity is subscribed and that client type was already created by
        /// a prior <see cref="GetServiceAsync{TApiClient}"/> call; otherwise false. Never subscribes,
        /// never creates the client, never allocates a Task. Use on hot paths where the entity is known
        /// to be subscribed; use <see cref="GetServiceAsync{TApiClient}"/> for the first call.
        /// </summary>
        /// <typeparam name="TApiClient">The API client type</typeparam>
        /// <param name="entityId">The entity ID</param>
        /// <param name="api">The cached client when found; otherwise null</param>
        bool TryGetService<TApiClient>(string entityId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TApiClient? api) where TApiClient : class;

        /// <summary>
        /// Register a service configuration.
        /// Called by generated code or during DI setup.
        /// </summary>
        void RegisterService<TApiClient>(MetaServiceConfig config);

        /// <summary>
        /// Register a config provider for <typeparamref name="TConfig"/>. The resolver invokes
        /// <see cref="SharedMeta.Client.IClientMetaConfigProvider{TConfig}.GetConfigAsync"/>
        /// during entity subscribe to materialize the config for the version pinned by the
        /// server. Subsequent calls overwrite the previously registered provider for the same
        /// type — register your provider before <c>RegisterAllServices()</c> if you want it to
        /// take effect over the generator's default <see cref="SharedMeta.Client.StaticConfigProvider{TConfig}"/>.
        /// </summary>
        void RegisterConfigProvider<TConfig>(SharedMeta.Client.IClientMetaConfigProvider<TConfig> provider) where TConfig : class;

        /// <summary>
        /// Register a config provider only if no provider has been registered for
        /// <typeparamref name="TConfig"/> yet. Used by generator-emitted
        /// <c>Add{Service}Services()</c> to install a default provider without clobbering a
        /// user-supplied one.
        /// </summary>
        void TryRegisterConfigProvider<TConfig>(SharedMeta.Client.IClientMetaConfigProvider<TConfig> provider) where TConfig : class;

        /// <summary>
        /// Debug/dev command: wipe every registered config provider's cache (e.g. the on-disk
        /// <see cref="SharedMeta.Client.FileConfigCache{TConfig}"/>) so the next entity subscribe
        /// re-downloads. Use after re-publishing a config under the same version during
        /// development. Providers without a clearable cache are skipped. No-op when no clearable
        /// provider is registered.
        /// </summary>
        void ClearConfigCaches();

        /// <summary>
        /// Get the resolved config for a connected entity.
        /// Returns null if the entity is not connected or has no config.
        /// </summary>
        TConfig? GetEntityConfig<TConfig>(string entityId) where TConfig : class;

        /// <summary>
        /// 0.20.0: Resolve a client-side sibling service impl for a given interface type. Used
        /// by sibling-service code paths during client-side replay/optimistic execution: when
        /// user code calls <c>Get{Iface}SiblingAsync()</c> or <c>GetI{Iface}(self)</c> on the
        /// client, the framework asks this method for a transient impl bound to the supplied
        /// <paramref name="metaContext"/>. Returns null when the resolver doesn't have a service
        /// registered for the interface (typed factory generated for every <c>[MetaServiceImpl]</c>
        /// — no reflection).
        /// </summary>
        object? ResolveClientSibling(string entityId, Type interfaceType, object metaContext);

        /// <summary>
        /// Subscribe to a method being replayed from server broadcast. 0.24.0+ keyed by
        /// client-local <c>MethodId</c> from <c>GameMethodIds</c> /
        /// <see cref="SharedMeta.Core.Framework.FrameworkMethodIds"/> — the wire no longer
        /// carries service/method names. Use to react to service events (e.g.
        /// <c>FrameworkMethodIds.ILobbySubscriber_OnMatchFound</c>).
        /// </summary>
        /// <param name="entityId">Entity ID to subscribe on</param>
        /// <param name="methodId">Client-local method id from GameMethodIds / FrameworkMethodIds</param>
        /// <param name="handler">Handler to invoke when the method is replayed</param>
        /// <returns>Subscription handle - dispose to unsubscribe</returns>
        IMethodSubscription OnMethodReplayed(string entityId, ushort methodId, Action<MethodReplayedContext> handler);

        /// <summary>
        /// Subscribe to a method being replayed with strongly-typed arguments.
        /// </summary>
        IMethodSubscription OnMethodReplayed<TArgs>(string entityId, ushort methodId, Action<TArgs> handler);
    }

    /// <summary>
    /// Configuration for a meta service API client.
    /// Used by code generators to register service configurations.
    /// Declared as a <c>record</c> (i.e. record class — the C# 9 form, equivalent to the C# 10
    /// explicit <c>record class</c>) so consumers can use <c>with</c> expressions to clone a
    /// generator-emitted base config and override individual fields without hand-copying every
    /// property. Adding a new field to the framework no longer breaks call sites that clone.
    /// </summary>
    public record MetaServiceConfig
    {
        /// <summary>
        /// The service interface name (e.g., "IProfileService")
        /// Used for routing local invocations.
        /// </summary>
        public string ServiceName { get; init; } = null!;

        /// <summary>
        /// 0.24.0+ Method ids (client-local indices from <c>GameMethodIds</c>) that this service
        /// handles. The resolver consults this when routing inbound broadcasts: a broadcast whose
        /// <c>MethodId</c> is in this set is dispatched by the per-service <c>ApiClient</c>; a
        /// broadcast that doesn't match any local service's set falls through to
        /// <see cref="EntityReplayDispatcher"/>. Replaces the legacy <c>ServiceName</c> string
        /// match — the wire no longer carries service/method names.
        /// </summary>
        public IReadOnlyList<ushort> MethodIds { get; init; } = System.Array.Empty<ushort>();

        /// <summary>
        /// The API client type (e.g., typeof(ProfileServiceApiClient))
        /// </summary>
        public Type ApiClientType { get; init; } = null!;

        /// <summary>
        /// The state type used by this service
        /// </summary>
        public Type StateType { get; init; } = null!;

        /// <summary>
        /// The service implementation type for local replay
        /// </summary>
        public Type LocalServiceType { get; init; } = null!;

        /// <summary>
        /// The config type for this service (from [MetaService(ConfigType=...)] or DefaultConfig).
        /// Null if no config is configured. The resolver looks up an
        /// <see cref="SharedMeta.Client.IClientMetaConfigProvider{TConfig}"/> registered for
        /// this type via <see cref="IMetaServiceResolver.RegisterConfigProvider{TConfig}"/>
        /// to materialize the config when subscribing to entities.
        /// </summary>
        public Type? ConfigType { get; init; }

        /// <summary>
        /// Factory to create the API client.
        /// Parameters: (network, serializer, state, modeProvider, diagnostics, crossEntityResolver, optimisticRandom, config, namedRandoms, configResolver)
        /// namedRandoms: positional list of MetaRandom declared via [NamedRandom] on the state, or null if state has none.
        /// configResolver (0.21.0+): optional function that materializes a config object for a specific
        ///   <see cref="MetaConfigVersion"/>. When the server's <c>ExecutedConfigVersion</c> on a
        ///   broadcast/response differs from the session-pinned version, the client's replay path
        ///   calls this to resolve the right config branch. Wired by <c>MetaServiceResolver</c> to
        ///   <c>EntityConnection.ResolveConfigForBroadcast</c>.
        /// </summary>
        public Func<INetwork, IMetaSerializer, object, IExecutionModeProvider, IDesyncDiagnostics?, ICrossEntityResolver?, MetaRandom?, object?, IReadOnlyList<MetaRandom>?, Func<MetaConfigVersion, object?>?, object> ApiClientFactory { get; init; } = null!;

        /// <summary>
        /// Factory to create the local service instance.
        /// Optional in INetwork-based architecture (ApiClient creates service internally).
        /// </summary>
        public Func<object>? LocalServiceFactory { get; init; }

        /// <summary>
        /// Factory to create the local invoker.
        /// Optional in INetwork-based architecture (ApiClient handles local replay internally).
        /// </summary>
        public Func<Func<object>, LocalMethodInvoker>? LocalInvokerFactory { get; init; }

        /// <summary>
        /// Factory to connect client to entity with typed state.
        /// Eliminates reflection for ConnectAsync call.
        /// Legacy - not used in INetwork-based architecture.
        /// </summary>
        public Func<object, string, Task>? ConnectFactory { get; init; }

        /// <summary>
        /// Additional invoker factories for subscriber interfaces.
        /// Key is the service/interface name (e.g., "ILobbySubscriber").
        /// Used to register framework subscriber method handlers.
        /// </summary>
        public Dictionary<string, Func<Func<object>, LocalMethodInvoker>>? AdditionalInvokerFactories { get; init; }

        /// <summary>
        /// Callback to refresh state in an existing API client after reconnect.
        /// Parameters: (apiClient, newState, newOptimisticRandom, newNamedRandoms)
        /// </summary>
        public Action<object, object, MetaRandom?, IReadOnlyList<MetaRandom>?>? StateRefresher { get; init; }

        /// <summary>
        /// Factory to wrap a deserialized state into a typed
        /// <see cref="SharedMeta.Client.EntityStateContainer{TState}"/>.
        /// Optional — the resolver falls back to reflection if not provided.
        /// Generator-emitted; allows the resolver to construct a typed container without
        /// knowing TState at compile time.
        /// </summary>
        public Func<object, SharedMeta.Client.IEntityStateContainer>? StateContainerFactory { get; init; }

        /// <summary>
        /// Centralized patch applier so the resolver's entity-level broadcast handler can
        /// apply ServerPatch byte payloads to the shared state without going through any
        /// individual ApiClient. Parameters: (currentState, patchBytes, serializer).
        /// Generator-emitted; absent when the state has no PatchSchema.
        /// </summary>
        public Action<object, byte[], IMetaSerializer>? PatchApplier { get; init; }

        /// <summary>
        /// Foreign-service replay dispatcher (0.14.0+). Lets the resolver's entity-level
        /// broadcast handler reconstruct an Optimistic / Server / CrossOptimistic mutation
        /// for a service the receiver doesn't have an ApiClient for — by instantiating the
        /// shared impl class on the fly and invoking the broadcast's method against the
        /// shared state container.
        /// <para>
        /// Each <see cref="MetaServiceConfig"/> emits the dispatcher for its own service —
        /// the resolver routes incoming broadcasts to the matching config by
        /// <c>ServiceName</c>. Closes the gap where Optimistic / Server broadcasts carry
        /// only replay-context (no state-data), so foreign-service ApiClients used to drop
        /// the mutation entirely.
        /// </para>
        /// <para>
        /// Parameters: (state, methodId, argsBytes, replayContext, callerId, serverTimeTicks,
        /// serializer, optimisticRandom, namedRandoms, config, crossEntityResolver).
        /// 0.24.0+ <c>methodId</c> is the client-local index from <c>GameMethodIds</c>; the
        /// generator emits a <c>switch (methodId)</c> with one case per method's const.
        /// </para>
        /// </summary>
        public Action<object, ushort, byte[], byte[], string?, long, IMetaSerializer, SharedMeta.Core.Random.MetaRandom?, IReadOnlyList<SharedMeta.Core.Random.MetaRandom>?, object?, ICrossEntityResolver?>? EntityReplayDispatcher { get; init; }

        /// <summary>
        /// 0.20.0: Factory that creates a transient impl of this service bound to the calling
        /// client-side <c>MetaContext</c>. Used by sibling-service resolution on the client —
        /// when user code calls <c>Get{Iface}SiblingAsync()</c> or
        /// <c>GetI{Iface}(self).Method(...)</c> during a client-side replay or optimistic
        /// flow, the framework creates a fresh impl with the caller's Context so the impl
        /// reads the same state mirror and randoms as the outer service.
        /// <para>
        /// Generator-emitted as a typed lambda <c>(ctx) =&gt; new {Impl}() { Context = (MetaContext&lt;TState&gt;)ctx }</c>
        /// — no reflection. Wired into <see cref="MetaContext.SiblingServiceResolver"/> on
        /// client-side <c>ClientMetaContext</c> when ApiClient sets up its context.
        /// </para>
        /// </summary>
        public Func<object /* MetaContext */, object>? ClientSiblingFactory { get; init; }
    }
}
