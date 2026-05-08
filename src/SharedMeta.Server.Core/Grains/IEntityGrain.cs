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
        /// </summary>
        Task<EntitySnapshot> SubscribeAsync(string playerId, ISessionManagerReference sessionManager, string? clientVersion = null);

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
