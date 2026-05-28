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
        public List<MetaOperation>? TriggerOperations { get; set; }

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

        /// <summary>Full serialized state for ServerReplace mode.</summary>
        public byte[]? StateBytes { get; set; }

        /// <summary>FNV-1a hash of server state after execution (deep desync detection). Null when disabled.</summary>
        public uint? DeepDesyncCrc { get; set; }

        /// <summary>Per-index scroll deltas for [NamedRandom] streams (positional). Null when none advanced.</summary>
        public long[]? NamedRandomScrollDeltas { get; set; }

        /// <summary>
        /// The <see cref="MetaConfigVersion"/> the server actually executed under. Used by the
        /// client's optimistic / cross-optimistic replay path to pin <c>Context.Config</c> to
        /// the same branch the server saw, regardless of the client's own session-resolved
        /// version. Added in 0.21.0. <c>default(MetaConfigVersion)</c> means "no config system"
        /// — the client falls back to its session-resolved version.
        /// </summary>
        public MetaConfigVersion ExecutedConfigVersion { get; set; }

        /// <summary>0.24.0+ Server-stamped diagnostic string from <c>MetaOperation.Debug</c>
        /// (currently carries entity-seq as <c>"seq=N"</c>). See <c>ByteCallResponse.Debug</c>.</summary>
        public string? Debug { get; set; }
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
        public List<MetaOperation>? TriggerOperations { get; set; }

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

        /// <summary>Full serialized state for ServerReplace mode.</summary>
        public byte[]? StateBytes { get; set; }

        /// <summary>FNV-1a hash of server state after execution (deep desync detection). Null when disabled.</summary>
        public uint? DeepDesyncCrc { get; set; }

        /// <summary>Per-index scroll deltas for [NamedRandom] streams (positional). Null when none advanced.</summary>
        public long[]? NamedRandomScrollDeltas { get; set; }

        /// <summary>
        /// The <see cref="MetaConfigVersion"/> the server actually executed under. Used by the
        /// client's optimistic / cross-optimistic replay path to pin <c>Context.Config</c> to
        /// the same branch the server saw, regardless of the client's own session-resolved
        /// version. Added in 0.21.0. <c>default(MetaConfigVersion)</c> means "no config system"
        /// — the client falls back to its session-resolved version.
        /// </summary>
        public MetaConfigVersion ExecutedConfigVersion { get; set; }

        /// <summary>0.24.0+ Server-stamped diagnostic string from <c>MetaOperation.Debug</c>
        /// (currently carries entity-seq as <c>"seq=N"</c>). See <c>ByteCallResponse.Debug</c>.</summary>
        public string? Debug { get; set; }
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
        public List<MetaOperation>? TriggerOperations { get; set; }

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

        /// <summary>Full serialized state for ServerReplace mode.</summary>
        public byte[]? StateBytes { get; set; }

        /// <summary>FNV-1a hash of server state after execution (deep desync detection). Null when disabled.</summary>
        public uint? DeepDesyncCrc { get; set; }

        /// <summary>Per-index scroll deltas for [NamedRandom] streams (positional). Null when none advanced.</summary>
        public long[]? NamedRandomScrollDeltas { get; set; }

        /// <summary>
        /// The <see cref="MetaConfigVersion"/> the server actually executed under. Used by the
        /// client's optimistic / cross-optimistic replay path to pin <c>Context.Config</c> to
        /// the same branch the server saw, regardless of the client's own session-resolved
        /// version. Added in 0.21.0. <c>default(MetaConfigVersion)</c> means "no config system"
        /// — the client falls back to its session-resolved version.
        /// </summary>
        public MetaConfigVersion ExecutedConfigVersion { get; set; }

        /// <summary>
        /// 0.24.0+ Server-stamped diagnostic string from <c>MetaOperation.Debug</c>. Currently
        /// carries the entity sequence number the server ran the body under (<c>"seq=N"</c>).
        /// Surfaced in desync error reports so client-side ordering races against preceding
        /// broadcasts can be diagnosed by comparing this against the client's locally-tracked
        /// per-entity seq.
        /// </summary>
        public string? Debug { get; set; }
    }
}
