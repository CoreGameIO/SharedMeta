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
        /// Client state-type id — index into the client signature's <c>KnownStateTypes</c>. The
        /// server translates it to the state type that selects the entity grain.
        /// </summary>
        [Id(1), Key(1)] public ushort StateTypeId { get; set; }

        /// <summary>
        /// Client application version (e.g. "1.4.3"). Optional — when present the server resolves
        /// the config version appropriate for this client via <c>[MetaConfigVersion]</c> rules on
        /// the config class. Absent = use the entity's default pinned config version.
        /// </summary>
        [Id(2), Key(2)] public string? ClientVersion { get; set; }
    }
}
