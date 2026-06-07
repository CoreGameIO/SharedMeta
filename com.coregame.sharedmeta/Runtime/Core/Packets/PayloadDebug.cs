using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core
{
    /// <summary>
    /// Optional debug information carried alongside RPC calls and operations.
    /// Generated when <c>RuntimeMetaConfig.EnablePayloadDebug</c> is true, or
    /// populated per-call by codegen for opt-in debug features (e.g.
    /// <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c>).
    /// <para>
    /// Lives on <c>RpcCall.Debug</c> (request) and <c>MetaOperation.Debug</c> (response /
    /// broadcast) — same struct, both directions. The framework deliberately uses this
    /// single piggyback slot for all debug-channel data so hot-path transport DTOs stay
    /// free of debug-only fields.
    /// </para>
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class PayloadDebug
    {
        /// <summary>
        /// Free-form per-payload-item debug strings (e.g. argument labels, replay markers).
        /// </summary>
        [Id(0), Key(0)] public List<string> PayloadItemInfo { get; set; } = new();

        /// <summary>
        /// 0.26.6+ Client's FNV-1a CRC over the serialized entity state captured BEFORE the
        /// local-side execution that precedes the call (Optimistic / CrossOptimistic local
        /// pass, or Server-mode pre-replay state). Set by generated <c>*ApiClient</c> for
        /// methods annotated <c>[MetaMethod(DeepStateCheck = SnapshotTiming.Before|Both)]</c>.
        /// Travels on <c>RpcCall.Debug</c>. <c>0</c> = not requested.
        /// </summary>
        [Id(1), Key(1)] public uint PreStateCrc { get; set; }

        /// <summary>
        /// 0.26.6+ Client's FNV-1a CRC over the serialized state AFTER the local-side
        /// execution. Pair to <see cref="PreStateCrc"/> for the After/Both timings.
        /// </summary>
        [Id(2), Key(2)] public uint PostStateCrc { get; set; }

        /// <summary>
        /// 0.26.6+ Server-side full serialized state, populated only when the server
        /// detected a CRC mismatch during a <c>[MetaMethod(DeepStateCheck = X)]</c> check.
        /// Travels back on <c>MetaOperation.Debug</c>. Empty / default when no mismatch.
        /// Paired with <see cref="DesyncTiming"/> which says which timing failed.
        /// </summary>
        [Id(3), Key(3), MemoryPackAllowSerialize] public ReadOnlyMemory<byte> DesyncStateBytes { get; set; }

        /// <summary>
        /// 0.26.6+ Which snapshot timing's check failed when <see cref="DesyncStateBytes"/>
        /// is populated. <see cref="SnapshotTiming.None"/> = no desync (the bytes field is
        /// empty too). Otherwise exactly <c>Before</c> or <c>After</c>.
        /// </summary>
        [Id(4), Key(4)] public SnapshotTiming DesyncTiming { get; set; }

        /// <summary>
        /// 0.26.6+ Free-form server-stamped diagnostic info (e.g. <c>"seq=N"</c> for
        /// entity-sequence). Migrated from the former <c>MetaOperation.Debug</c> string
        /// when the field became <see cref="PayloadDebug"/>-typed.
        /// </summary>
        [Id(5), Key(5)] public string? Info { get; set; }
    }
}
