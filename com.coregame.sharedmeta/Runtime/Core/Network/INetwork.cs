using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core.Packets;

namespace SharedMeta.Core.Network
{
    /// <summary>
    /// Network layer for RPC calls.
    /// Handles transport, packet matching, timeouts.
    /// Does NOT know about meta-logic.
    /// </summary>
    public interface INetwork
    {
        /// <summary>
        /// Unique client identifier.
        /// </summary>
        string ClientId { get; }

        /// <summary>
        /// Player ID for this client (used as CallerId for RPC calls).
        /// </summary>
        string? PlayerId { get; }

        /// <summary>
        /// Currently connected entity ID.
        /// </summary>
        string? EntityId { get; }

        /// <summary>
        /// 0.24.0+ Highest per-entity broadcast sequence number the client has observed for the
        /// network's bound entity. Surfaced for desync diagnostic — generated <c>*ApiClient</c>
        /// logs this alongside the server-stamped <c>response.Debug</c> ("seq=N") so a desync
        /// reader can immediately tell whether the client's local replay ran against a stale
        /// state (gap between client seq and server seq → ordering issue, broadcast not yet
        /// applied) or a matching state (true result-computation divergence).
        /// </summary>
        long LastKnownEntitySequence { get; }

        /// <summary>
        /// 0.24.0+ Annotated client signature returned by the server (verdict + id mapping).
        /// Set by <c>ClientDispatcher</c> after <c>SessionConnect</c> / phase-2
        /// <c>RegisterClientSignature</c>, or restored from <see cref="IServerAnnotationCache"/>
        /// when the cached <c>ServerSignatureHash</c> matches the server's reported one.
        /// Consumed by generated <c>*ApiClient</c> through
        /// <c>CapabilitiesGate.IsRejected(annotated, methodId)</c> /
        /// <c>CapabilitiesGate.IsForcedServerPatch(annotated, methodId)</c> — O(1) array lookup
        /// per call. Replaces <see cref="Capabilities"/>; both populated during the
        /// 0.24.0 migration window, the legacy one removed in the next minor.
        /// </summary>
        SharedMeta.Core.Transport.ClientSignatureAnnotated? Annotated { get; set; }

        /// <summary>
        /// 0.22.0+: Per-entity capability overlay supplied by the server's
        /// <c>SubscribeResponse.AugmentedCapabilities</c>. Stored on the per-entity adapter
        /// (one INetwork per entity). Generated <c>*ApiClient</c> checks this alongside
        /// session-level <see cref="Capabilities"/> at the gate — service-level rejection or
        /// force-ServerPatch can be triggered on a single entity even when the rest of the
        /// session is unrestricted (typical case: <c>[MetaConfigStructureBoundary]</c> hit
        /// for this entity's resolved config version).
        /// </summary>
        SharedMeta.Core.Transport.EntityAugmentedCapabilities? EntityCapabilities { get; set; }

        /// <summary>
        /// Approximate current server time (UTC ticks).
        /// Computed from last received server time + local elapsed delta.
        /// Used by generated code to capture time at method start.
        /// </summary>
        long ServerTimeTicks { get; }

        /// <summary>
        /// Call a method that returns a value.
        /// <para><c>methodVersion</c> = <c>[MetaMethod(Version=N)]</c>, stamped on
        /// <c>RpcCall.MethodVersion</c>. <c>methodId</c> (0.24.0+) = client's global method
        /// index from <c>GameMethodIds</c>, stamped on <c>RpcCall.MethodId</c> — the server
        /// translates it to its own server-side index via the signature mapping.</para>
        /// </summary>
        Task<CallResponse<T>> CallAsync<T>(ushort methodId, ReadOnlyMemory<byte> args, bool isCrossOptimistic = false, long serverTimeTicks = 0, PayloadDebug? debug = null);

        /// <summary>
        /// Call a void method. See <see cref="CallAsync{T}"/> for the parameter contract.
        /// <paramref name="debug"/>: optional <see cref="PayloadDebug"/> stamped onto
        /// <c>RpcCall.Debug</c> — generated <c>*ApiClient</c> uses it to ship deep-state
        /// CRCs (<c>PreStateCrc</c>/<c>PostStateCrc</c>) for
        /// <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c> methods.
        /// </summary>
        Task<VoidCallResponse> CallVoidAsync(ushort methodId, ReadOnlyMemory<byte> args, bool isCrossOptimistic = false, long serverTimeTicks = 0, PayloadDebug? debug = null);

        /// <summary>
        /// Call a method and get raw bytes result (for serializer-specific deserialization).
        /// See <see cref="CallAsync{T}"/> for the parameter contract.
        /// </summary>
        Task<ByteCallResponse> CallBytesAsync(ushort methodId, ReadOnlyMemory<byte> args, bool isCrossOptimistic = false, long serverTimeTicks = 0, PayloadDebug? debug = null);

