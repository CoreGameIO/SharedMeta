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
        /// The <see cref="MetaConfigVersion"/>s the server actually executed under — index 0 is
        /// the legacy primary config when declared, remaining indices are
        /// <see cref="SharedMeta.Core.ServiceConfigAttribute"/> entries in declaration order. Used
        /// by the client's optimistic / cross-optimistic replay path to pin
        /// <c>Context.Config</c>/<c>Context.Configs</c> to the same branch(es) the server saw,
        /// regardless of the client's own session-resolved versions. Null/empty means "no config
        /// system" — the client falls back to its session-resolved version(s). 0.33.0+ (was a
        /// single scalar <c>ExecutedConfigVersion</c> pre-0.33).
        /// </summary>
        public List<MetaConfigVersion>? ExecutedConfigVersions { get; set; }

        /// <summary>0.26.6+ Server-stamped <see cref="PayloadDebug"/> from <c>MetaOperation.Debug</c>.
        /// Carries entity-seq info, and (when a method is annotated
        /// <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c> and the server detected a CRC
        /// mismatch) <see cref="PayloadDebug.DesyncStateBytes"/> + <see cref="PayloadDebug.DesyncTiming"/>.
        /// </summary>
        public PayloadDebug? Debug { get; set; }
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
        /// The <see cref="MetaConfigVersion"/>s the server actually executed under — index 0 is
        /// the legacy primary config when declared, remaining indices are
        /// <see cref="SharedMeta.Core.ServiceConfigAttribute"/> entries in declaration order. Used
        /// by the client's optimistic / cross-optimistic replay path to pin
        /// <c>Context.Config</c>/<c>Context.Configs</c> to the same branch(es) the server saw,
        /// regardless of the client's own session-resolved versions. Null/empty means "no config
        /// system" — the client falls back to its session-resolved version(s). 0.33.0+ (was a
        /// single scalar <c>ExecutedConfigVersion</c> pre-0.33).
        /// </summary>
        public List<MetaConfigVersion>? ExecutedConfigVersions { get; set; }

        /// <summary>0.26.6+ Server-stamped <see cref="PayloadDebug"/> from <c>MetaOperation.Debug</c>.
        /// Carries entity-seq info, and (when a method is annotated
        /// <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c> and the server detected a CRC
        /// mismatch) <see cref="PayloadDebug.DesyncStateBytes"/> + <see cref="PayloadDebug.DesyncTiming"/>.
        /// </summary>
        public PayloadDebug? Debug { get; set; }
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
        /// The <see cref="MetaConfigVersion"/>s the server actually executed under — index 0 is
        /// the legacy primary config when declared, remaining indices are
        /// <see cref="SharedMeta.Core.ServiceConfigAttribute"/> entries in declaration order. Used
        /// by the client's optimistic / cross-optimistic replay path to pin
        /// <c>Context.Config</c>/<c>Context.Configs</c> to the same branch(es) the server saw,
        /// regardless of the client's own session-resolved versions. Null/empty means "no config
        /// system" — the client falls back to its session-resolved version(s). 0.33.0+ (was a
        /// single scalar <c>ExecutedConfigVersion</c> pre-0.33).
        /// </summary>
        public List<MetaConfigVersion>? ExecutedConfigVersions { get; set; }

        /// <summary>
        /// 0.26.6+ Server-stamped <see cref="PayloadDebug"/> from <c>MetaOperation.Debug</c>.
        /// Carries <see cref="PayloadDebug.Info"/> (e.g. <c>"seq=N"</c>) plus, when a method
        /// is annotated <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c> and the server
        /// detected a CRC mismatch, <see cref="PayloadDebug.DesyncStateBytes"/> +
        /// <see cref="PayloadDebug.DesyncTiming"/>.
        /// </summary>
        public PayloadDebug? Debug { get; set; }
    }
}
