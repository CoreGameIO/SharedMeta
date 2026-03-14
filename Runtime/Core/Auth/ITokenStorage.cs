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

        /// <summary>Token expiration time (UTC).</summary>
        public DateTime ExpiresAt { get; }

        public CachedToken(string token, string playerId, DateTime expiresAt)
        {
            Token = token;
            PlayerId = playerId;
            ExpiresAt = expiresAt;
        }

        /// <summary>
        /// Whether the token is still valid (with a 5-minute safety margin).
        /// </summary>
        public bool IsValid => ExpiresAt > DateTime.UtcNow.AddMinutes(5);
    }

    /// <summary>
    /// Abstraction for persisting auth tokens across app sessions.
    /// Implement per platform: PlayerPrefs (Unity), file system, secure storage, etc.
    /// </summary>
    public interface ITokenStorage
    {
        /// <summary>Load cached token, or null if none stored or expired.</summary>
        CachedToken? Load();

        /// <summary>Save token for future sessions.</summary>
        void Save(CachedToken token);

        /// <summary>Clear stored token (logout).</summary>
        void Clear();
    }
}
