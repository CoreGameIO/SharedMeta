using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Logging;
#nullable enable

namespace SharedMeta.Client
{
    /// <summary>
    /// Cross-platform authentication client: login, refresh, account linking and token caching for
    /// one auth endpoint, over one <see cref="IMetaAuthProvider"/>.
    /// <para>
    /// Both are fixed at construction and cannot be swapped afterwards. That is the point: an app
    /// that talks to more than one backend (an in-process local one and a remote server, say) gives
    /// each its own client, so neither can end up answering the other's calls. Pair each client with
    /// its own <see cref="ITokenStorage"/> scope for the same reason — sharing one lets the
    /// credentials of one backend overwrite the other's.
    /// </para>
    /// <code>
    /// var auth   = new MetaAuthClient("https://host/meta/auth", new UnityMetaAuthProvider());
    /// var tokens = new MetaTokenManager(auth, deviceId, storage);
    /// </code>
    /// </summary>
    public sealed class MetaAuthClient
    {
        private readonly string _authUrl;
        private readonly IMetaAuthProvider _provider;

        /// <param name="authUrl">Auth endpoint base URL (e.g. "https://host/meta/auth"); the
        /// operation path (<c>/login</c>, <c>/refresh</c>, …) is appended per call.</param>
        /// <param name="provider">
        /// Transport for the auth calls. Required on Unity and netstandard —
        /// pass <c>UnityMetaAuthProvider</c>, <c>LocalMetaAuthProvider</c>, or your own. On other
        /// .NET targets it defaults to <see cref="HttpMetaAuthProvider"/>.
        /// </param>
        public MetaAuthClient(string authUrl, IMetaAuthProvider? provider = null)
        {
            _authUrl = (authUrl ?? throw new ArgumentNullException(nameof(authUrl))).TrimEnd('/');
            _provider = provider ?? DefaultProvider();
        }

        /// <summary>Auth endpoint this client talks to.</summary>
        public string AuthUrl => _authUrl;

        /// <summary>The provider serving this client's calls. Useful in diagnostics.</summary>
        public IMetaAuthProvider Provider => _provider;

        private static IMetaAuthProvider DefaultProvider()
        {
#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
            return new HttpMetaAuthProvider();
#else
            throw new ArgumentNullException("provider",
                "MetaAuthClient needs an explicit IMetaAuthProvider on this platform — " +
                "UnityMetaAuthProvider for a Unity client, LocalMetaAuthProvider for the in-process " +
                "backend, or your own implementation.");
#endif
        }

        /// <summary>
        /// Authenticate, reusing a cached token while it is still valid, refreshing when only the
        /// refresh token is, and falling back to a full device login otherwise.
        /// </summary>
        public async Task<MetaLoginResult> EnsureAuthenticatedAsync(
            string deviceId,
            ITokenStorage tokenStorage,
            CancellationToken cancellation = default)
        {
            var cached = tokenStorage.Load();
            if (cached != null && cached.IsValid)
            {
                MetaLog.Debug("[MetaAuth] Using cached token for player: " + cached.PlayerId);
                return new MetaLoginResult
                {
                    Token = cached.Token,
                    PlayerId = cached.PlayerId,
                    IsNewPlayer = false,
                    ExpiresAt = cached.ExpiresAt,
                    RefreshToken = cached.RefreshToken,
                    RefreshExpiresAt = cached.RefreshExpiresAt
                };
            }

            // Access token expired but the refresh token is still valid → exchange it for a fresh
            // access token (and a rotated refresh token) instead of a full re-login. If the refresh
            // fails (expired / revoked / reuse-detected) we fall through to a full login.
            if (cached != null && cached.RefreshValid)
            {
                try
                {
                    var refreshed = await RefreshAsync(cached.RefreshToken, cancellation);
                    tokenStorage.Save(new CachedToken(refreshed.Token, refreshed.PlayerId, refreshed.ExpiresAt,
                        refreshed.RefreshToken, refreshed.RefreshExpiresAt));
                    MetaLog.Debug("[MetaAuth] Access token refreshed for player: " + refreshed.PlayerId);
                    return refreshed;
                }
                catch (Exception ex)
                {
                    MetaLog.Warning("[MetaAuth] Refresh failed (" + ex.Message + ") — falling back to full login.");
                }
            }

            var result = await LoginAsync(deviceId, cancellation);

            tokenStorage.Save(new CachedToken(result.Token, result.PlayerId, result.ExpiresAt,
                result.RefreshToken, result.RefreshExpiresAt));
            MetaLog.Debug("[MetaAuth] Token cached for player: " + result.PlayerId);

            return result;
        }

