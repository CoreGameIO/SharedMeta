using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core.Auth;
using SharedMeta.Core.Logging;
#nullable enable
#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
using System.Net.Http;
using System.Net.Http.Json;
#endif

namespace SharedMeta.Client
{
    /// <summary>
    /// Cross-platform authentication helper.
    /// Handles login and token caching for both Unity and .NET.
    /// </summary>
    public static class MetaAuth
    {
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
        /// Platform-specific login function. Set this to override the default HTTP implementation.
        /// On Unity, this is set automatically by <c>UnityMetaAuth</c> (SharedMeta.Auth.Client assembly).
        /// </summary>
        public static Func<string, string, CancellationToken, Task<MetaLoginResult>>? LoginFunc { get; set; }

        /// <summary>
        /// Authenticate with the server using a DeviceId. Always makes a network call.
        /// On Unity: uses UnityWebRequest (requires UnityMetaAuth.Register() or auto-registration).
        /// On .NET: uses HttpClient.
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

            if (LoginFunc != null)
                return await LoginFunc(url, deviceId, cancellation);

#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
            return await LoginHttpClientAsync(url, deviceId, cancellation);
#else
            throw new PlatformNotSupportedException(
                "MetaAuth.LoginAsync requires either UnityMetaAuth.Register() (Unity) or net8.0+. " +
                "Call UnityMetaAuth.Register() before using MetaAuth, or set MetaAuth.LoginFunc manually.");
#endif
        }

        /// <summary>
        /// Login via platform (Google, Apple, Steam) instead of device ID.
        /// Validates platform token server-side and returns JWT.
        /// </summary>
        /// <param name="authUrl">Auth endpoint URL (e.g., "http://localhost:5100/meta/auth").</param>
        /// <param name="platform">Platform name: "google", "apple", "steam".</param>
        /// <param name="platformToken">Token/ticket from the platform SDK.</param>
        /// <param name="tokenStorage">Optional: cache the resulting token.</param>
        /// <param name="cancellation">Cancellation token.</param>
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
            if (PlatformLoginFunc != null)
                result = await PlatformLoginFunc(url, platform, platformToken, cancellation);
            else
            {
#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
                result = await PlatformLoginHttpClientAsync(url, platform, platformToken, cancellation);
#else
                throw new PlatformNotSupportedException(
                    "MetaAuth.LoginWithPlatformAsync requires UnityMetaAuth or net8.0+.");
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

            if (AuthActionFunc != null)
                return await AuthActionFunc(url, platform, platformToken, accessToken, cancellation);

#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
            return await LinkHttpClientAsync(url, platform, platformToken, accessToken, cancellation);
#else
            throw new PlatformNotSupportedException(
                "MetaAuth.LinkAccountAsync requires UnityMetaAuth or net8.0+.");
#endif
        }

        /// <summary>
        /// Unlink an auth key from the currently authenticated player.
        /// Cannot unlink the last key.
        /// </summary>
        public static async Task<bool> UnlinkAsync(
            string authUrl,
            string authKey,
            string accessToken,
            CancellationToken cancellation = default)
        {
            var url = authUrl.TrimEnd('/') + "/unlink";
            MetaLog.Debug("[MetaAuth] Unlinking " + authKey + " at: " + url);

            if (UnlinkFunc != null)
                return await UnlinkFunc(url, authKey, accessToken, cancellation);

#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
            return await UnlinkHttpClientAsync(url, authKey, accessToken, cancellation);
#else
            throw new PlatformNotSupportedException(
                "MetaAuth.UnlinkAsync requires UnityMetaAuth or net8.0+.");
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

        /// <summary>Platform-specific platform login function (set by UnityMetaAuth).</summary>
        public static Func<string, string, string, CancellationToken, Task<MetaLoginResult>>? PlatformLoginFunc { get; set; }

        /// <summary>Platform-specific link function (set by UnityMetaAuth).</summary>
        public static Func<string, string, string, string, CancellationToken, Task<bool>>? AuthActionFunc { get; set; }

        /// <summary>Platform-specific unlink function (set by UnityMetaAuth).</summary>
        public static Func<string, string, string, CancellationToken, Task<bool>>? UnlinkFunc { get; set; }

#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
        private static async Task<MetaLoginResult> LoginHttpClientAsync(
            string url, string deviceId, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            var response = await http.PostAsJsonAsync(url, new { DeviceId = deviceId }, cancellation);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MetaLoginResult>(cancellationToken: cancellation);
            return result ?? throw new InvalidOperationException("Login returned null response");
        }

        private static async Task<MetaLoginResult> PlatformLoginHttpClientAsync(
            string url, string platform, string platformToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            var response = await http.PostAsJsonAsync(url,
                new { Platform = platform, PlatformToken = platformToken }, cancellation);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MetaLoginResult>(cancellationToken: cancellation);
            return result ?? throw new InvalidOperationException("Platform login returned null response");
        }

        private static async Task<bool> LinkHttpClientAsync(
            string url, string platform, string platformToken, string accessToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.PostAsJsonAsync(url,
                new { Platform = platform, PlatformToken = platformToken }, cancellation);
            return response.IsSuccessStatusCode;
        }

        private static async Task<bool> UnlinkHttpClientAsync(
            string url, string authKey, string accessToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.PostAsJsonAsync(url, new { AuthKey = authKey }, cancellation);
            return response.IsSuccessStatusCode;
        }
#endif
    }
}
