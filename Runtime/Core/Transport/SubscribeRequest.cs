using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to subscribe to an entity's state changes.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SubscribeRequest
    {
        /// <summary>ID of the entity to subscribe to.</summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>
        /// Type name of the entity state (e.g., "GameState").
        /// Used by server to resolve the correct grain type.
        /// </summary>
        [Id(1), Key(1)] public string StateTypeName { get; set; } = "";
    }
}
