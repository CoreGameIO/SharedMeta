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
        /// Register a service configuration.
        /// Called by generated code or during DI setup.
        /// </summary>
        void RegisterService<TApiClient>(MetaServiceConfig config);

        /// <summary>
        /// Get the resolved config for a connected entity.
        /// Returns null if the entity is not connected or has no config.
        /// </summary>
        TConfig? GetEntityConfig<TConfig>(string entityId) where TConfig : class;

        /// <summary>
        /// Subscribe to a method being replayed from server broadcast.
        /// Use this to react to service events (e.g., OnMatchFound from LobbySubscriber).
        /// </summary>
        /// <param name="entityId">Entity ID to subscribe on</param>
        /// <param name="serviceName">Service interface name (e.g., "ILobbySubscriber")</param>
        /// <param name="methodName">Method name (e.g., "OnMatchFound")</param>
        /// <param name="handler">Handler to invoke when the method is replayed</param>
        /// <returns>Subscription handle - dispose to unsubscribe</returns>
        IMethodSubscription OnMethodReplayed(string entityId, string serviceName, string methodName, Action<MethodReplayedContext> handler);

        /// <summary>
        /// Subscribe to a method being replayed with strongly-typed arguments.
        /// </summary>
        IMethodSubscription OnMethodReplayed<TArgs>(string entityId, string serviceName, string methodName, Action<TArgs> handler);
    }

    /// <summary>
    /// Configuration for a meta service API client.
    /// Used by code generators to register service configurations.
    /// </summary>
    public class MetaServiceConfig
    {
        /// <summary>
        /// The service interface name (e.g., "IProfileService")
        /// Used for routing local invocations.
        /// </summary>
        public string ServiceName { get; init; } = null!;

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
        /// Null if no config is configured.
        /// </summary>
        public Type? ConfigType { get; init; }

        /// <summary>
        /// Factory to create the default config instance on the client.
        /// Config is shared code — no need to send bytes from server.
        /// </summary>
        public Func<object>? ConfigFactory { get; init; }

        /// <summary>
        /// Factory to create the API client.
        /// Parameters: (network, serializer, state, modeProvider, diagnostics, crossEntityResolver, optimisticRandom, config, namedRandoms)
        /// namedRandoms: positional list of MetaRandom declared via [NamedRandom] on the state, or null if state has none.
        /// </summary>
        public Func<INetwork, IMetaSerializer, object, IExecutionModeProvider, IDesyncDiagnostics?, ICrossEntityResolver?, MetaRandom?, object?, IReadOnlyList<MetaRandom>?, object> ApiClientFactory { get; init; } = null!;

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
    }
}