        /// <summary>
        /// Send a desync follow-up report (deep desync detection).
        /// Returns null if the underlying transport does not support reporting.
        /// </summary>
        Task<SharedMeta.Core.Transport.DesyncReportResponse?> SendDesyncReportAsync(SharedMeta.Core.Transport.DesyncReportRequest request);

        /// <summary>
        /// Fire a signal — a one-way RPC that produces no response on the wire and bypasses
        /// the RequestId / auto-retry / connection-health machinery. The server routes the call
        /// to the target entity's <c>HandleSignalAsync</c>, which executes read-only, skipping
        /// sequence increments, broadcasts, and persistence. Server-side errors are logged but
        /// never propagate back — callers must treat this as pure fire-and-forget.
        /// Typical use: heartbeat, telemetry ping, bridge-driven notification.
        /// Transports are expected to resolve the returned <see cref="ValueTask"/> as soon as
        /// the message is handed off to the wire; it does NOT represent server execution.
        /// Default implementation throws — transports that do not support signals must opt in.
        /// </summary>
        ValueTask SendSignalAsync(ushort methodId, ReadOnlyMemory<byte> args)
            => throw new System.NotSupportedException(
                "This transport does not support fire-and-forget signals. Use a MetaMethod without [Signal] or switch to a transport that supports signals (InProcess, SignalR, HttpPolling).");

        /// <summary>
        /// Suppress broadcast processing. Must be paired with ResumeBroadcasts().
        /// Used to prevent broadcasts from modifying state between receiving an RPC response
        /// and completing the local replay (which would cause desyncs).
        /// </summary>
        void SuppressBroadcasts();

        /// <summary>
        /// Resume broadcast processing after SuppressBroadcasts().
        /// When the last suppress is released, pending broadcasts are drained.
        /// </summary>
        void ResumeBroadcasts();

        /// <summary>
        /// Broadcasts from server (other players' actions).
        /// </summary>
        event Action<NetworkBroadcast>? OnBroadcast;

        /// <summary>
        /// Connection lost.
        /// </summary>
        event Action<string>? OnDisconnected;
    }

    /// <summary>
    /// Broadcast message from server.
    /// </summary>
    public class NetworkBroadcast
    {
        /// <summary>
        /// 0.24.0+ Client's local global method index (already translated from server's id
        /// via <c>ClientSignatureAnnotated.ServerToClient</c>). Generated broadcast handlers
        /// dispatch on this against <c>GameMethodIds</c> / <c>FrameworkMethodIds</c> constants
        /// — a jump table on <c>ushort</c> instead of string-pair matching. <c>ushort.MaxValue</c>
        /// when the server emitted a method the client doesn't know — handler ignores.
        /// </summary>
        public ushort MethodId { get; set; }

        /// <summary>Caller who initiated this action.</summary>
        public string? CallerId { get; set; }

        /// <summary>Serialized arguments.</summary>
        public byte[] ArgsBytes { get; set; } = Array.Empty<byte>();

        /// <summary>Server-side replay context for deterministic local execution.</summary>
        public byte[] ReplayContext { get; set; } = Array.Empty<byte>();

        /// <summary>Triggered operations executed after the main call (if any).</summary>
        public List<MetaOperation>? TriggerOperations { get; set; }

        /// <summary>Server time (UTC ticks) for deterministic replay.</summary>
        public long ServerTimeTicks { get; set; }

        /// <summary>Delta of optimistic random ScrollId during this call (for desync detection).</summary>
        public long RandomScrollDelta { get; set; }

        /// <summary>Serialized state diff patch for ServerPatch mode.</summary>
        public byte[]? PatchBytes { get; set; }

        /// <summary>Full serialized state for ServerReplace mode.</summary>
        public byte[]? StateBytes { get; set; }

        /// <summary>Per-index scroll deltas for [NamedRandom] streams (positional). Null when none advanced.</summary>
        public long[]? NamedRandomScrollDeltas { get; set; }

        /// <summary>
        /// The <see cref="MetaConfigVersion"/>s the server actually executed under — index 0 is
        /// the target service's legacy primary config when declared, remaining indices are
        /// <see cref="ServiceConfigAttribute"/> entries in declaration order. Subscribers use it
        /// to pin <c>Context.Config</c> / <c>Context.Configs</c> during broadcast replay, so a
        /// cross-version observer
        /// (mid-session config rollout, or <c>[EntityScope(Global)]</c> where the server
        /// normalized to <c>CurrentClientVersion</c>) replays the same config(s) the server saw.
        /// Null/empty = no config system, fall back to session-resolved version(s). 0.33.0+ (was
        /// a single scalar <c>ExecutedConfigVersion</c> pre-0.33).
        /// </summary>
        public List<MetaConfigVersion>? ExecutedConfigVersions { get; set; }
    }
}
