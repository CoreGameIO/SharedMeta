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
        /// Approximate current server time (UTC ticks).
        /// Computed from last received server time + local elapsed delta.
        /// Used by generated code to capture time at method start.
        /// </summary>
        long ServerTimeTicks { get; }

        /// <summary>
        /// Call a method that returns a value.
        /// <para><c>methodVersion</c> (0.22.0+) is the value of <c>[MetaMethod(Version=N)]</c>
        /// on the declared interface method; stamped onto <see cref="RpcCall.MethodVersion"/>
        /// so the server dispatcher routes to the correct <c>(Alias, Version)</c> implementation.
        /// Default <c>0</c> selects the lowest-versioned (legacy) implementation under the alias.</para>
        /// </summary>
        Task<CallResponse<T>> CallAsync<T>(string serviceName, string methodName, byte[] args, bool isCrossOptimistic = false, long serverTimeTicks = 0, int methodVersion = 0);

        /// <summary>
        /// Call a void method. See <see cref="CallAsync{T}"/> for the <c>methodVersion</c> contract.
        /// </summary>
        Task<VoidCallResponse> CallVoidAsync(string serviceName, string methodName, byte[] args, bool isCrossOptimistic = false, long serverTimeTicks = 0, int methodVersion = 0);

        /// <summary>
        /// Call a method and get raw bytes result (for serializer-specific deserialization).
        /// See <see cref="CallAsync{T}"/> for the <c>methodVersion</c> contract.
        /// </summary>
        Task<ByteCallResponse> CallBytesAsync(string serviceName, string methodName, byte[] args, bool isCrossOptimistic = false, long serverTimeTicks = 0, int methodVersion = 0);

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
        ValueTask SendSignalAsync(string serviceName, string methodName, byte[] args, int methodVersion = 0)
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
        /// <summary>Service name (e.g., "IProfileService").</summary>
        public string ServiceName { get; set; } = "";

        /// <summary>Method name (e.g., "SetName").</summary>
        public string MethodName { get; set; } = "";

        /// <summary>Caller who initiated this action.</summary>
        public string? CallerId { get; set; }

        /// <summary>Serialized arguments.</summary>
        public byte[] ArgsBytes { get; set; } = Array.Empty<byte>();

        /// <summary>Server-side replay context for deterministic local execution.</summary>
        public byte[] ReplayContext { get; set; } = Array.Empty<byte>();

        /// <summary>Triggered operations executed after the main call (if any).</summary>
        public List<OperationResult>? TriggerOperations { get; set; }

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
        /// The <see cref="MetaConfigVersion"/> the server actually executed under (added in
        /// 0.21.0). Subscribers use it to pin <c>Context.Config</c> during broadcast replay,
        /// so a cross-version observer (mid-session config rollout, or <c>[EntityScope(Global)]</c>
        /// where the server normalized to <c>CurrentClientVersion</c>) replays the same config
        /// the server saw. <c>default(MetaConfigVersion)</c> = no config system, fall back to
        /// session-resolved version.
        /// </summary>
        public MetaConfigVersion ExecutedConfigVersion { get; set; }
    }
}
