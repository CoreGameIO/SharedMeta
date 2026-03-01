using System;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Login request from client.
    /// </summary>
    public class LoginRequest
    {
        /// <summary>Unique device identifier.</summary>
        public string DeviceId { get; set; } = "";
    }

    /// <summary>
    /// Login response to client.
    /// </summary>
    public class LoginResponse
    {
        /// <summary>JWT access token.</summary>
        public string Token { get; set; } = "";

        /// <summary>Player identifier (from JWT sub claim).</summary>
        public string PlayerId { get; set; } = "";

        /// <summary>Whether this is a newly created player.</summary>
        public bool IsNewPlayer { get; set; }

        /// <summary>Token expiration time (UTC).</summary>
        public DateTime ExpiresAt { get; set; }
    }
}
