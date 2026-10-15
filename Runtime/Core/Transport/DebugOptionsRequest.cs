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
        /// <summary>
        /// Request deep desync analysis for this player. Stored on the server against the player,
        /// not against the connection, so it survives reconnects and admin tooling sees the same
        /// value. Refused outright when the silo runs the analysis in Off mode.
        /// </summary>
        [Id(0), Key(0)] public bool DeepDesyncEnabled { get; set; }
    }

    /// <summary>
    /// Response from debug options request.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class DebugOptionsResponse
    {
        /// <summary>
        /// True only when the request was actually applied to this session. A silo that cannot
        /// honour the request — Off, or Forced asked to switch off — answers false with a reason
        /// rather than accepting and ignoring it, so "did it work" has one answer.
        /// </summary>
        [Id(0), Key(0)] public bool Success { get; set; }
        [Id(1), Key(1)] public string? Error { get; set; }
    }
}
