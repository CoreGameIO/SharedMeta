using System;
using System.Collections.Generic;
using SharedMeta.Core.Packets;

namespace SharedMeta.Core.Network
{
    /// <summary>
    /// Response from a network call with a result value.
    /// </summary>
    public class CallResponse<T>
    {
        /// <summary>
        /// Result from server execution.
        /// </summary>
        public T Result { get; set; } = default!;

        /// <summary>
        /// Server-side replay context for deterministic local execution.
        /// </summary>
        public byte[] ReplayContext { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Triggered operations executed after the main call (if any).
        /// </summary>
        public List<OperationResult>? TriggerOperations { get; set; }

        /// <summary>
        /// Cross-entity call results (for CrossOptimistic desync validation).
        /// </summary>
        public List<CrossEntityOperationInfo>? CrossEntityOperations { get; set; }

        /// <summary>Server time (UTC ticks) from the original call for deterministic replay.</summary>
        public long ServerTimeTicks { get; set; }

        /// <summary>Delta of optimistic random ScrollId during this call (for desync detection).</summary>
        public long RandomScrollDelta { get; set; }

        /// <summary>Serialized state diff patch for ServerPatch mode.</summary>
        public byte[]? PatchBytes { get; set; }
    }

    /// <summary>
    /// Response from a void network call.
    /// </summary>
    public class VoidCallResponse
    {
        /// <summary>
        /// Server-side replay context for deterministic local execution.
        /// </summary>
        public byte[] ReplayContext { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Triggered operations executed after the main call (if any).
        /// </summary>
        public List<OperationResult>? TriggerOperations { get; set; }

        /// <summary>
        /// Cross-entity call results (for CrossOptimistic desync validation).
        /// </summary>
        public List<CrossEntityOperationInfo>? CrossEntityOperations { get; set; }

        /// <summary>Server time (UTC ticks) from the original call for deterministic replay.</summary>
        public long ServerTimeTicks { get; set; }

        /// <summary>Delta of optimistic random ScrollId during this call (for desync detection).</summary>
        public long RandomScrollDelta { get; set; }

        /// <summary>Serialized state diff patch for ServerPatch mode.</summary>
        public byte[]? PatchBytes { get; set; }
    }

    /// <summary>
    /// Response from a network call with raw bytes (for serializer-specific deserialization).
    /// </summary>
    public class ByteCallResponse
    {
        /// <summary>
        /// Raw result bytes from server (caller deserializes based on serializer).
        /// </summary>
        public byte[] ResultBytes { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Server-side replay context for deterministic local execution.
        /// </summary>
        public byte[] ReplayContext { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Triggered operations executed after the main call (if any).
        /// </summary>
        public List<OperationResult>? TriggerOperations { get; set; }

        /// <summary>
        /// Cross-entity call results (for CrossOptimistic desync validation).
        /// </summary>
        public List<CrossEntityOperationInfo>? CrossEntityOperations { get; set; }

        /// <summary>Server time (UTC ticks) from the original call for deterministic replay.</summary>
        public long ServerTimeTicks { get; set; }

        /// <summary>Delta of optimistic random ScrollId during this call (for desync detection).</summary>
        public long RandomScrollDelta { get; set; }

        /// <summary>Serialized state diff patch for ServerPatch mode.</summary>
        public byte[]? PatchBytes { get; set; }
    }
}
