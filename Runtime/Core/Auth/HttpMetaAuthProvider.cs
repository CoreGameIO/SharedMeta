#if !NETSTANDARD2_1 && !UNITY_5_3_OR_NEWER
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace SharedMeta.Core.Auth
{
    /// <summary>
    /// Default <see cref="IMetaAuthProvider"/> that talks to a real auth HTTP endpoint
    /// via <see cref="HttpClient"/>. Used automatically by <c>MetaAuth</c> on non-Unity .NET
    /// targets when no custom provider is set.
    /// </summary>
    public class HttpMetaAuthProvider : IMetaAuthProvider
    {
        public async Task<MetaLoginResult> LoginAsync(
            string authUrl, string deviceId, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            var response = await http.PostAsJsonAsync(
                authUrl, new { DeviceId = deviceId }, cancellation);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MetaLoginResult>(
                cancellationToken: cancellation);
            return result ?? throw new InvalidOperationException("Login returned null response");
        }

        public async Task<MetaLoginResult> LoginWithPlatformAsync(
            string authUrl, string platform, string platformToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            var response = await http.PostAsJsonAsync(
                authUrl, new { Platform = platform, PlatformToken = platformToken }, cancellation);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MetaLoginResult>(
                cancellationToken: cancellation);
            return result ?? throw new InvalidOperationException("Platform login returned null response");
        }

        public async Task<MetaLoginResult> RefreshAsync(
            string authUrl, string refreshToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            var response = await http.PostAsJsonAsync(
                authUrl, new { RefreshToken = refreshToken }, cancellation);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<MetaLoginResult>(
                cancellationToken: cancellation);
            return result ?? throw new InvalidOperationException("Refresh returned null response");
        }

        public async Task<bool> LinkAsync(
            string authUrl, string platform, string platformToken, string accessToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.PostAsJsonAsync(
                authUrl, new { Platform = platform, PlatformToken = platformToken }, cancellation);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> UnlinkAsync(
            string authUrl, string authKey, string accessToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.PostAsJsonAsync(
                authUrl, new { AuthKey = authKey }, cancellation);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> ResetDeviceAsync(
            string authUrl, string deviceId, string accessToken, CancellationToken cancellation)
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.PostAsJsonAsync(
                authUrl, new { DeviceId = deviceId }, cancellation);
            return response.IsSuccessStatusCode;
        }
    }
}
#endif
