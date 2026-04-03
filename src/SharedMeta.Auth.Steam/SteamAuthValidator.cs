using System;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SharedMeta.Auth.Steam
{
    /// <summary>
    /// Configuration for Steam authentication.
    /// Get these from Steamworks Partner site → Web API Keys.
    /// </summary>
    public class SteamAuthOptions
    {
        /// <summary>Steam Web API Key (Publisher key from partner.steamgames.com).</summary>
        public string WebApiKey { get; set; } = "";

        /// <summary>Your Steam App ID.</summary>
        public uint AppId { get; set; }
    }

    /// <summary>
    /// Validates Steam session tickets via Steam Web API.
    ///
    /// Flow:
    /// 1. Client calls Steamworks SDK GetAuthSessionTicket → gets encrypted binary ticket
    /// 2. Client hex-encodes the ticket and sends to SharedMeta server as platformToken
    /// 3. Server sends ticket to Steam Web API ISteamUserAuth/AuthenticateUserTicket
    /// 4. Steam validates the ticket cryptographically and returns the SteamID64
    ///
    /// Why reliable:
    /// - Session ticket is cryptographically signed by Steam client on user's machine
    /// - Only Valve's servers can verify the signature (via AuthenticateUserTicket)
    /// - Requires Publisher Web API Key — without it, cannot validate tickets
    /// - Steam checks that the ticket was issued for the correct AppId
    /// - SteamID64 is the permanent, immutable user identifier
    /// - Ticket is single-use and expires quickly — replay attacks are impractical
    /// </summary>
    public class SteamAuthValidator : IExternalAuthValidator
    {
        private readonly SteamAuthOptions _options;
        private readonly HttpClient _http;

        private const string SteamApiUrl =
            "https://partner.steam-api.com/ISteamUserAuth/AuthenticateUserTicket/v1/";

        public string Platform => "steam";

        public SteamAuthValidator(SteamAuthOptions options, HttpClient? httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.WebApiKey))
                throw new ArgumentException("WebApiKey is required", nameof(options));
            if (options.AppId == 0)
                throw new ArgumentException("AppId is required", nameof(options));
            _http = httpClient ?? new HttpClient();
        }

        public async Task<ExternalAuthResult> ValidateAsync(string platformToken)
        {
            if (string.IsNullOrEmpty(platformToken))
                throw new ExternalAuthException("Session ticket is empty");

            var url = $"{SteamApiUrl}?key={_options.WebApiKey}" +
                      $"&appid={_options.AppId}" +
                      $"&ticket={platformToken}";

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(url);
            }
            catch (Exception ex)
            {
                throw new ExternalAuthException("Failed to contact Steam Web API", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new ExternalAuthException($"Steam API returned {response.StatusCode}: {error}");
            }

            var json = await response.Content.ReadAsStringAsync();

            // Parse response: {"response":{"params":{"result":"OK","steamid":"765...","ownersteamid":"765..."}}}
            var steamId = ExtractSteamId(json);
            if (string.IsNullOrEmpty(steamId))
                throw new ExternalAuthException("Steam API returned no SteamID");

            // Check result is OK
            var result = ExtractJsonValue(json, "result");
            if (result != "OK")
                throw new ExternalAuthException($"Steam ticket validation failed: {result}");

            return new ExternalAuthResult { PlatformUserId = steamId };
        }

        private static string? ExtractSteamId(string json)
        {
            return ExtractJsonValue(json, "steamid");
        }

        private static string? ExtractJsonValue(string json, string key)
        {
            var search = "\"" + key + "\":\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0)
            {
                // Try without quotes (for non-string values like "result":"OK")
                search = "\"" + key + "\":\"";
                idx = json.IndexOf(search, StringComparison.Ordinal);
                if (idx < 0) return null;
            }
            var start = idx + search.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }
    }
}
