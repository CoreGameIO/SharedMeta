using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to establish or resume a session with the server.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SessionConnectRequest
    {
        /// <summary>Player identifier for this session.</summary>
        [Id(0), Key(0)] public string PlayerId { get; set; } = "";

        /// <summary>
        /// Session ID for resuming an existing session.
        /// Null for new sessions.
        /// </summary>
        [Id(1), Key(1)] public Guid? SessionId { get; set; }

        /// <summary>
        /// Last acknowledged sequence number from previous session.
        /// Server will resend any missed packets after this sequence.
        /// </summary>
        [Id(2), Key(2)] public long LastAcknowledgedSequence { get; set; }

        /// <summary>
        /// Client's application version in "major.minor.patch" format (e.g. "1.2.3").
        /// Used by the server to enforce minimum version compatibility.
        /// Null if the client does not send version information.
        /// </summary>
        [Id(4), Key(4)] public string? ClientVersion { get; set; }

        /// <summary>
        /// 0.22.0+: FNV-1a hash of the client's full <see cref="MetaClientSignature"/>
        /// (sorted KnownMethods + per-method ArgHash + Version). Phase-1 handshake key.
        /// <para>
        /// When non-zero, the server looks up the matching <see cref="ClientSignatureAnnotated"/>
        /// in its signature registry. If unknown, the server replies with
        /// <c>NeedsSignatureRegistration = true</c> and the client follows up with a
        /// <see cref="RegisterClientSignatureRequest"/> carrying the full signature.
        /// </para>
        /// <para>
        /// Zero (default) means the client opted out of compatibility negotiation or is
        /// pre-0.22.0; the server treats those connections as fully compatible (legacy
        /// behaviour preserved).
        /// </para>
        /// </summary>
        [Id(5), Key(5)] public ulong ClientSignatureHash { get; set; }

        /// <summary>
        /// 0.24.0+ Explicit session-connect intent. <see cref="SessionConnectMode.StartNew"/>
        /// asks the server to create a fresh session (server allocates SessionId, ignores any
        /// supplied one). <see cref="SessionConnectMode.Resume"/> asks the server to bind to
        /// an existing <see cref="SessionId"/>; if the server doesn't recognize it, the response
        /// returns <see cref="SessionConnectFailureReason.SessionUnknown"/> instead of silently
        /// creating a new session — the client decides whether to retry as StartNew (typical
        /// path) or surface the loss to the game.
        /// <para>
        /// Default value (<c>StartNew = 0</c>) makes pre-0.24 clients land in the fresh-session
        /// branch automatically, which matches the legacy "null SessionId = new session" behavior.
        /// </para>
        /// </summary>
        [Id(6), Key(6)] public SessionConnectMode Mode { get; set; } = SessionConnectMode.StartNew;

        /// <summary>
        /// 0.24.0+ Client's highest fully-completed RPC <c>RequestId</c> (responses received
        /// and pending entry cleared). Server uses this on Resume to advance its
        /// <c>RpcOrderingBuffer.LastDispatchedRequestId</c> over any gap — covers the case
        /// where the server lost cached responses (eviction or crash before persistence
        /// flush) but the client did receive them. Without this, client re-sends of ids
        /// ahead of the server's lost baseline would be classified <c>OutOfOrder</c> and
        /// stashed forever, blocking the session.
        /// <para>
        /// Default 0 → no advancement (cold-start or first connect, nothing to skip past).
        /// </para>
        /// </summary>
        [Id(7), Key(7)] public long LastCompletedRequestId { get; set; }

        /// <summary>
        /// 0.24.0+ Client's known subscriptions at Resume time — entityId + state type + the
        /// highest entity-sequence number the client has observed. On Resume the server
        /// validates each claim against the entity grain (asks "do you still have us as a
        /// subscriber and do our sequence numbers agree?") and returns a matching
        /// <see cref="SessionConnectResponse.Subscriptions"/> entry per claim with status
        /// <c>Continued</c> (no gap, no state shipped) or <c>Refreshed</c> (gap detected,
        /// fresh state included).
        /// <para>
        /// Replaces the previous server-driven re-subscribe flow that read from
        /// <c>SessionManagerGrainState.SubscribedEntities</c> — subscriptions are now
        /// owned by the client, the server reconstructs them from this claim list on Resume.
        /// </para>
        /// <para>
        /// Null/empty for <c>StartNew</c> connects and for first-ever connects with no
        /// prior subscriptions.
        /// </para>
        /// </summary>
        [Id(8), Key(8)] public List<SubscriptionClaim>? ClaimedSubscriptions { get; set; }
    }

    /// <summary>
    /// 0.24.0+ One client-claimed subscription sent in <see cref="SessionConnectRequest.ClaimedSubscriptions"/>.
    /// Server matches against entity grain state to produce a <see cref="SubscriptionResult"/>.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SubscriptionClaim
    {
        [Id(0), Key(0)] public string EntityId { get; set; } = "";
        [Id(1), Key(1)] public string StateTypeName { get; set; } = "";
        /// <summary>Highest entity sequence number the client has applied locally for this entity.
        /// Used by the server to detect gaps (anything below current entity seq = gap → Refreshed).</summary>
        [Id(2), Key(2)] public long LastKnownEntitySequence { get; set; }
    }

    /// <summary>
    /// 0.24.0+ Direction of the SessionConnect call. See <see cref="SessionConnectRequest.Mode"/>.
    /// </summary>
    // Underlying type is the default int (NOT byte): BestHTTP's LitJson JSON encoder
    // serializes byte/short-backed enums via (int)obj, which throws InvalidCastException
    // when unboxing a boxed byte-enum. This enum travels client→server in every
    // SessionConnect over the JSON protocol, so it must stay int-backed.
    [GenerateSerializer]
    public enum SessionConnectMode
    {
        /// <summary>Server allocates a new SessionId and binds to it. SessionId in the request
        /// is ignored. <c>IsNewSession=true</c> on the response.</summary>
        StartNew = 0,
        /// <summary>Server checks whether <see cref="SessionConnectRequest.SessionId"/> matches
        /// its currently-known session. Match → resume + replay missed packets. Mismatch →
        /// <see cref="SessionConnectFailureReason.SessionUnknown"/> in the response, no implicit
        /// fallback to StartNew.</summary>
        Resume = 1,
    }
}
