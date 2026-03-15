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
    }
}
