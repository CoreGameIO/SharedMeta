using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Unified response from server to client.
    /// Atomic unit of delivery — the client either receives the entire response or nothing.
    ///
    /// Used for BOTH:
    /// - RPC return values (SendToEntityAsync → client): contains preceding broadcasts + RPC response
    /// - Observer pushes (OnBatch → hub → client): contains broadcasts and/or deferred responses
    ///
    /// Each SessionOp in Operations is either a broadcast (RequestId == 0) or an RPC response
    /// (RequestId > 0). Client processes all operations identically.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SessionResponse
    {
        /// <summary>
        /// Session-level sequence number. One per response, always incrementing per player.
        /// Used for ordering and gap detection on reconnection.
        /// 0 when the response has no operations (e.g., deferred path with no preceding broadcasts).
        /// </summary>
        [Id(0), Key(0)] public long SequenceNumber { get; set; }

        /// <summary>
        /// All operations in this batch, ordered by EntityId.
        /// Each operation is a SessionOp (broadcast or RPC response).
        /// </summary>
        [Id(1), Key(1)] public List<SessionOp> Operations { get; set; } = new();

        /// <summary>
        /// Top-level error if the request failed before any operations were produced.
        /// For per-operation errors, check individual SessionOp.Error.
        /// </summary>
        [Id(2), Key(2)] public string? Error { get; set; }

        /// <summary>
        /// Current server UTC ticks at response creation/resend.
        /// Used by client for clock synchronization. Re-stamped when resending missed packets.
        /// </summary>
        [Id(3), Key(3)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// Out-of-band stall notification from the session manager, used by the server-side
        /// RPC reordering layer to inform the client about temporarily stuck request flows
        /// (missing predecessor that hasn't arrived yet). When non-null, this response is
        /// purely informational and typically carries no <see cref="Operations"/>.
        /// Routed on the client to <c>ISessionHealthListener</c>.
        /// </summary>
        [Id(4), Key(4)] public StallNotification? StallNotification { get; set; }

        /// <summary>True if there was a top-level error.</summary>
        [IgnoreMember] public bool HasError => Error != null;

        /// <summary>
        /// Creates an error SessionResponse.
        /// </summary>
        public static SessionResponse ForError(string error)
        {
            return new SessionResponse { Error = error };
        }
    }
}
