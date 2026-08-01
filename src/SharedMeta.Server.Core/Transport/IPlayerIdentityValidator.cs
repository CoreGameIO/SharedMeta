using System.Threading.Tasks;

namespace SharedMeta.Server.Core.Transport
{
    /// <summary>
    /// Checks that a PlayerId taken from an authenticated token still corresponds to a live
    /// account in the host's auth store.
    /// <para>
    /// A JWT is stateless: it carries a signature, an expiry and a <c>sub</c> claim, and nothing
    /// more. Wiping the auth store (fresh environment, dropped volume, deleted account) therefore
    /// does not invalidate tokens already in client hands — until the access token expires the
    /// client keeps presenting a PlayerId the server no longer knows, the transport trusts the
    /// claim, and the entity grains lazily materialise empty state under that id. The result is an
    /// orphan profile with no auth record behind it.
    /// </para>
    /// <para>
    /// Registering an implementation closes that window: <c>SessionConnect</c> rejects the unknown
    /// identity with <c>SessionConnectFailureReason.IdentityUnknown</c>, which the client treats as
    /// an auth failure — it drops the cached token and performs a full login, receiving a real
    /// PlayerId. Hosts that use <c>SharedMeta.Auth</c> get an implementation registered by
    /// <c>AddMetaAuth</c>; hosts with their own identity provider supply their own. With no
    /// implementation in DI the check is skipped entirely.
    /// </para>
    /// </summary>
    public interface IPlayerIdentityValidator
    {
        /// <summary>
        /// True when <paramref name="playerId"/> is a known account. Must return true for a player
        /// created moments ago by a login — an implementation that lags behind account creation
        /// would reject the very client it just issued a token to.
        /// </summary>
        Task<bool> ExistsAsync(string playerId);
    }
}
