using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Packets
{
    /// <summary>
    /// Canonical representation of one server-side operation: what was called + what came out.
    /// Replaces the historical split between <c>RpcCall</c>'s call-side fields, <c>RpcResponse</c>'s
    /// response-side fields, and the flat fields previously duplicated on <c>EntityBroadcast</c> /
    /// <c>EntityCallResult</c> / <c>SessionOp.MainOperation</c>.
    /// <para>
    /// Routing fields (EntityId, RequestId, ExcludePlayerId, EntitySequenceNumber) stay on the
    /// outer wrappers — only the operation payload lives here. Designed for pooling: parameterless
    /// constructor + <see cref="Reset"/> + no internal references that escape the lifecycle.
    /// </para>
    /// <para>
    /// Field id space is the union of the four merged types' fields, preserved by the wire codec
    /// of each serializer (MemoryPack / MessagePack / Orleans). This is a wire-breaking change
    /// from pre-0.23 — all clients must redeploy in lockstep with the server.
    /// </para>
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer, Immutable]
    public partial class MetaOperation
    {
        // ── Call identification (what was invoked) ────────────────────────
        // 0.24.0+ Wire identification is purely the server-side global method index
        // (stable per server build). The client translates to its own local id via
        // ClientCapabilities.ServerToClientMethodIds at session-connect time. The
        // legacy ServiceName/MethodName/MethodVersion strings were removed from the
        // wire — id slots 0/1/2 are intentionally vacated (never reuse them for new
        // fields; old recorded broadcasts may still carry data there).
        [Id(17), Key(17)] public ushort MethodId { get; set; }
        [Id(3), Key(3)] public byte[]? Payload { get; set; }
        /// <summary>
        /// Originator id. On broadcast ops this is the player whose RPC produced this
        /// operation — subscribers' broadcast-replay path uses it to set <c>Context.CallerId</c>
        /// so the replayed method records the same authorship the server recorded.
        /// Null when the operation has no client originator (server-initiated event, etc.).
        /// Note: this is NOT a routing field — server-side caller-exclusion already runs on
        /// <see cref="SharedMeta.Server.Core.Grains.EntityBroadcast.ExcludePlayerId"/> before
        /// the op reaches the wire.
        /// </summary>
        [Id(16), Key(16)] public string? CallerId { get; set; }

        // ── Response / replay artifacts ───────────────────────────────────
        /// <summary>Serialized return value (null for void / no return).</summary>
        [Id(4), Key(4)] public byte[]? ResultBytes { get; set; }
        /// <summary>Server-recorded replay context for deterministic client execution.</summary>
        [Id(5), Key(5)] public byte[]? ReplayPayload { get; set; }
        /// <summary>Serialized state diff (ServerPatch mode).</summary>
        [Id(6), Key(6)] public byte[]? PatchBytes { get; set; }
        /// <summary>Full serialized state (ServerReplace mode).</summary>
        [Id(7), Key(7)] public byte[]? StateBytes { get; set; }

        // ── Determinism deltas ─────────────────────────────────────────────
        [Id(8), Key(8)] public long RandomScrollDelta { get; set; }
        [Id(9), Key(9)] public long[]? NamedRandomScrollDeltas { get; set; }

        // ── Server context for replay ──────────────────────────────────────
        [Id(10), Key(10)] public long ServerTimeTicks { get; set; }
        [Id(11), Key(11)] public MetaConfigVersion ExecutedConfigVersion { get; set; }

        // ── Method-level error (set when the method body returned an error) ──
        [Id(12), Key(12)] public string? Error { get; set; }

        // ── Deep-desync detection (only set when DeepDesyncRequested) ──────
        [Id(13), Key(13)] public uint? DeepDesyncCrc { get; set; }

        // ── Nested triggers (replaces TriggerBroadcasts / TriggerOperations) ──
        [Id(14), Key(14)] public List<MetaOperation>? Triggers { get; set; }

        // ── Debug (rarely used, optional payload-debug info) ──────────────
        [Id(15), Key(15)] public string? Debug { get; set; }

        [IgnoreMember] public bool HasError => Error != null;
        [IgnoreMember] public bool Success => Error == null;

        /// <summary>
        /// Clears all mutable state so the instance can be returned to a pool and reused.
        /// Sets reference fields to null and value fields to default. Triggers list reference
        /// is cleared but not pooled here — callers are expected to recycle nested ops separately.
        /// </summary>
        public void Reset()
        {
            MethodId = 0;
            Payload = null;
            ResultBytes = null;
            ReplayPayload = null;
            PatchBytes = null;
            StateBytes = null;
            RandomScrollDelta = 0;
            NamedRandomScrollDeltas = null;
            ServerTimeTicks = 0;
            ExecutedConfigVersion = default;
            Error = null;
            DeepDesyncCrc = null;
            Triggers = null;
            Debug = null;
            CallerId = null;
        }
    }
}
