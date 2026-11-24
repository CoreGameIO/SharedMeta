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
        /// Client state-type id of the subscription to end, as in <see cref="SubscribeRequest.StateTypeId"/>.
        /// entityId alone is not a subscription key: state types sharing an entityId
        /// (Inventory/Wallet keyed by playerId) are separate subscriptions.
        /// </summary>
        [Id(1), Key(1)] public ushort StateTypeId { get; set; }
    }
}
