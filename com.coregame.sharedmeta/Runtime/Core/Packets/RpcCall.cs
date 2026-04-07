using System;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core
{
    /// <summary>
    /// Unified RPC call structure for both requests and responses.
    /// Payload contains serialized arguments (request) or result + replay data (response).
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RpcCall
    {
        [Id(0), Key(0)] public string ServiceName { get; set; } = "";
        [Id(1), Key(1)] public string MethodName { get; set; } = "";
        [Id(2), Key(2)] public int MethodVersion { get; set; } = 0;
        [Id(3), Key(3)] public byte[] Payload { get; set; } = Array.Empty<byte>();
        [Id(4), Key(4)] public PayloadDebug? Debug { get; set; }

        /// <summary>
        /// ID of the caller making this request.
        /// Set by the transport layer (client ID, connection ID, etc.)
        /// </summary>
        [Id(5), Key(5)] public string? CallerId { get; set; }

        /// <summary>
        /// Server-side replay context for deterministic client execution.
        /// Only set for incoming broadcasts, null for outgoing calls.
        /// </summary>
        [Id(6), Key(6)] public byte[]? ReplayPayload { get; set; }

        /// <summary>
        /// When true, SessionManager suppresses cross-entity broadcasts for the caller
        /// (they were already applied locally in CrossOptimistic mode).
        /// </summary>
        [Id(7), Key(7)] public bool IsCrossOptimistic { get; set; }

        /// <summary>
        /// Server time (UTC ticks) captured at method start for deterministic execution.
        /// Set by client before optimistic execution, sent with request, used in MetaContext on server.
        /// Carried in broadcasts for replay on other clients.
        /// </summary>
        [Id(8), Key(8)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// When true, server should compute deep desync CRC for this call.
        /// Set per-session by SetDebugOptions.
        /// </summary>
        [Id(9), Key(9)] public bool DeepDesyncRequested { get; set; }
    }
}
