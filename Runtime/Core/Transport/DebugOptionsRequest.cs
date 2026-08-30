using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to set debug options for this session.
    /// Server may ignore if debug API is disabled (production mode).
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class DebugOptionsRequest
    {
        /// <summary>Enable deep desync detection (patch CRC comparison) for this session.</summary>
        [Id(0), Key(0)] public bool DeepDesyncEnabled { get; set; }
    }

    /// <summary>
    /// Response from debug options request.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class DebugOptionsResponse
    {
        [Id(0), Key(0)] public bool Success { get; set; }
        [Id(1), Key(1)] public string? Error { get; set; }
    }
}
