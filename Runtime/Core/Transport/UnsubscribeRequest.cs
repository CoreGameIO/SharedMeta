using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to unsubscribe from an entity.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class UnsubscribeRequest
    {
        /// <summary>ID of the entity to unsubscribe from.</summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>
        /// 0.33.0+ Full type name of the state to unsubscribe from — matching the corresponding
        /// <see cref="SubscribeRequest.StateTypeName"/>. entityId alone is not a unique
        /// subscription key (the server addresses entities by (state type, entityId), so the
        /// same entityId can be shared by independent state types, e.g. Inventory/Profile/Wallet
        /// all keyed by playerId) — the server needs this to remove the right subscription.
        /// </summary>
        [Id(1), Key(1)] public string StateTypeName { get; set; } = "";
    }
}
