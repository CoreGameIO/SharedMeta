namespace SharedMeta.Core.Diagnostics
{
    /// <summary>Why a session ended. Bounded set — safe as a metric tag.</summary>
    public enum MetaSessionEndReason
    {
        Other = 0,
        Graceful,
        TransportDrop,
        Superseded,
        StashOverflow,
        Stall,
        InactivityTimeout,
        ServerClose,
        VersionRejected,
    }

    /// <summary>Values of <see cref="MetaCallInfo.Result"/>.</summary>
    public static class MetaCallResults
    {
        public const string Success    = "success";
        public const string Error      = "error";
        public const string Rejected   = "rejected";
        public const string BadRequest = "bad_request";
    }

    /// <summary>
    /// Values of <see cref="MetaCallInfo.ErrorKind"/> and of the <c>error_kind</c> metric tag.
    /// Bounded set: free-form error text never goes into a tag.
    /// </summary>
    public static class MetaCallErrorKinds
    {
        public const string Rejected            = "rejected";
        public const string PermissionDenied    = "permission_denied";
        public const string NotSubscribed       = "not_subscribed";
        public const string SessionNotConnected = "session_not_connected";
        public const string SessionSuperseded   = "session_superseded";
        public const string BadRequest          = "bad_request";
        public const string Exception           = "exception";
        public const string ProviderError       = "provider_error";
    }

    /// <summary>One finished meta call, as handed to call observers.</summary>
    public readonly struct MetaCallInfo
    {
        /// <summary>W3C trace id of the call (32 hex chars); empty when the call had no trace.</summary>
        public string CallId { get; init; }

        /// <summary><see cref="MetaCallOps"/> value.</summary>
        public string Op { get; init; }

        public string? Service { get; init; }
        public string? Method { get; init; }

        /// <summary>Execution mode name (<c>Optimistic</c>, <c>Server</c>, …), null when unknown.</summary>
        public string? Mode { get; init; }

        public string? EntityId { get; init; }
        public string? StateType { get; init; }

        /// <summary>Caller's player id. Server side only; null on the client.</summary>
        public string? PlayerId { get; init; }

        /// <summary>Caller's session id. Server side only.</summary>
        public string? SessionId { get; init; }

        /// <summary>Caller's client app version. Server side only.</summary>
        public string? ClientVersion { get; init; }

        /// <summary><see cref="MetaCallResults"/> value.</summary>
        public string Result { get; init; }

        /// <summary><see cref="MetaCallErrorKinds"/> value, null on success.</summary>
        public string? ErrorKind { get; init; }

        public double DurationMs { get; init; }
    }

    /// <summary>
    /// Client-side hook for analytics: told about every finished RPC / Query / Signal. Works
    /// without the DiagnosticSource package (Unity). Called on the thread that completed the
    /// call — keep it cheap and never throw.
    /// </summary>
    public interface IMetaClientCallObserver
    {
        void OnCallCompleted(in MetaCallInfo call);
    }

    /// <summary>
    /// Server-side hook for analytics: told about every finished client call and every session
    /// start / end. Invoked by the transport handler after the response is built, outside grain
    /// turns. Exceptions are caught and logged; they never fail the call.
    /// </summary>
    public interface IMetaServerObserver
    {
        void OnCallCompleted(in MetaCallInfo call);

        void OnSessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed);

        void OnSessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs);
    }
}
