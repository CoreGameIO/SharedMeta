using System;

namespace SharedMeta.Core.Auth
{
    /// <summary>
    /// Result of a login operation.
    /// </summary>
    public class MetaLoginResult
    {
        /// <summary>JWT access token.</summary>
        public string Token { get; set; } = "";

        /// <summary>Player identifier.</summary>
        public string PlayerId { get; set; } = "";

        /// <summary>Whether this is a newly created player.</summary>
        public bool IsNewPlayer { get; set; }

        /// <summary>Access token expiration time (UTC).</summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>Long-lived refresh token. Exchange via MetaAuthClient.RefreshAsync for a new access token.</summary>
        public string RefreshToken { get; set; } = "";

        /// <summary>Refresh token expiration time (UTC).</summary>
        public DateTime RefreshExpiresAt { get; set; }
    }
}
