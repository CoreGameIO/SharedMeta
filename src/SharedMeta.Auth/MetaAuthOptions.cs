using System;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Configuration options for SharedMeta JWT authentication.
    /// </summary>
    public class MetaAuthOptions
    {
        /// <summary>Secret key for signing JWT tokens. Must be at least 32 characters.</summary>
        public string SecretKey { get; set; } = "";

        /// <summary>JWT issuer. Default: "SharedMeta".</summary>
        public string Issuer { get; set; } = "SharedMeta";

        /// <summary>JWT audience. Default: "SharedMeta".</summary>
        public string Audience { get; set; } = "SharedMeta";

        /// <summary>
        /// Access (JWT) token lifetime. Kept short because it is paired with a long-lived refresh
        /// token (see <see cref="RefreshTokenLifetime"/>). Default: 30 minutes.
        /// </summary>
        public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Refresh token lifetime — the absolute expiry of a rotation family. Rotation does not extend
        /// it, so a session cannot outlive this window even with continued refreshing. Default: 30 days.
        /// </summary>
        public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

        /// <summary>
        /// Deprecated alias for <see cref="AccessTokenLifetime"/>. Forwards to it for source
        /// compatibility. Note the default changed from 7 days to 30 minutes in 0.30.0 — set
        /// <see cref="AccessTokenLifetime"/>/<see cref="RefreshTokenLifetime"/> explicitly.
        /// </summary>
        [Obsolete("Renamed to AccessTokenLifetime (now paired with RefreshTokenLifetime). This alias forwards to AccessTokenLifetime.")]
        public TimeSpan TokenLifetime
        {
            get => AccessTokenLifetime;
            set => AccessTokenLifetime = value;
        }
    }
}