        /// <summary>
        /// Exchange a refresh token for a new access token (and a rotated refresh token).
        /// Throws when the refresh token is invalid/expired/revoked.
        /// </summary>
        public Task<MetaLoginResult> RefreshAsync(
            string refreshToken,
            CancellationToken cancellation = default)
        {
            var url = Endpoint("/refresh");
            Trace("Refreshing token at: " + url);
            return _provider.RefreshAsync(url, refreshToken, cancellation);
        }

        /// <summary>Authenticate with a device id. Always calls the provider.</summary>
        public Task<MetaLoginResult> LoginAsync(
            string deviceId,
            CancellationToken cancellation = default)
        {
            var url = Endpoint("/login");
            Trace("Logging in at: " + url);
            return _provider.LoginAsync(url, deviceId, cancellation);
        }

        /// <summary>
        /// Login via platform (Google, Apple, Steam) instead of a device id. The server validates the
        /// platform token and answers with a JWT.
        /// </summary>
        public async Task<MetaLoginResult> LoginWithPlatformAsync(
            string platform,
            string platformToken,
            ITokenStorage? tokenStorage = null,
            CancellationToken cancellation = default)
        {
            var url = Endpoint("/login-platform");
            Trace("Platform login at: " + url + " platform: " + platform);

            var result = await _provider.LoginWithPlatformAsync(url, platform, platformToken, cancellation);

            if (tokenStorage != null)
            {
                tokenStorage.Save(new CachedToken(result.Token, result.PlayerId, result.ExpiresAt,
                    result.RefreshToken, result.RefreshExpiresAt));
                MetaLog.Debug("[MetaAuth] Platform token cached for player: " + result.PlayerId);
            }

            return result;
        }

        /// <summary>
        /// Link a platform account to the currently authenticated player. Requires a valid access
        /// token from a prior device or platform login.
        /// </summary>
        public Task<bool> LinkAccountAsync(
            string platform,
            string platformToken,
            string accessToken,
            CancellationToken cancellation = default)
        {
            var url = Endpoint("/link");
            Trace("Linking " + platform + " at: " + url);
            return _provider.LinkAsync(url, platform, platformToken, accessToken, cancellation);
        }

        /// <summary>Unlink an auth key from the current player. The last key cannot be unlinked.</summary>
        public Task<bool> UnlinkAsync(
            string authKey,
            string accessToken,
            CancellationToken cancellation = default)
        {
            var url = Endpoint("/unlink");
            Trace("Unlinking " + authKey + " at: " + url);
            return _provider.UnlinkAsync(url, authKey, accessToken, cancellation);
        }

        /// <summary>
        /// Reset (force-unlink) a device from the current player: the next login with this deviceId
        /// creates a new player. Clears <paramref name="tokenStorage"/> on success when supplied.
        /// </summary>
        public async Task<bool> ResetDeviceAsync(
            string deviceId,
            string accessToken,
            ITokenStorage? tokenStorage = null,
            CancellationToken cancellation = default)
        {
            var url = Endpoint("/reset-device");
            Trace("Resetting device at: " + url);

            var success = await _provider.ResetDeviceAsync(url, deviceId, accessToken, cancellation);

            if (success && tokenStorage != null)
            {
                tokenStorage.Clear();
                MetaLog.Debug("[MetaAuth] Token cleared after device reset");
            }

            return success;
        }

        /// <summary>Clear the cached token (logout).</summary>
        public static void ClearToken(ITokenStorage tokenStorage)
        {
            tokenStorage.Clear();
            MetaLog.Debug("[MetaAuth] Token cleared");
        }

        private string Endpoint(string path) => _authUrl + path;

        // The provider name rides along with the URL: a provider that answers in-process (local
        // backend, test double) contacts nothing, so a bare URL in the log implies a request that
        // never happened.
        private void Trace(string message)
            => MetaLog.Debug("[MetaAuth] " + message + " (via " + _provider.GetType().Name + ")");
    }
}
