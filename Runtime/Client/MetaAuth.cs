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
        /// Clear cached token (logout).
        /// </summary>
        public static void ClearToken(ITokenStorage tokenStorage)
        {
            tokenStorage.Clear();
            MetaLog.Debug("[MetaAuth] Token cleared");
        }

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
#endif
    }
}
