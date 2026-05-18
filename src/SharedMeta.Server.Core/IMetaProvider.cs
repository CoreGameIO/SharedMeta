using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;
using SharedMeta.Server;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Context provided to IMetaProvider by EntityGrain.
    /// </summary>
    public interface IMetaProviderContext
    {
        /// <summary>The entity ID.</summary>
        string EntityId { get; }

        /// <summary>Serializer for payloads.</summary>
        IMetaSerializer Serializer { get; }

        /// <summary>Orleans grain factory for cross-grain calls.</summary>
        IGrainFactory GrainFactory { get; }

        /// <summary>Logger for the provider. Null if not configured.</summary>
        ILogger? Logger { get; }

        /// <summary>
        /// Persisted state for named randoms declared via [NamedRandom] on the state.
        /// Packed list of MetaRandom in attribute declaration order. Null = no named randoms persisted yet.
        /// </summary>
        byte[]? NamedRandomsBytes => null;
    }

    /// <summary>
    /// Result of handling an RPC call.
    /// </summary>
    public class HandleCallResult
    {
        /// <summary>
        /// The canonical operation payload (return bytes, replay payload, patch / state,
        /// random deltas, executed config version, method-level error, nested triggers).
        /// </summary>
        public MetaOperation Response { get; set; } = new();

        /// <summary>Broadcasts to distribute to subscribers.</summary>
        public List<EntityBroadcast> Broadcasts { get; set; } = new();

        /// <summary>
        /// Cross-entity calls made during this operation.
        /// Used by EntityGrain/SessionManager for broadcast suppression and desync validation.
        /// </summary>
        public List<CrossEntityCallInfo>? CrossEntityCalls { get; set; }

        /// <summary>
        /// If true, EntityGrain must persist state immediately regardless of PersistencePolicy.
        /// Propagated from DispatchResult.ForcePersist (set by [MetaMethod(ForcePersist = true)]).
        /// </summary>
        public bool ForcePersist { get; set; }
    }

    /// <summary>
    /// Result of handling an external event.
    /// </summary>
    public class HandleEventResult
    {
        /// <summary>Broadcasts to distribute to subscribers.</summary>
        public List<EntityBroadcast> Broadcasts { get; set; } = new();
    }

    /// <summary>
    /// Business logic provider for EntityGrain.
    /// All game-specific logic is handled through this interface.
    /// EntityGrain remains a thin wrapper with no knowledge of specific services.
    /// </summary>
    /// <typeparam name="TState">The state type for this entity.</typeparam>
    public interface IMetaProvider<TState> where TState : class, ISharedState, new()
    {
        /// <summary>
        /// Initialize the provider with grain context and state.
        /// Called on grain activation.
        /// </summary>
        /// <param name="context">The grain context.</param>
        /// <param name="state">The entity state (may be new or loaded from storage).</param>
        /// <param name="serverRandomBytes">Persisted server random state (null = create from entityId seed).</param>
        /// <param name="optimisticRandomBytes">Persisted optimistic random state (null = create from entityId seed).</param>
        void Initialize(IMetaProviderContext context, TState state,
            byte[]? serverRandomBytes = null, byte[]? optimisticRandomBytes = null);

        /// <summary>
        /// Handle an RPC call asynchronously.
        /// Service methods may be async (e.g., calling other grains).
        /// </summary>
        /// <param name="call">The RPC call.</param>
        /// <param name="isClientOriginated">
        /// True when invoked directly by a client RPC (<see cref="SharedMeta.Server.Core.Grains.EntityGrain.HandleCallAsync"/>).
        /// False when invoked by another grain via cross-entity (<see cref="SharedMeta.Server.Core.Grains.EntityGrain.HandleCallFromEntityAsync"/>).
        /// The provider uses this to gate methods declared with <c>[MetaMethod(GenerateClientApi = false)]</c>:
        /// only direct client RPC is rejected; cross-entity calls always pass through. Sibling-bypass
        /// dispatches in-process and never reaches this method.
        /// </param>
        /// <param name="requirePatchForFanOut">
        /// 0.22.0+ When <c>true</c>, the provider activates patch tracking even for non-ServerPatch
        /// execution modes — the resulting broadcast carries both <c>ReplayPayload</c> and
        /// <c>PatchBytes</c>, and <c>SessionManagerGrain</c> tailors per-subscriber on fan-out
        /// (modern keeps replay, legacy keeps patch). EntityGrain sets this when its aggregated
        /// force-patch refcount contains the call's <c>(Service, Alias, MethodVersion)</c> tuple.
        /// </param>
        /// <returns>Response and broadcasts to distribute.</returns>
        Task<HandleCallResult> HandleCallAsync(RpcCall call, bool isClientOriginated = true, bool requirePatchForFanOut = false);

        /// <summary>
        /// Handle an external event from a framework service asynchronously.
        /// </summary>
        /// <param name="subscriberInterface">The subscriber interface (e.g., "ILobbySubscriber").</param>
        /// <param name="methodName">The method name (e.g., "OnMatchFound").</param>
        /// <param name="eventData">The serialized event data.</param>
        /// <param name="callerId">Optional caller ID.</param>
        /// <returns>Broadcasts to distribute.</returns>
        Task<HandleEventResult> HandleExternalEventAsync(
            string subscriberInterface,
            string methodName,
            byte[] eventData,
            string? callerId = null);

        /// <summary>
        /// Get the current state bytes for snapshot.
        /// </summary>
        byte[] GetStateBytes();

        /// <summary>
        /// Get the current server random state bytes for persistence.
        /// </summary>
        byte[] GetServerRandomBytes();

        /// <summary>
        /// Get the current optimistic random state bytes for persistence/snapshot.
        /// </summary>
        byte[] GetOptimisticRandomBytes();

        /// <summary>
        /// Get the current named-randoms state bytes (packed list) for persistence/snapshot.
        /// Default returns empty bytes for providers without [NamedRandom] on their state.
        /// </summary>
        byte[] GetNamedRandomsBytes() => System.Array.Empty<byte>();

        /// <summary>
        /// Run state initialization/migration logic.
        /// Called during grain activation with the persisted version number.
        /// Returns the new version to persist (return currentVersion if no migration needed).
        /// </summary>
        Task<int> InitializeStateAsync(int currentVersion);

        /// <summary>
        /// Called when the grain is deactivating.
        /// Allows cleanup of resources.
        /// </summary>
        void OnDeactivating();

        /// <summary>
        /// Access policy for this entity type.
        /// Determines who can subscribe to entities of this type.
        /// </summary>
        EntityAccessPolicy AccessPolicy { get; }

        /// <summary>
        /// Check if a player is authorized to subscribe to this entity.
        /// Called by EntityGrain when AccessPolicy is Authorized.
        /// Generated code routes to service.IsAuthorized(playerId).
        /// </summary>
        Task<bool> CheckAccessAsync(string playerId);

        /// <summary>
        /// Handle a query call. Read-only: dispatches the method but skips
        /// replay recording, broadcast creation, random state, and persistence.
        /// </summary>
        Task<QueryCallResponse> HandleQueryAsync(RpcCall call);

        /// <summary>
        /// Handle a signal call — fire-and-forget, void return, read-only state.
        /// Dispatches the method but skips replay recording, broadcast creation, random state,
        /// persistence, and response generation. No value flows back to the caller.
        /// <para>
        /// Bridge services (<c>[ServerMetaService]</c>) called from within a signal body are
        /// wrapped by their normal <c>{Service}Recorder</c>, but the Recorder writes into
        /// <see cref="SharedMeta.Core.NullServerRecordContext"/> — real side-effects happen,
        /// recording is a no-op.
        /// </para>
        /// </summary>
        Task HandleSignalAsync(RpcCall call) => Task.CompletedTask;

        // Method-level classification hooks (IsQueryMethod / IsSignalMethod / IsOpenAccessQuery /
        // IsClientCallable) intentionally live as `protected virtual` on MetaProviderBase rather
        // than on this interface. They are an internal detail consumed by the provider's own
        // Handle*Async methods — EntityGrain doesn't need to know which methods are queries,
        // signals, or client-callable; it just routes the call and lets the provider validate.

        /// <summary>
        /// Config version for this entity. Returns (0,0) if no config.
        /// Used by EntityGrain to include in subscribe/snapshot responses.
        /// </summary>
        MetaConfigVersion ConfigVersion => default;

        /// <summary>
        /// Initialize config for this entity with the given version.
        /// Called by EntityGrain during activation.
        /// For new entities, version is resolved via IConfigVersionResolver or IMetaConfigProvider.CurrentVersion.
        /// For existing entities, version comes from persisted grain state.
        /// </summary>
        /// <param name="version">The config version to use. (0,0) if no config configured.</param>
        void InitializeConfig(MetaConfigVersion version) { }

        /// <summary>
        /// Resolve the config version appropriate for a specific connecting client's app version.
        /// Called by EntityGrain when building the subscribe snapshot so each subscriber's
        /// SubscribeResponse carries the correct config branch per <c>[MetaConfigVersion]</c> rules.
        /// Default returns <see cref="ConfigVersion"/> (entity's primary pinned version).
        /// </summary>
        MetaConfigVersion ResolveClientConfigVersion(string? clientVersion) => ConfigVersion;

        /// <summary>
        /// Returns true when <paramref name="resolvedClientConfigVersion"/> is high enough for
        /// the entity's current state schema. Called by EntityGrain during SubscribeAsync.
        /// Default returns true (no schema requirements declared).
        /// </summary>
        bool IsClientConfigCompatible(MetaConfigVersion resolvedClientConfigVersion) => true;
    }

    /// <summary>
    /// Factory for creating IMetaProvider instances.
    /// Registered in DI and resolved per-grain.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    public interface IMetaProviderFactory<TState> where TState : class, ISharedState, new()
    {
        /// <summary>
        /// Create a new provider instance.
        /// </summary>
        IMetaProvider<TState> Create();
    }
}
