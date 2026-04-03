using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;

namespace SharedMeta.Auth.Apple
{
    /// <summary>
    /// Configuration for Sign in with Apple authentication.
    /// </summary>
    public class AppleAuthOptions
    {
        /// <summary>
        /// Your app's bundle ID (e.g. "com.yourcompany.yourgame").
        /// Used to validate the 'aud' claim in Apple's identity token.
        /// </summary>
        public string BundleId { get; set; } = "";
    }

    /// <summary>
    /// Validates Sign in with Apple identity tokens.
    ///
    /// Flow:
    /// 1. Client uses ASAuthorizationAppleIDProvider → gets identity token (JWT)
    /// 2. Client sends identity token to SharedMeta server as platformToken
    /// 3. Server downloads Apple's public keys (JWKS) from appleid.apple.com
    /// 4. Server verifies JWT signature (RS256) using Apple's public keys
    /// 5. Server validates claims: iss, aud (bundle ID), exp
    /// 6. Server extracts stable Apple user ID from 'sub' claim
    ///
    /// Why reliable:
    /// - JWT is signed by Apple's private key (RS256) — cannot be forged
    /// - Server verifies signature against Apple's published JWKS public keys
    /// - 'aud' check ensures token was issued for this specific app
    /// - 'iss' check ensures token comes from Apple (not a different IdP)
    /// - 'sub' is a stable, per-app user identifier from Apple
    /// </summary>
    public class AppleAuthValidator : IExternalAuthValidator
    {
        private readonly AppleAuthOptions _options;
        private readonly HttpClient _http;
        private readonly JwtSecurityTokenHandler _tokenHandler = new();

        private const string AppleIssuer = "https://appleid.apple.com";
        private const string AppleKeysUrl = "https://appleid.apple.com/auth/keys";

        // Cached keys with expiry
        private IList<JsonWebKey>? _cachedKeys;
        private DateTime _cacheExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

        public string Platform => "apple";

        public AppleAuthValidator(AppleAuthOptions options, HttpClient? httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.BundleId))
                throw new ArgumentException("BundleId is required", nameof(options));
            _http = httpClient ?? new HttpClient();
        }

        public async Task<ExternalAuthResult> ValidateAsync(string platformToken)
        {
            if (string.IsNullOrEmpty(platformToken))
                throw new ExternalAuthException("Identity token is empty");

            var keys = await GetAppleKeysAsync();

            var validationParams = new TokenValidationParameters
            {
                ValidIssuer = AppleIssuer,
                ValidAudience = _options.BundleId,
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
            };

            try
            {
                var principal = _tokenHandler.ValidateToken(platformToken, validationParams, out _);
                var sub = principal.FindFirst("sub")?.Value;
                if (string.IsNullOrEmpty(sub))
                    throw new ExternalAuthException("Apple identity token has no sub claim");

                var email = principal.FindFirst("email")?.Value;
                return new ExternalAuthResult
                {
                    PlatformUserId = sub,
                    DisplayName = email
                };
            }
            catch (SecurityTokenException ex)
            {
                throw new ExternalAuthException($"Apple token validation failed: {ex.Message}", ex);
            }
        }

        private async Task<IList<JsonWebKey>> GetAppleKeysAsync()
        {
            if (_cachedKeys != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedKeys;

            await _cacheLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (_cachedKeys != null && DateTime.UtcNow < _cacheExpiry)
                    return _cachedKeys;

                HttpResponseMessage response;
                try
                {
                    response = await _http.GetAsync(AppleKeysUrl);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex)
                {
                    throw new ExternalAuthException("Failed to fetch Apple JWKS keys", ex);
                }

                var jwks = await response.Content.ReadFromJsonAsync<AppleJwks>();
                if (jwks?.Keys == null || jwks.Keys.Count == 0)
                    throw new ExternalAuthException("Apple JWKS response has no keys");

                _cachedKeys = jwks.Keys.Select(k =>
                {
                    var jwk = new JsonWebKey
                    {
                        Kty = k.Kty,
                        Kid = k.Kid,
                        Use = k.Use,
                        Alg = k.Alg,
                        N = k.N,
                        E = k.E,
                    };
                    return jwk;
                }).ToList();
                _cacheExpiry = DateTime.UtcNow + CacheDuration;

                return _cachedKeys;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private class AppleJwks
        {
            [JsonPropertyName("keys")]
            public List<AppleJwk>? Keys { get; set; }
        }

        private class AppleJwk
        {
            [JsonPropertyName("kty")] public string Kty { get; set; } = "";
            [JsonPropertyName("kid")] public string Kid { get; set; } = "";
            [JsonPropertyName("use")] public string Use { get; set; } = "";
            [JsonPropertyName("alg")] public string Alg { get; set; } = "";
            [JsonPropertyName("n")] public string N { get; set; } = "";
            [JsonPropertyName("e")] public string E { get; set; } = "";
        }
    }
}
