using System;

#nullable enable

namespace SharedMeta.Core.Auth
{
    /// <summary>
    /// Cached login result for token reuse across sessions.
    /// </summary>
    public class CachedToken
    {
        /// <summary>JWT access token.</summary>
        public string Token { get; }

        /// <summary>Player identifier.</summary>
        public string PlayerId { get; }

        /// <summary>Access token expiration time (UTC).</summary>
        public DateTime ExpiresAt { get; }

        /// <summary>Long-lived refresh token (empty if the server didn't issue one).</summary>
        public string RefreshToken { get; }

        /// <summary>Refresh token expiration time (UTC).</summary>
        public DateTime RefreshExpiresAt { get; }

        public CachedToken(string token, string playerId, DateTime expiresAt)
            : this(token, playerId, expiresAt, "", default)
        {
        }

        public CachedToken(string token, string playerId, DateTime expiresAt,
            string refreshToken, DateTime refreshExpiresAt)
        {
            Token = token;
            PlayerId = playerId;
            ExpiresAt = expiresAt;
            RefreshToken = refreshToken ?? "";
            RefreshExpiresAt = refreshExpiresAt;
        }

        /// <summary>
        /// Whether the access token is still valid (with a 5-minute safety margin).
        /// </summary>
        public bool IsValid => ExpiresAt > DateTime.UtcNow.AddMinutes(5);

        /// <summary>
        /// Whether a usable refresh token is present (non-empty and not yet expired). When the access
        /// token is no longer <see cref="IsValid"/> but this is true, call MetaAuth.RefreshAsync instead
        /// of a full re-login.
        /// </summary>
        public bool RefreshValid => !string.IsNullOrEmpty(RefreshToken) && RefreshExpiresAt > DateTime.UtcNow;
    }

    /// <summary>
    /// Abstraction for persisting auth tokens across app sessions.
    /// Implement per platform: PlayerPrefs (Unity), file system, secure storage, etc.
    /// </summary>
    public interface ITokenStorage
    {
        /// <summary>
        /// Load the cached token, or null if none is stored or it has no usable credential left
        /// (both the access token and the refresh token are expired). A token whose access part is
        /// expired but whose refresh part is still valid is returned so the caller can refresh.
        /// </summary>
        CachedToken? Load();

        /// <summary>Save token for future sessions.</summary>
        void Save(CachedToken token);

        /// <summary>Clear stored token (logout).</summary>
        void Clear();
    }
}
