using System;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Kind of desync detected by the client. Bit flags so a single report
    /// can carry multiple mismatches for the same RPC.
    /// </summary>
    [Flags]
    public enum DesyncMismatchKind
    {
        None   = 0,
        Patch  = 1,  // PatchCrc differs (deep desync)
        Result = 2,  // RPC return value differs
        Random = 4,  // Random scroll delta differs
    }

    /// <summary>
    /// Client follow-up after a desync was detected. Carries the data the
    /// server needs to log/store the mismatch. Result and Random kinds embed
    /// both server and client values directly (no server-side cache needed);
    /// Patch kind only carries the client patch (server patch is cached on the handler).
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class DesyncReportRequest
    {
        [Id(0), Key(0)] public string EntityId { get; set; } = "";
        [Id(1), Key(1)] public string ServiceName { get; set; } = "";
        [Id(2), Key(2)] public string MethodName { get; set; } = "";
        [Id(3), Key(3)] public byte[] ArgsBytes { get; set; } = Array.Empty<byte>();
        /// <summary>Client patch bytes (Patch mismatch only).</summary>
        [Id(4), Key(4)] public byte[] ClientPatchBytes { get; set; } = Array.Empty<byte>();
        /// <summary>Bitmask of <see cref="DesyncMismatchKind"/>.</summary>
        [Id(5), Key(5)] public int MismatchKind { get; set; }
        /// <summary>Server-returned result bytes (Result mismatch only).</summary>
        [Id(6), Key(6)] public byte[] ServerResultBytes { get; set; } = Array.Empty<byte>();
        /// <summary>Locally re-executed result bytes (Result mismatch only).</summary>
        [Id(7), Key(7)] public byte[] LocalResultBytes { get; set; } = Array.Empty<byte>();
        /// <summary>Server random scroll delta (Random mismatch only).</summary>
        [Id(8), Key(8)] public long ServerRandomDelta { get; set; }
        /// <summary>Local random scroll delta (Random mismatch only).</summary>
        [Id(9), Key(9)] public long LocalRandomDelta { get; set; }
    }

    /// <summary>
    /// Server response to a desync report follow-up.
    /// Status: "stored" | "disabled" | "no-server-patch" | "error"
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class DesyncReportResponse
    {
        [Id(0), Key(0)] public string Status { get; set; } = "";
        [Id(1), Key(1)] public string? Error { get; set; }
    }
}
