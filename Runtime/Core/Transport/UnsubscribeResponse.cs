using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response to unsubscribe request.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class UnsubscribeResponse
    {
        /// <summary>True if unsubscription was successful.</summary>
        [Id(0), Key(0)] public bool Success { get; set; }

        /// <summary>Error message if unsubscription failed.</summary>
        [Id(1), Key(1)] public string? Error { get; set; }
    }
}
