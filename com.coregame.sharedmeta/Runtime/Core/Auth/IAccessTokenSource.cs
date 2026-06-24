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
}
