using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace SharedMeta.Core.Auth
{
    /// <summary>
    /// A re-acquirable access-token source. Hand the same instance to a connection (as its token
    /// provider, via <see cref="GetTokenAsync"/>) and to <c>MetaClientOptions.AccessTokenSource</c>:
    /// the client then auto-recovers when the server rejects a still-locally-valid token (e.g. the JWT
    /// signing key changed) by calling <see cref="Invalidate"/> and retrying the connect once.
    /// <c>MetaTokenManager</c> implements this.
    /// </summary>
    public interface IAccessTokenSource
    {
        /// <summary>
        /// The authenticated player id of the current token, or null before the first token is acquired.
        /// <c>MetaClient</c> seeds its <c>PlayerId</c> from this when none is set explicitly — important
        /// for UserOwned entities, which are keyed by the player id.
        /// </summary>
        string? PlayerId { get; }

        /// <summary>Return a currently-valid access token, refreshing transparently if needed.</summary>
        Task<string?> GetTokenAsync(CancellationToken cancellation = default);

        /// <summary>
        /// Force the next <see cref="GetTokenAsync"/> to re-acquire even if the cached token hasn't
        /// locally expired — used when the server rejected a token the client still considered valid.
        /// </summary>
        void Invalidate();
    }

    /// <summary>
    /// Last-resort recognition of "the server rejected our credential" from a thrown exception.
    /// <para>
    /// Prefer <c>SessionConnectFailureReason.AuthenticationRequired</c> — servers report auth
    /// rejection structurally. This heuristic covers what the structured path cannot reach: a
    /// pre-0.37.2 server that throws instead of answering, and transports whose auth rejection
    /// never becomes a response at all (an HTTP 401 raised inside the client stack). Matching on
    /// message text is deliberately conservative — a false positive costs one wasted token
    /// re-acquisition, never a lost session.
    /// </para>
    /// </summary>
    public static class AuthFailureHeuristic
    {
        /// <summary>
        /// True when <paramref name="ex"/> or any inner exception reads like an auth rejection
        /// rather than a network error. Used to gate automatic re-authentication so a transient
        /// outage doesn't trigger a needless relogin.
        /// </summary>
        public static bool LooksLikeAuthFailure(System.Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message;
                if (!string.IsNullOrEmpty(m) &&
                    (m.IndexOf("Authentication", System.StringComparison.OrdinalIgnoreCase) >= 0
                     || m.IndexOf("Unauthorized", System.StringComparison.OrdinalIgnoreCase) >= 0
                     || m.IndexOf("401", System.StringComparison.Ordinal) >= 0))
                    return true;
            }
            return false;
        }
    }
}
