using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Logging;
#nullable enable

namespace SharedMeta.Client
{
    /// <summary>
    /// Cross-platform authentication helper.
    /// Handles login and token caching for both Unity and .NET.
    ///
    /// Extensibility: set <see cref="Provider"/> to route all auth calls through
    /// a custom <see cref="IMetaAuthProvider"/> — useful for local backends,
    /// alternative transports, or third-party auth services. The legacy
    /// <see cref="LoginFunc"/>/<see cref="PlatformLoginFunc"/>/<see cref="AuthActionFunc"/>/<see cref="UnlinkFunc"/>
    /// hooks are still honored as a fallback for backward compatibility.
    /// </summary>
    public static class MetaAuth
    {
        /// <summary>
        /// Active auth provider. When set, takes precedence over legacy Func hooks.
        /// On non-Unity .NET targets an <see cref="HttpMetaAuthProvider"/> is used by
        /// default when neither <see cref="Provider"/> nor the legacy Func hooks are set.
        /// </summary>
        public static IMetaAuthProvider? Provider { get; set; }

        /// <summary>
        /// Authenticate with the server, reusing a cached token if still valid.
        /// </summary>
        /// <param name="authUrl">Auth endpoint URL (e.g., "http://localhost:5100/meta/auth").</param>
        /// <param name="deviceId">Unique device identifier.</param>
        /// <param name="tokenStorage">Token storage for caching across sessions.</param>
        /// <param name="cancellation">Cancellation token.</param>
        /// <returns>Login result with token and player info.</returns>
        public static async Task<MetaLoginResult> EnsureAuthenticatedAsync(
            string authUrl,
            string deviceId,
            ITokenStorage tokenStorage,
            CancellationToken cancellation = default)
        {
            var cached = tokenStorage.Load();
            if (cached != null)
            {
                MetaLog.Debug("[MetaAuth] Using cached token for player: " + cached.PlayerId);
                return new MetaLoginResult
                {
                    Token = cached.Token,
                    PlayerId = cached.PlayerId,
                    IsNewPlayer = false,
                    ExpiresAt = cached.ExpiresAt
                };
            }

            var result = await LoginAsync(authUrl, deviceId, cancellation);

            tokenStorage.Save(new CachedToken(result.Token, result.PlayerId, result.ExpiresAt));
            MetaLog.Debug("[MetaAuth] Token cached for player: " + result.PlayerId);

            return result;
        }

        /// <summary>
        /// Authenticate with the server using a DeviceId. Always makes a network call.
        /// Priority: <see cref="Provider"/> → <see cref="LoginFunc"/> → default HttpClient (non-Unity only).
        /// </summary>
        /// <param name="authUrl">Auth endpoint URL (e.g., "http://localhost:5100/meta/auth").</param>
        /// <param name="deviceId">Unique device identifier.</param>
        /// <param name="cancellation">Cancellation token.</param>
        public static async Task<MetaLoginResult> LoginAsync(
            string authUrl,
            string deviceId,
            CancellationToken cancellation = default)
        {
            var url = authUrl.TrimEnd('/') + "/login";
            MetaLog.Debug("[MetaAuth] Logging in at: " + url);

            if (Provider != null)
                return await Provider.LoginAsync(url, deviceId, cancellation);

            if (LoginFunc != null)
                return await LoginFunc(url, deviceId, cancellation);

#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
            return await new HttpMetaAuthProvider().LoginAsync(url, deviceId, cancellation);
#else
            throw new PlatformNotSupportedException(
                "MetaAuth.LoginAsync requires either UnityMetaAuth.Register() (Unity) or a custom Provider. " +
                "Call UnityMetaAuth.Register() before using MetaAuth, or set MetaAuth.Provider manually.");
#endif
        }

