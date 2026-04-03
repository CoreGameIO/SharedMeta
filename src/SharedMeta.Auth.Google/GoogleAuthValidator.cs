using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SharedMeta.Auth.Google
{
    /// <summary>
    /// Configuration for Google Play Games authentication.
    /// Get these from Google Cloud Console → APIs &amp; Services → Credentials.
    /// </summary>
    public class GoogleAuthOptions
    {
        /// <summary>OAuth2 Web Client ID (not the Android/iOS client ID).</summary>
        public string ClientId { get; set; } = "";

        /// <summary>OAuth2 Web Client Secret.</summary>
        public string ClientSecret { get; set; } = "";
    }

    /// <summary>
    /// Validates Google Play Games server auth codes.
    ///
    /// Flow:
    /// 1. Client calls Google Play Games SDK RequestServerSideAccess → gets server auth code
    /// 2. Client sends auth code to SharedMeta server as platformToken
    /// 3. Server exchanges auth code for tokens via Google OAuth2 API (requires client secret)
    /// 4. Server extracts stable Google user ID (sub) from id_token
    ///
    /// Why reliable:
    /// - Auth code is one-time-use, issued by Google for this specific app
    /// - Exchange requires client_secret (server-side only, never on client)
    /// - Google returns a signed id_token (JWT) with the user's stable sub claim
    /// - Cannot be forged without Google's signing key
    /// </summary>
    public class GoogleAuthValidator : IExternalAuthValidator
    {
        private readonly GoogleAuthOptions _options;
        private readonly HttpClient _http;
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

        public string Platform => "google";

        public GoogleAuthValidator(GoogleAuthOptions options, HttpClient? httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.ClientId))
                throw new ArgumentException("ClientId is required", nameof(options));
            if (string.IsNullOrEmpty(options.ClientSecret))
                throw new ArgumentException("ClientSecret is required", nameof(options));
            _http = httpClient ?? new HttpClient();
        }

        public async Task<ExternalAuthResult> ValidateAsync(string platformToken)
        {
            if (string.IsNullOrEmpty(platformToken))
                throw new ExternalAuthException("Platform token (server auth code) is empty");

            // Exchange auth code for tokens
            var content = new FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("code", platformToken),
                new System.Collections.Generic.KeyValuePair<string, string>("client_id", _options.ClientId),
                new System.Collections.Generic.KeyValuePair<string, string>("client_secret", _options.ClientSecret),
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "authorization_code"),
                new System.Collections.Generic.KeyValuePair<string, string>("redirect_uri", ""),
            });

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync(TokenEndpoint, content);
            }
            catch (Exception ex)
            {
                throw new ExternalAuthException("Failed to contact Google OAuth2 API", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new ExternalAuthException($"Google token exchange failed ({response.StatusCode}): {error}");
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>();
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.IdToken))
                throw new ExternalAuthException("Google returned no id_token");

            // Decode id_token payload (JWT) to get sub
            var sub = ExtractSubFromJwt(tokenResponse.IdToken);
            if (string.IsNullOrEmpty(sub))
                throw new ExternalAuthException("Google id_token has no sub claim");

            return new ExternalAuthResult { PlatformUserId = sub };
        }

        /// <summary>
        /// Extract 'sub' from JWT payload without full signature verification.
        /// Signature was implicitly verified by Google during the token exchange
        /// (we sent client_secret, Google returned a fresh id_token).
        /// </summary>
        private static string? ExtractSubFromJwt(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1];
            // Fix base64url padding
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            payload = payload.Replace('-', '+').Replace('_', '/');

            var bytes = Convert.FromBase64String(payload);
            var json = System.Text.Encoding.UTF8.GetString(bytes);

            // Simple extraction — avoid System.Text.Json dependency for just one field
            var subIdx = json.IndexOf("\"sub\"", StringComparison.Ordinal);
            if (subIdx < 0) return null;
            var colonIdx = json.IndexOf(':', subIdx);
            if (colonIdx < 0) return null;
            var quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return null;
            var quoteEnd = json.IndexOf('"', quoteStart + 1);
            return quoteEnd < 0 ? null : json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private class GoogleTokenResponse
        {
            [JsonPropertyName("id_token")]
            public string? IdToken { get; set; }

            [JsonPropertyName("access_token")]
            public string? AccessToken { get; set; }
        }
    }
}
