using Orleans;
using SharedMeta.Core;
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
        Task<EntitySnapshot> SubscribeAsync(string playerId, ISessionManagerReference sessionManager);

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
