using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response to session connection request.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SessionConnectResponse
    {
        /// <summary>True if connection was successful.</summary>
        [Id(0), Key(0)] public bool Success { get; set; }

        /// <summary>Error message if connection failed.</summary>
        [Id(1), Key(1)] public string? Error { get; set; }

        /// <summary>Assigned session ID.</summary>
        [Id(2), Key(2)] public Guid SessionId { get; set; }

        /// <summary>True if this is a new session, false if resuming.</summary>
        [Id(3), Key(3)] public bool IsNewSession { get; set; }

        /// <summary>
        /// Responses missed during disconnect (for session resume).
        /// Empty for new sessions. Each response has its own SequenceNumber.
        /// </summary>
        [Id(4), Key(4)] public List<SessionResponse> MissedPackets { get; set; } = new();

        /// <summary>
        /// Current server UTC ticks for initial clock synchronization.
        /// </summary>
        [Id(6), Key(6)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// Server's current version. Populated when version checking is configured.
        /// Useful for displaying upgrade prompts on the client.
        /// </summary>
        [Id(8), Key(8)] public string? ServerVersion { get; set; }

        /// <summary>
        /// Minimum client version required by the server. Populated when the connection
        /// is rejected due to an incompatible client version.
        /// </summary>
        [Id(9), Key(9)] public string? MinClientVersion { get; set; }

        /// <summary>
        /// Maximum client version this server supports. Populated when the connection is
        /// rejected because the client is too new for this server — lets the client show
        /// "please wait for a server update" or switch to a compatible endpoint.
        /// </summary>
        [Id(10), Key(10)] public string? MaxClientVersion { get; set; }

        /// <summary>
        /// 0.22.0+: True when the server didn't find the client's
        /// <see cref="SessionConnectRequest.ClientSignatureHash"/> in its registry and
        /// needs a phase-2 follow-up. The client MUST send a
        /// <see cref="RegisterClientSignatureRequest"/> with the full signature before
        /// issuing any RPC. While this flag is set, <see cref="Annotated"/> is null and the
        /// client should treat the session as "not yet known to negotiate against."
        /// </summary>
        [Id(11), Key(11)] public bool NeedsSignatureRegistration { get; set; }

        /// <summary>
        /// 0.24.0+ Server signature hash, ALWAYS populated. Drives client cache
        /// invalidation for <see cref="ClientSignatureAnnotated"/> — the client compares
        /// to <c>cached[clientHash].ServerSignatureHash</c>; mismatch forces a phase-2
        /// re-registration even when the server already knew this clientHash.
        /// </summary>
        [Id(13), Key(13)] public ulong ServerSignatureHash { get; set; }

        /// <summary>
        /// 0.24.0+ Annotated client signature (verdict + id mapping). Populated when the
        /// server already knew this <c>ClientSignatureHash</c> AND its cached annotations
        /// are current for the reported <see cref="ServerSignatureHash"/>. Null when
        /// phase-2 is needed (<see cref="NeedsSignatureRegistration"/> true) or when the
        /// client's cache is stale (annotations will arrive in
        /// <see cref="RegisterClientSignatureResponse.Annotated"/>).
        /// </summary>
        [Id(14), Key(14)] public ClientSignatureAnnotated? Annotated { get; set; }

        /// <summary>
        /// 0.24.0+ Structured rejection reason when <see cref="Success"/> is false. Lets the
        /// client distinguish "your session is unknown, please start fresh" (recoverable —
        /// fire <c>IMetaSessionRecoveryHandler.OnSessionLostAsync</c>) from "configuration
        /// problem / hard reject". Default <see cref="SessionConnectFailureReason.None"/>
        /// when the connect succeeded.
        /// </summary>
        [Id(15), Key(15)] public SessionConnectFailureReason FailureReason { get; set; }

        /// <summary>
        /// 0.24.0+ Per-claim subscription verdicts produced by the server on Resume —
        /// answers for each <see cref="SessionConnectRequest.ClaimedSubscriptions"/> entry.
        /// <para>
        /// Replaces the older <see cref="ResubscribedEntities"/> field (server-driven
        /// resubscribe from persisted state). Subscriptions are now owned by the client:
        /// the client claims, the server verifies against entity grains and reports
        /// <c>Continued</c> (no state shipped, client keeps current view),
        /// <c>Refreshed</c> (gap detected — fresh state attached), or
        /// <c>Failed</c> (entity unreachable / permission denied — client surfaces to game).
        /// </para>
        /// <para>
        /// Null/empty when no claims were sent (StartNew connect, first-ever connect, or
        /// resume with no prior subscriptions).
        /// </para>
        /// </summary>
        [Id(16), Key(16)] public List<SubscriptionResult>? Subscriptions { get; set; }

        /// <summary>
        /// The canonical shape of an authentication rejection. Both server transports answer with
        /// this, and clients synthesize it when a 401 arrives with no body (an ASP.NET
        /// authorization-middleware rejection carries none) — so the dispatcher sees one shape
        /// regardless of which layer refused the credential.
        /// </summary>
        public static SessionConnectResponse AuthenticationRequired(string error) => new()
        {
            Success = false,
            FailureReason = SessionConnectFailureReason.AuthenticationRequired,
            Error = error,
        };
    }

    /// <summary>
    /// 0.24.0+ Reason for a failed <see cref="SessionConnectResponse"/>.
    /// </summary>
    [GenerateSerializer]
    public enum SessionConnectFailureReason : byte
    {
        /// <summary>Connect succeeded; the response carries SessionId / Annotated / etc.</summary>
        None = 0,
        /// <summary>
        /// <see cref="SessionConnectRequest.Mode"/> was <see cref="SessionConnectMode.Resume"/>
        /// but the server doesn't recognize <see cref="SessionConnectRequest.SessionId"/>
        /// (typical causes: server restarted without persistent session state, grain
        /// idle-deactivated, sessionId was forged). Client should fire its
        /// <c>IMetaSessionRecoveryHandler</c> to let the game decide the next step
        /// (typically <c>SessionRecoveryAction.Reconnect</c> — issue a fresh
        /// <see cref="SessionConnectMode.StartNew"/> and re-subscribe known entities).
        /// </summary>
        SessionUnknown = 1,

        /// <summary>
        /// 0.24.0+ Server matched the sessionId, but at least one entity in the client's
        /// <see cref="SessionConnectRequest.ClaimedSubscriptions"/> returned
        /// <see cref="SubscriptionStatus.Failed"/> on reclaim (access denied, schema
        /// incompatible, entity unavailable). Whole-session abort: the client cannot
        /// safely continue with partial subscriptions, so the recovery flow fires the
        /// same path as <see cref="SessionUnknown"/> — fresh session, client decides
        /// whether to re-attempt subscribes from the game side.
        /// </summary>
        SubscriptionReclaimFailed = 2,

        /// <summary>
        /// The token authenticated, but its PlayerId is unknown to the server's auth store —
        /// the account behind the token is gone (auth data wiped, environment reset, account
        /// deleted) while the token itself is still within its lifetime. Not recoverable by
        /// reconnecting: the client must discard the cached token and perform a full login,
        /// which yields a fresh PlayerId. Retrying with the same credential loops forever.
        /// </summary>
        IdentityUnknown = 3,

        /// <summary>
        /// The connection carried no usable credential where one is required — no token, an expired
        /// one, or a token the authentication middleware refused (wrong signature, wrong issuer). The
        /// classic trigger is an app resumed from the background after its access token expired:
        /// the transport re-dials with the stale token, the endpoint accepts the connection as
        /// anonymous, and the handshake lands here.
        /// <para>
        /// Recoverable, unlike <see cref="IdentityUnknown"/>: re-acquire a token (refresh keeps the
        /// same PlayerId) and re-run the handshake. Transports that read their token during the
        /// connection handshake — SignalR does — must be re-dialled for a new token to take effect.
        /// </para>
        /// </summary>
        AuthenticationRequired = 4,
    }

    /// <summary>
    /// Info about an entity re-subscribed during session reconnect (transport DTO).
    /// <para>
    /// DEPRECATED in 0.24.0+ in favour of <see cref="SubscriptionResult"/> — server no
    /// longer drives re-subscribe from its own persisted subscription list; the client
    /// claims its known subscriptions via <see cref="SessionConnectRequest.ClaimedSubscriptions"/>
    /// and the server verifies with the entity grains. Kept on the wire for one release
    /// so older clients can still observe (server will leave it empty in new builds).
    /// </para>
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ResubscribedEntityInfo
    {
        [Id(0), Key(0)] public string EntityId { get; set; } = "";
        [Id(1), Key(1)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        [Id(2), Key(2)] public long EntitySequenceNumber { get; set; }
        [Id(3), Key(3)] public byte[]? OptimisticRandomBytes { get; set; }
        // 0.33.0+ Id/Key 4/5/7 (ConfigMajor/Minor/PatchVersion ints) retired — see ConfigVersions
        // below. Never reuse ids 4/5/7.
        [Id(6), Key(6)] public byte[]? NamedRandomsBytes { get; set; }

        /// <summary>Resolved config version(s) — index 0 legacy primary when declared, remaining <see cref="SharedMeta.Core.ServiceConfigAttribute"/> entries.</summary>
        [Id(8), Key(8)] public List<MetaConfigVersion>? ConfigVersions { get; set; }
    }

    /// <summary>
    /// 0.24.0+ Verdict on one <see cref="SubscriptionClaim"/>.
    /// </summary>
    [GenerateSerializer]
    public enum SubscriptionStatus : byte
    {
        /// <summary>Entity grain confirms the subscription and the client's
        /// <c>LastKnownEntitySequence</c> matches current — no gap, no state shipped,
        /// client keeps its local view as-is.</summary>
        Continued = 0,
        /// <summary>Entity grain confirms the subscription but there's a sequence gap, OR
        /// the grain had to re-add the subscriber from scratch. A full state snapshot is
        /// attached; the client replaces its local view.</summary>
        Refreshed = 1,
        /// <summary>Subscription could not be re-established (entity unavailable, permission
        /// denied, configuration mismatch). Client surfaces this to the game via
        /// <c>IMetaSubscriptionRecoveryHandler</c>; the game decides whether to retry,
        /// drop the entity, or fail the session.</summary>
        Failed = 2,
    }

    /// <summary>
    /// 0.24.0+ Per-claim subscription verdict returned in
    /// <see cref="SessionConnectResponse.Subscriptions"/>.
    /// <para>
    /// For <see cref="SubscriptionStatus.Refreshed"/> the state-snapshot fields are
    /// populated (<see cref="StateBytes"/>, randoms, config version). For
    /// <see cref="SubscriptionStatus.Continued"/> only <see cref="EntityId"/> /
    /// <see cref="Status"/> / <see cref="EntitySequenceNumber"/> are meaningful — state
    /// bytes are empty. For <see cref="SubscriptionStatus.Failed"/> <see cref="FailureReason"/>
    /// carries a short diagnostic; other state fields are empty.
    /// </para>
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer, Immutable]
    public partial class SubscriptionResult
    {
        [Id(0), Key(0)] public string EntityId { get; set; } = "";
        [Id(1), Key(1)] public SubscriptionStatus Status { get; set; }
        [Id(2), Key(2)] public long EntitySequenceNumber { get; set; }
        [Id(3), Key(3)] public byte[]? StateBytes { get; set; }
        [Id(4), Key(4)] public byte[]? OptimisticRandomBytes { get; set; }
        [Id(5), Key(5)] public byte[]? NamedRandomsBytes { get; set; }
        // 0.33.0+ Id/Key 6/7/8 (ConfigMajor/Minor/PatchVersion ints) retired — see ConfigVersions
        // below. Never reuse ids 6/7/8.
        [Id(9), Key(9)] public string? FailureReason { get; set; }

        /// <summary>Resolved config version(s) — index 0 legacy primary when declared, remaining <see cref="SharedMeta.Core.ServiceConfigAttribute"/> entries.</summary>
        [Id(10), Key(10)] public List<MetaConfigVersion>? ConfigVersions { get; set; }

        /// <summary>
        /// 0.33.0+ Full type name of the state this verdict resolves — echoes the corresponding
        /// <see cref="SubscriptionClaim.StateTypeName"/>. entityId alone doesn't uniquely identify
        /// a connection (the server addresses entities by (state type, entityId), so two claims
        /// can share an entityId across different state types, e.g. Inventory/Profile/Wallet all
        /// keyed by playerId) — the client needs this to correlate a verdict back to the right
        /// connection on Resume.
        /// </summary>
        [Id(11), Key(11)] public string StateTypeName { get; set; } = "";
    }
}
