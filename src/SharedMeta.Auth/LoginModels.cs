using System;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Device login request from client.
    /// </summary>
    public class LoginRequest
    {
        /// <summary>Unique device identifier.</summary>
        public string DeviceId { get; set; } = "";
    }

    /// <summary>
    /// Platform login request from client.
    /// </summary>
    public class PlatformLoginRequest
    {
        /// <summary>Platform identifier: "google", "apple", "steam".</summary>
        public string Platform { get; set; } = "";

        /// <summary>Platform token/ticket to validate.</summary>
        public string PlatformToken { get; set; } = "";
    }

    /// <summary>
    /// Link platform account to current player (requires authentication).
    /// </summary>
    public class LinkAccountRequest
    {
        /// <summary>Platform identifier: "google", "apple", "steam".</summary>
        public string Platform { get; set; } = "";

        /// <summary>Platform token/ticket to validate.</summary>
        public string PlatformToken { get; set; } = "";
    }

    /// <summary>
    /// Unlink an auth key from current player (requires authentication).
    /// </summary>
    public class UnlinkRequest
    {
        /// <summary>Auth key to unlink, e.g. "google:123456" or a device ID.</summary>
        public string AuthKey { get; set; } = "";
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

    /// <summary>
    /// Reset (force-unlink) a device from the current player.
    /// After reset, the next login with this deviceId will create a new player.
    /// </summary>
    public class ResetDeviceRequest
    {
        /// <summary>Device ID to unlink from the current player.</summary>
        public string DeviceId { get; set; } = "";
    }

    /// <summary>
    /// Response for link/unlink operations.
    /// </summary>
    public class AuthOperationResponse
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
    }
}
