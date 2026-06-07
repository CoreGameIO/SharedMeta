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

        /// <summary>Serialized method arguments.</summary>
        [Id(4), Key(4), MemoryPackAllowSerialize] public ReadOnlyMemory<byte> Payload { get; set; }

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
        /// 0.24.0+ Client's global method index from <c>GameMethodIds</c>. The server's
        /// <c>MetaConnectionHandler</c> translates to its own server-side index via the
        /// per-signature clientToServer map; sentinel <c>ushort.MaxValue</c> from the map
        /// rejects the call. The previous <c>ServiceName</c> / <c>MethodName</c> /
        /// <c>MethodVersion</c> string triple was removed in 0.24.0 — version is encoded
        /// in the index, and (service, method) is resolved server-side via the signature.
        /// </summary>
        [Id(9), Key(9)] public ushort MethodId { get; set; }

        /// <summary>
        /// 0.26.6+ Optional <see cref="SharedMeta.Core.PayloadDebug"/> piggybacked from the
        /// client. The framework uses this slot to carry per-method debug-channel data
        /// (currently: deep-state-check <c>PreStateCrc</c>/<c>PostStateCrc</c> for methods
        /// annotated <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c>). The handler
        /// copies this onto <see cref="SharedMeta.Core.RpcCall.Debug"/> before dispatch so
        /// server-side <c>MetaProviderBase.HandleCallAsync</c> can compare CRCs. Default
        /// <c>null</c> keeps the wire identical for unannotated methods.
        /// </summary>
        [Id(10), Key(10)] public SharedMeta.Core.PayloadDebug? Debug { get; set; }
    }
}
