#if UNITY_5_3_OR_NEWER
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Client;
using UnityEngine;
using UnityEngine.Networking;

namespace SharedMeta.Client.Auth
{
    /// <summary>
    /// Unity implementation of MetaAuth login using UnityWebRequest.
    /// Call <see cref="Register"/> once at startup to enable MetaAuth on Unity.
    /// </summary>
    public static class UnityMetaAuth
    {
        /// <summary>
        /// Register Unity login implementation with MetaAuth.
        /// Call this once before using MetaAuth.LoginAsync or MetaAuth.EnsureAuthenticatedAsync.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Register()
        {
            MetaAuth.LoginFunc = LoginUnityAsync;
        }

        private static async Task<MetaLoginResult> LoginUnityAsync(
            string url, string deviceId, CancellationToken cancellation)
        {
            var body = "{\"deviceId\":\"" + EscapeJson(deviceId) + "\"}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                cancellation.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(
                    $"Auth login failed (HTTP {request.responseCode}): {request.error}");

            var json = request.downloadHandler.text;
            return ParseLoginResponse(json);
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
