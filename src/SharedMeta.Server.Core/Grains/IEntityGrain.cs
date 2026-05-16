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
        /// Subscribe to this entity. Returns current state.
        /// <para>
        /// 0.22.0+ <paramref name="forceServerPatchMethods"/> — caller passes the subset of
        /// <see cref="ClientCapabilities.ForceServerPatchMethods"/> applicable to this entity.
        /// The grain refcounts these into <c>_forcePatchMethodRefs</c>; subsequent
        /// <c>HandleCallAsync</c> calls activate patch tracking when the dispatched method is
        /// present in the set so the broadcast carries both replay payload and patch bytes.
        /// Null = pass-through (no force-patch tracking added).
        /// </para>
        /// </summary>
        Task<EntitySnapshot> SubscribeAsync(string playerId, ISessionManagerReference sessionManager, string? clientVersion = null, IReadOnlyList<MethodIdentity>? forceServerPatchMethods = null);

        /// <summary>
        /// Unsubscribe from this entity.
        /// </summary>
        Task UnsubscribeAsync(string playerId);

        /// <summary>
        /// Handle an RPC call from a player.
        /// Broadcasts to ALL EXCEPT caller (caller receives direct response).
        /// </summary>
        Task<EntityCallResult> HandleCallAsync(RpcCall call);

        /// <summary>
        /// Handle a call from another entity (cross-entity call).
        /// Broadcasts to ALL (including caller entity's subscribers).
        /// Used when one entity needs to call another entity's service.
        /// </summary>
        Task<EntityCallResult> HandleCallFromEntityAsync(RpcCall call);

        /// <summary>
        /// 0.22.0+: Fire-and-forget cross-entity call entry point. Marked <c>[OneWay]</c> so the
        /// source grain dispatches and continues without waiting for the target to finish.
        /// Target executes the same body as <see cref="HandleCallFromEntityAsync"/>; the result
        /// is computed but ignored (no reply envelope, no result recording). Errors are logged
        /// inside the target and never surfaced to the source. Used for methods declared with
        /// <c>[MetaMethod(OneWay = true)]</c>.
        /// </summary>
        [OneWay]
        Task HandleCallFromEntityOneWayAsync(RpcCall call);

        /// <summary>
        /// Handle an external event (from framework services like Lobby).
        /// Broadcasts to ALL subscribers.
        /// </summary>
        Task<EntityCallResult> HandleExternalEventAsync(
            string subscriberInterface,
            string methodName,
            byte[] eventData,
            string? callerId = null);

        /// <summary>
        /// Execute a query call (no subscription required).
        /// Read-only: no state sync, no broadcasts, no replay, no sequence number changes.
        /// </summary>
        Task<QueryCallResponse> HandleQueryAsync(RpcCall call);

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
        /// Marked [AlwaysInterleave] to prevent deadlocks in mutual cross-entity reads.
        /// </summary>
        [AlwaysInterleave]
        Task<byte[]?> GetEntityStateAsync();

        /// <summary>
        /// 0.21.0 — admin force-migrate. Drives this entity's state schema up to whatever
        /// schema floor is required by <paramref name="floorClientVersion"/> (resolved via
        /// the config class's <c>[MetaConfigVersion]</c> rules), running each
        /// <c>[MetaStateVersion]</c> step in order with its configured transition config.
        /// Returns <c>true</c> if migration ran and state was persisted; <c>false</c> when
        /// the entity was already at-or-above the floor (no-op).
        /// <para>
        /// Use case: dropping support for an old config branch. Iterate entity IDs of a
        /// given state type from your project's player DB / storage and call this on each.
        /// No subscriber required — works on cold or active entities. Existing active
        /// pins are NOT overwritten (Private/Shared keep their first-subscriber version);
        /// the force-migrate only advances <c>state.Version</c>.
        /// </para>
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
