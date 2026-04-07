using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core
{
    /// <summary>
    /// Response data from an RPC method execution.
    /// Contains result data and replay information for deterministic replay.
    ///
    /// Layer separation:
    /// - EntitySequenceNumber: in EntityCallResult (Entity layer)
    /// - SessionSequenceNumber: in SessionResponse (Transport layer)
    /// - RpcResponse: pure business data (no sequence numbers)
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RpcResponse
    {
        /// <summary>
        /// Serialized return value from the RPC method.
        /// Null if method returns void.
        /// </summary>
        [Id(0), Key(0)] public byte[]? ResultBytes { get; set; }

        /// <summary>
        /// Serialized replay payload for deterministic replay on client.
        /// Contains any non-deterministic values captured during server execution
        /// (random numbers, timestamps, external service results, etc.)
        /// </summary>
        [Id(1), Key(1)] public byte[]? ReplayPayload { get; set; }

        /// <summary>
        /// Debug information about the call (only in debug mode).
        /// </summary>
        [Id(2), Key(2)] public string? Debug { get; set; }

        /// <summary>
        /// Error message if the call failed.
        /// Null if the call succeeded.
        /// </summary>
        [Id(3), Key(3)] public string? Error { get; set; }

        /// <summary>
        /// Delta of optimistic random ScrollId during this call.
        /// Used by client for desync detection: client's local delta must match server's.
        /// </summary>
        [Id(4), Key(4)] public long RandomScrollDelta { get; set; }

        /// <summary>
        /// Serialized state diff patch for ServerPatch mode.
        /// When present, client applies this patch instead of replaying the method.
        /// </summary>
        [Id(5), Key(5)] public byte[]? PatchBytes { get; set; }

        /// <summary>
        /// Full serialized state for ServerReplace mode.
        /// When present, client replaces its entire state with this instead of replaying or patching.
        /// </summary>
        [Id(6), Key(6)] public byte[]? StateBytes { get; set; }

        /// <summary>
        /// FNV-1a hash of serialized state after method execution (deep desync detection).
        /// Null when deep desync mode is disabled. Client compares its local hash with this.
        /// </summary>
        [Id(7), Key(7)] public uint? DeepDesyncCrc { get; set; }

        /// <summary>True if the call failed (Error is not null).</summary>
        [IgnoreMember] public bool HasError => Error != null;

        /// <summary>True if the call succeeded (Error is null).</summary>
        [IgnoreMember] public bool Success => Error == null;
    }
}
