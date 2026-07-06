using System;
using System.Collections.Generic;
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
    /// Result of handling an RPC call. Payload fields are <see cref="ReadOnlyMemory{T}"/>
    /// over GC-managed byte[] (fresh allocation per RPC via <c>PackForExternalUsage</c>).
    /// Orleans honors class-level <c>[Immutable]</c> markers on the wrapping wire DTOs
    /// (EntityCallResult, EntityBroadcast) so in-silo hops share by reference, not deep-copy.
    /// </summary>
    public readonly struct HandleCallResult
    {
        /// <summary>Pre-serialized MetaOperation for the originating caller's RPC response.</summary>
        public ReadOnlyMemory<byte> ResponseBytes { get; init; }

        /// <summary>Method return value (shortcut so cross-entity callers can read it without
        /// deserializing <see cref="ResponseBytes"/>). Same bytes appear inside ResponseBytes.</summary>
        public ReadOnlyMemory<byte> ResultBytes { get; init; }

        /// <summary>Pre-serialized broadcast for modern (replay-eligible) subscribers. Default
        /// (uninitialized) when no broadcast is produced.</summary>
        public ReadOnlyMemory<byte> BroadcastReplayBytes { get; init; }

        /// <summary>Pre-serialized broadcast for legacy (force-patch) subscribers. Default when
        /// no force-patch tailoring was required for this call.</summary>
        public ReadOnlyMemory<byte> BroadcastPatchBytes { get; init; }

        /// <summary>
        /// Cross-entity calls made during this operation. Used by EntityGrain and replay clients
        /// for broadcast suppression / desync validation.
        /// </summary>
        public List<CrossEntityOperationInfo>? CrossEntityCalls { get; init; }

        /// <summary>
        /// If true, EntityGrain must persist state immediately regardless of PersistencePolicy.
        /// Propagated from <c>DispatchResult.ForcePersist</c> (set by <c>[MetaMethod(ForcePersist = true)]</c>).
        /// </summary>
        public bool ForcePersist { get; init; }

        /// <summary>Top-level error (provider could not dispatch). Method-body errors are
        /// embedded in the serialized ResponseBytes via <c>MetaOperation.Error</c>.</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// Result of handling an external event. Single pre-serialized broadcast — external events
    /// don't go through force-patch tailoring so one variant suffices. Pool ownership for the
    /// outgoing bytes is exposed via <c>MetaProviderBase.TakeOutgoingEventBroadcast</c>.
    /// </summary>
    public readonly struct HandleEventResult
    {
        public ReadOnlyMemory<byte> BroadcastBytes { get; init; }
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
        /// When true, activates patch tracking even for non-ServerPatch execution modes —
        /// the resulting broadcast carries both <c>ReplayPayload</c> and <c>PatchBytes</c>,
        /// and the per-subscriber tailor strips one on fan-out (modern keeps replay, legacy
        /// keeps patch). Set by EntityGrain when its force-patch refcount contains the call.
        /// </param>
        /// <returns>Response and broadcasts to distribute.</returns>
        /// <param name="entitySequenceNumber">
        /// 0.24.0+ The entity grain's seq value for this call (advanced by ++ in EntityGrain.HandleCallAsync
        /// BEFORE invoking the provider). Stamped onto <c>MetaOperation.Debug</c> for response /
        /// broadcast so client-side desync diagnostics can see exactly which entity-seq the server
        /// ran the body against — invaluable for tracing ordering races between RPC results and
        /// preceding broadcasts. Zero when invoked from a context that has no entity seq
        /// (legacy callers, tests).
        /// </param>
        ValueTask<HandleCallResult> HandleCallAsync(RpcCall call, bool isClientOriginated = true, bool requirePatchForFanOut = false, long entitySequenceNumber = 0);

        /// <summary>
        /// Handle an external event from a framework service asynchronously.
        /// 0.24.0+ ushort identifier (from <see cref="SharedMeta.Core.Framework.FrameworkMethodIds"/>
        /// for framework subscribers) replaces the legacy <c>(subscriberInterface, methodName)</c>
        /// string pair on the wire and at the dispatch site.
        /// </summary>
        /// <param name="methodId">Framework subscriber method id (see <see cref="SharedMeta.Core.Framework.FrameworkMethodIds"/>).</param>
        /// <param name="eventData">The serialized event data.</param>
        /// <param name="callerId">Optional caller ID.</param>
        /// <returns>Broadcasts to distribute.</returns>
        ValueTask<HandleEventResult> HandleExternalEventAsync(
            ushort methodId,
            ReadOnlyMemory<byte> eventData,
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
        ValueTask<QueryCallResponse> HandleQueryAsync(RpcCall call);

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
        ValueTask HandleSignalAsync(RpcCall call) => default;

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
        /// <paramref name="callerClientVersion"/> (0.33.0+) lets implementations also gate on
        /// <c>[ServiceConfig]</c>-linked conditions, each resolved independently.
        /// Default returns true (no schema requirements declared).
        /// </summary>
        bool IsClientConfigCompatible(MetaConfigVersion resolvedClientConfigVersion, string? callerClientVersion) => true;
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
