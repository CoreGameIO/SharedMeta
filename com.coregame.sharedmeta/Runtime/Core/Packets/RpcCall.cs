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
    [MemoryPackable, MessagePackObject, GenerateSerializer, Immutable]
    public partial class RpcCall
    {
        /// <summary>
        /// 0.24.0+ Method identifier. Client→server hops carry the client's global index
        /// (translated to server's by <c>MetaConnectionHandler</c> via the per-signature
        /// clientToServer map). Server↔server (cross-entity) hops carry the server's global
        /// index directly. Coexists with ServiceName/MethodName/MethodVersion during migration.
        /// </summary>
        [Id(0), Key(0)] public ushort MethodId { get; set; }

        [Id(1), Key(1), MemoryPackAllowSerialize] public ReadOnlyMemory<byte> Payload { get; set; }

        [Id(2), Key(2)] public PayloadDebug? Debug { get; set; }

        /// <summary>
        /// ID of the caller making this request.
        /// Set by the transport layer (client ID, connection ID, etc.)
        /// </summary>
        [Id(3), Key(3)] public string? CallerId { get; set; }

        /// <summary>
        /// Server-side replay context for deterministic client execution.
        /// Only set for incoming broadcasts, null for outgoing calls.
        /// </summary>
        [Id(4), Key(4), MemoryPackAllowSerialize] public ReadOnlyMemory<byte> ReplayPayload { get; set; }

        /// <summary>
        /// When true, SessionManager suppresses cross-entity broadcasts for the caller
        /// (they were already applied locally in CrossOptimistic mode).
        /// </summary>
        [Id(5), Key(5)] public bool IsCrossOptimistic { get; set; }

        /// <summary>
        /// Server time (UTC ticks) captured at method start for deterministic execution.
        /// Set by client before optimistic execution, sent with request, used in MetaContext on server.
        /// Carried in broadcasts for replay on other clients.
        /// </summary>
        [Id(6), Key(6)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// When true, server should compute deep desync CRC for this call.
        /// Set per-session by SetDebugOptions.
        /// </summary>
        [Id(7), Key(7)] public bool DeepDesyncRequested { get; set; }

        /// <summary>
        /// Caller's client app version (e.g. "1.4.3"). Populated by the transport handler on
        /// the server from the connection's stored client version. Used by the entity grain
        /// to resolve the per-caller config branch via <c>[MetaConfigVersion]</c> rules and
        /// set <see cref="ServerMetaContext{TState}.Config"/> for the duration of the call,
        /// keeping client-side optimistic execution and server-side computation in sync.
        /// </summary>
        [Id(8), Key(8)] public string? CallerClientVersion { get; set; }
    }
}
