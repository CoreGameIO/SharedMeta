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

        /// <summary>Token lifetime. Default: 7 days.</summary>
        public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(7);
    }
}
