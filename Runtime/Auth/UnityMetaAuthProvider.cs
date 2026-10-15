#if UNITY_5_3_OR_NEWER
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core.Auth;
using UnityEngine;
using UnityEngine.Networking;

namespace SharedMeta.Client.Auth
{
    /// <summary>
    /// Unity <see cref="IMetaAuthProvider"/> over <see cref="UnityWebRequest"/>. Pass an instance to
    /// the <c>MetaAuthClient</c> that talks to your remote server:
    /// <code>
    /// var auth = new MetaAuthClient(authUrl, new UnityMetaAuthProvider());
    /// </code>
    /// Stateless and thread-safe; one instance per client, or one shared, both work.
    /// </summary>
    public sealed class UnityMetaAuthProvider : IMetaAuthProvider
    {
        // Unity's main-thread SynchronizationContext + thread id, captured on load (which runs on the
        // main thread). UnityWebRequest can only be created/sent from the main thread, but auth calls
        // may arrive on a background thread — e.g. SignalR resolves the access-token provider during
        // its (off-thread) connect handshake. PostJsonAsync marshals onto this context when called
        // off-thread so login/refresh works regardless of the caller's thread.
        private static SynchronizationContext _mainThreadContext;
        private static int _mainThreadId;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureMainThread()
        {
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public async Task<MetaLoginResult> RefreshAsync(
            string authUrl, string refreshToken, CancellationToken cancellation)
        {
            var body = "{\"refreshToken\":\"" + EscapeJson(refreshToken) + "\"}";
            var json = await PostJsonAsync(authUrl, body, null, cancellation);
            return ParseLoginResponse(json);
        }

        public async Task<MetaLoginResult> LoginAsync(
            string authUrl, string deviceId, CancellationToken cancellation)
        {
            var body = "{\"deviceId\":\"" + EscapeJson(deviceId) + "\"}";
            var json = await PostJsonAsync(authUrl, body, null, cancellation);
            return ParseLoginResponse(json);
        }

        public async Task<MetaLoginResult> LoginWithPlatformAsync(
            string authUrl, string platform, string platformToken, CancellationToken cancellation)
        {
            var body = "{\"platform\":\"" + EscapeJson(platform) +
                       "\",\"platformToken\":\"" + EscapeJson(platformToken) + "\"}";
            var json = await PostJsonAsync(authUrl, body, null, cancellation);
            return ParseLoginResponse(json);
        }

        public async Task<bool> LinkAsync(
            string authUrl, string platform, string platformToken, string accessToken, CancellationToken cancellation)
        {
            var body = "{\"platform\":\"" + EscapeJson(platform) +
                       "\",\"platformToken\":\"" + EscapeJson(platformToken) + "\"}";
            var json = await PostJsonAsync(authUrl, body, accessToken, cancellation);
            return ExtractJsonBool(json, "success");
        }

        public async Task<bool> UnlinkAsync(
            string authUrl, string authKey, string accessToken, CancellationToken cancellation)
        {
            var body = "{\"authKey\":\"" + EscapeJson(authKey) + "\"}";
            var json = await PostJsonAsync(authUrl, body, accessToken, cancellation);
            return ExtractJsonBool(json, "success");
        }

        public async Task<bool> ResetDeviceAsync(
            string authUrl, string deviceId, string accessToken, CancellationToken cancellation)
        {
            var body = "{\"deviceId\":\"" + EscapeJson(deviceId) + "\"}";
            var json = await PostJsonAsync(authUrl, body, accessToken, cancellation);
            return ExtractJsonBool(json, "success");
        }

        // Marshal onto Unity's main thread when invoked from a background thread (UnityWebRequest is
        // main-thread-only). When already on the main thread, runs inline.
        private static Task<string> PostJsonAsync(
            string url, string body, string accessToken, CancellationToken cancellation)
        {
            if (_mainThreadContext == null || Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                return PostJsonOnMainThreadAsync(url, body, accessToken, cancellation);

            var tcs = new TaskCompletionSource<string>();
            _mainThreadContext.Post(async _ =>
            {
                try { tcs.TrySetResult(await PostJsonOnMainThreadAsync(url, body, accessToken, cancellation)); }
                catch (OperationCanceledException) { tcs.TrySetCanceled(); }
                catch (Exception e) { tcs.TrySetException(e); }
            }, null);
            return tcs.Task;
        }

        private static async Task<string> PostJsonOnMainThreadAsync(
            string url, string body, string accessToken, CancellationToken cancellation)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            if (!string.IsNullOrEmpty(accessToken))
                request.SetRequestHeader("Authorization", "Bearer " + accessToken);

            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                cancellation.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(
                    $"Auth request failed (HTTP {request.responseCode}): {request.downloadHandler.text}");

            return request.downloadHandler.text;
        }

        private static MetaLoginResult ParseLoginResponse(string json)
        {
            var result = new MetaLoginResult();
            result.Token = ExtractJsonString(json, "token");
            result.PlayerId = ExtractJsonString(json, "playerId");
            result.IsNewPlayer = ExtractJsonBool(json, "isNewPlayer");
            var expiresStr = ExtractJsonString(json, "expiresAt");
            if (DateTime.TryParse(expiresStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var exp))
                result.ExpiresAt = exp;

            result.RefreshToken = ExtractJsonString(json, "refreshToken");
            var refreshExpiresStr = ExtractJsonString(json, "refreshExpiresAt");
            if (DateTime.TryParse(refreshExpiresStr, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var refreshExp))
                result.RefreshExpiresAt = refreshExp;
            return result;
        }

        private static string ExtractJsonString(string json, string key)
        {
            var search = "\"" + key + "\":\"";
            var idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            var start = idx + search.Length;
            var end = json.IndexOf("\"", start, StringComparison.Ordinal);
            return end < 0 ? "" : json.Substring(start, end - start);
        }

        private static bool ExtractJsonBool(string json, string key)
        {
            var search = "\"" + key + "\":";
            var idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var start = idx + search.Length;
            return json.Length > start + 4 &&
                   json.Substring(start, 4).Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
#endif
