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

        /// <summary>
        /// Client application version (e.g. "1.4.3"). Optional — when present the server resolves
        /// the config version appropriate for this client via <c>[MetaConfigVersion]</c> rules on
        /// the config class. Absent = use the entity's default pinned config version.
        /// </summary>
        [Id(2), Key(2)] public string? ClientVersion { get; set; }
    }
}