        /// <summary>
        /// Login via platform (Google, Apple, Steam) instead of device ID.
        /// Validates platform token server-side and returns JWT.
        /// Priority: <see cref="Provider"/> → <see cref="PlatformLoginFunc"/> → default HttpClient (non-Unity only).
        /// </summary>
        public static async Task<MetaLoginResult> LoginWithPlatformAsync(
            string authUrl,
            string platform,
            string platformToken,
            ITokenStorage? tokenStorage = null,
            CancellationToken cancellation = default)
        {
            var url = authUrl.TrimEnd('/') + "/login-platform";
            MetaLog.Debug("[MetaAuth] Platform login at: " + url + " platform: " + platform);

            MetaLoginResult result;
            if (Provider != null)
            {
                result = await Provider.LoginWithPlatformAsync(url, platform, platformToken, cancellation);
            }
            else if (PlatformLoginFunc != null)
            {
                result = await PlatformLoginFunc(url, platform, platformToken, cancellation);
            }
            else
            {
#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
                result = await new HttpMetaAuthProvider().LoginWithPlatformAsync(url, platform, platformToken, cancellation);
#else
                throw new PlatformNotSupportedException(
                    "MetaAuth.LoginWithPlatformAsync requires UnityMetaAuth or a custom Provider.");
#endif
            }

            if (tokenStorage != null)
            {
                tokenStorage.Save(new CachedToken(result.Token, result.PlayerId, result.ExpiresAt));
                MetaLog.Debug("[MetaAuth] Platform token cached for player: " + result.PlayerId);
            }

            return result;
        }

        /// <summary>
        /// Link a platform account to the currently authenticated player.
        /// Requires a valid JWT token (from prior device or platform login).
        /// Priority: <see cref="Provider"/> → <see cref="AuthActionFunc"/> → default HttpClient (non-Unity only).
        /// </summary>
        public static async Task<bool> LinkAccountAsync(
            string authUrl,
            string platform,
            string platformToken,
            string accessToken,
            CancellationToken cancellation = default)
        {
            var url = authUrl.TrimEnd('/') + "/link";
            MetaLog.Debug("[MetaAuth] Linking " + platform + " at: " + url);

            if (Provider != null)
                return await Provider.LinkAsync(url, platform, platformToken, accessToken, cancellation);

            if (AuthActionFunc != null)
                return await AuthActionFunc(url, platform, platformToken, accessToken, cancellation);

#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
            return await new HttpMetaAuthProvider().LinkAsync(url, platform, platformToken, accessToken, cancellation);
#else
            throw new PlatformNotSupportedException(
                "MetaAuth.LinkAccountAsync requires UnityMetaAuth or a custom Provider.");
#endif
        }

        /// <summary>
        /// Unlink an auth key from the currently authenticated player.
        /// Cannot unlink the last key.
        /// Priority: <see cref="Provider"/> → <see cref="UnlinkFunc"/> → default HttpClient (non-Unity only).
        /// </summary>
        public static async Task<bool> UnlinkAsync(
            string authUrl,
            string authKey,
            string accessToken,
            CancellationToken cancellation = default)
        {
            var url = authUrl.TrimEnd('/') + "/unlink";
            MetaLog.Debug("[MetaAuth] Unlinking " + authKey + " at: " + url);

            if (Provider != null)
                return await Provider.UnlinkAsync(url, authKey, accessToken, cancellation);

            if (UnlinkFunc != null)
                return await UnlinkFunc(url, authKey, accessToken, cancellation);

#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
            return await new HttpMetaAuthProvider().UnlinkAsync(url, authKey, accessToken, cancellation);
#else
            throw new PlatformNotSupportedException(
                "MetaAuth.UnlinkAsync requires UnityMetaAuth or a custom Provider.");
#endif
        }

        /// <summary>
        /// Clear cached token (logout).
        /// </summary>
        public static void ClearToken(ITokenStorage tokenStorage)
        {
            tokenStorage.Clear();
            MetaLog.Debug("[MetaAuth] Token cleared");
        }

        // ============================================
        // Legacy Func-based hooks (kept for backward compatibility with UnityMetaAuth).
        // Prefer setting MetaAuth.Provider for new code.
        // ============================================

        /// <summary>Legacy platform-specific login function. Prefer <see cref="Provider"/>.</summary>
        public static Func<string, string, CancellationToken, Task<MetaLoginResult>>? LoginFunc { get; set; }

        /// <summary>Legacy platform-specific platform login function. Prefer <see cref="Provider"/>.</summary>
        public static Func<string, string, string, CancellationToken, Task<MetaLoginResult>>? PlatformLoginFunc { get; set; }

        /// <summary>Legacy platform-specific link function. Prefer <see cref="Provider"/>.</summary>
        public static Func<string, string, string, string, CancellationToken, Task<bool>>? AuthActionFunc { get; set; }

        /// <summary>Legacy platform-specific unlink function. Prefer <see cref="Provider"/>.</summary>
        public static Func<string, string, string, CancellationToken, Task<bool>>? UnlinkFunc { get; set; }
    }
}
