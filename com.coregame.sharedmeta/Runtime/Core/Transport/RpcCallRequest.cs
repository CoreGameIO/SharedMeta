using System;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to execute an RPC method on an entity.
    /// RequestId is managed here (transport layer) for idempotency.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RpcCallRequest
    {
        /// <summary>Entity to execute the call on.</summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>
        /// Request ID for idempotency.
        /// SessionManager uses this to cache responses and prevent duplicate processing.
        /// This is a transport-level concern, NOT part of RpcCall.
        /// </summary>
        [Id(1), Key(1)] public long RequestId { get; set; }

        /// <summary>Service containing the method to call.</summary>
        [Id(2), Key(2)] public string ServiceName { get; set; } = "";

        /// <summary>Method name to execute.</summary>
        [Id(3), Key(3)] public string MethodName { get; set; } = "";

        /// <summary>Serialized method arguments.</summary>
        [Id(4), Key(4)] public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Piggybacked acknowledgment: highest sequence number client has processed.
        /// Sent with each request to avoid separate AcknowledgeSequence calls.
        /// Server uses this to clean up pending packets that client has received.
        /// </summary>
        [Id(5), Key(5)] public long LastAcknowledgedSequence { get; set; }

        /// <summary>
        /// When true, this is a cross-optimistic call where the client already applied
        /// cross-entity side effects locally. Server uses this to suppress duplicate
        /// broadcasts for those cross-entity operations.
        /// </summary>
        [Id(6), Key(6)] public bool IsCrossOptimistic { get; set; }

        /// <summary>
        /// Server time (UTC ticks) captured by the client at method start.
        /// Server sets this on MetaContext for deterministic execution.
        /// </summary>
        [Id(7), Key(7)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// 0.22.0+: Method version (mirrors <c>RpcCall.MethodVersion</c>). Stamped by the
        /// generated client from <c>[MetaMethod(Version = N)]</c>. The server validates
        /// <c>(ServiceName, MethodName, MethodVersion)</c> against the caller's
        /// <see cref="ClientCapabilities"/> before dispatching — a forged client that bypasses
        /// the local <c>CapabilitiesGate</c> still gets rejected at this back-stop.
        /// </summary>
        [Id(8), Key(8)] public int MethodVersion { get; set; }
    }
}
