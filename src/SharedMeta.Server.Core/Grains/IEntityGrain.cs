using System.Collections.Generic;
using Orleans;
using Orleans.Concurrency;
using SharedMeta.Core;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Session;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Non-generic base interface for entity grains.
    /// Used for calls that don't require knowing the state type.
    /// </summary>
    public interface IEntityGrainBase : IGrainWithStringKey
    {
        /// <summary>
        /// Subscribes to this entity. Returns the current snapshot.
        /// <para>
        /// <paramref name="clientSignatureHash"/> = caller's negotiated signature hash (0 =
        /// no negotiation, no annotation). The grain resolves
        /// <see cref="ClientSignatureAnnotated"/> locally via
        /// <see cref="IClientSignatureRegistry.TryGetAnnotatedAsync"/> and refcounts
        /// force-patch contributions so subsequent dispatches activate patch tracking
        /// when needed.
        /// </para>
        /// </summary>
        Task<EntitySnapshot> SubscribeAsync(string playerId, ISessionManagerReference sessionManager, string? clientVersion = null, ulong clientSignatureHash = 0);

        /// <summary>
        /// 0.24.0+ Client-driven reclaim path. The client claims a previously-held subscription
        /// (entity id + last-known entity sequence number) on Resume. The grain verifies its
        /// persisted state and returns one of three verdicts:
        /// <list type="bullet">
        /// <item><c>Continued</c> — subscriber present, sequence numbers match, signature hash
        /// unchanged. No state shipped; client keeps its current view, server refreshes the live
        /// <see cref="ISessionManagerReference"/> binding so broadcasts resume flowing.</item>
        /// <item><c>Refreshed</c> — subscriber missing (eviction / never persisted) or sequence
        /// gap detected. Behaves like a full <see cref="SubscribeAsync"/>: migration, schema
        /// gate, force-patch refcounts, snapshot. State + randoms + config version returned.</item>
        /// <item><c>Failed</c> — access policy denied or schema incompatible. Reported with
        /// <c>FailureReason</c>; client surfaces to game via IMetaSubscriptionRecoveryHandler.</item>
        /// </list>
        /// Replaces the older server-driven re-subscribe-from-persisted-state flow; subscriptions
        /// are now owned by the client (single source of truth), the entity grain is the verifier.
        /// </summary>
        Task<SubscriptionResult> ReclaimSubscriptionAsync(string playerId, ISessionManagerReference sessionManager, long lastKnownEntitySequence, string? clientVersion = null, ulong clientSignatureHash = 0);

        /// <summary>
        /// Unsubscribe from this entity.
        /// </summary>
        Task UnsubscribeAsync(string playerId);

        /// <summary>
        /// Handle an RPC call from a player.
        /// Broadcasts to ALL EXCEPT caller (caller receives direct response).
        /// </summary>
        ValueTask<EntityCallResult> HandleCallAsync(RpcCall call);

        /// <summary>
        /// Handle a call from another entity (cross-entity call).
        /// Broadcasts to ALL (including caller entity's subscribers).
        /// Returns a slim <see cref="CrossEntityCallReturn"/> — source grain only needs
        /// ResultBytes / EntitySequenceNumber / Error, not the full pre-serialized
        /// MetaOperation that <c>HandleCallAsync</c> sends to SessionManager.
        /// </summary>
        ValueTask<CrossEntityCallReturn> HandleCallFromEntityAsync(RpcCall call);

        /// <summary>
        /// Fire-and-forget cross-entity call entry. <c>[OneWay]</c> suppresses the reply
        /// envelope so the source grain dispatches and continues. Target executes the same
        /// body as <see cref="HandleCallFromEntityAsync"/>; result discarded, errors logged
        /// on target. Used for <c>[MetaMethod(Mode = ExecutionMode.Notification)]</c>.
        /// </summary>
        [OneWay]
        Task HandleCallFromEntityOneWayAsync(RpcCall call);

        /// <summary>
        /// Execute a query call (no subscription required).
        /// Read-only: no state sync, no broadcasts, no replay, no sequence number changes.
        /// </summary>
        ValueTask<QueryCallResponse> HandleQueryAsync(RpcCall call);

        /// <summary>
        /// Execute a signal call (fire-and-forget, void return).
        /// Read-only body: no state sync, no broadcasts, no replay, no sequence number changes,
        /// no persistence. Server-side errors are logged, not propagated — the caller never
        /// sees a response (by contract). Marked <c>[OneWay]</c> so Orleans does not even send
        /// an ACK envelope back to the caller grain.
        /// </summary>
        [OneWay]
        Task HandleSignalAsync(RpcCall call);

        /// <summary>
        /// Get the current serialized state of this entity (read-only).
        /// Returns null if the entity hasn't been activated or has no state.
        /// </summary>
        Task<byte[]?> GetEntityStateAsync();

        /// <summary>
        /// Admin force-migrate: drives state schema up to the floor required by
        /// <paramref name="floorClientVersion"/>. No subscriber required — works on cold or
        /// active entities. Use case: dropping support for an old config branch — iterate
        /// known entity IDs and call on each. Active pins are not overwritten; only
        /// <c>state.Version</c> advances.
        /// </summary>
        Task<bool> ForceMigrateToFloorAsync(string floorClientVersion);
    }

    /// <summary>
    /// Generic entity grain interface.
    /// Extends IEntityGrainBase with type parameter for Orleans grain registration.
    /// </summary>
    /// <typeparam name="TState">The state type for this entity.</typeparam>
    public interface IEntityGrain<TState> : IEntityGrainBase
        where TState : class, ISharedState, new()
    {
        // All methods are inherited from IEntityGrainBase.
        // This generic interface exists for Orleans grain type registration.
    }
}
