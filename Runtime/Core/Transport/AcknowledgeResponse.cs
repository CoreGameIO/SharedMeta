using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response to acknowledge request.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AcknowledgeResponse
    {
        /// <summary>True if acknowledgment was processed.</summary>
        [Id(0), Key(0)] public bool Success { get; set; }

        /// <summary>Error message if acknowledgment failed.</summary>
        [Id(1), Key(1)] public string? Error { get; set; }
    }
}
