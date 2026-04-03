using System.Threading.Tasks;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Result of external platform token validation.
    /// </summary>
    public class ExternalAuthResult
    {
        /// <summary>Stable platform-specific user ID (Google sub, Apple sub, SteamID64).</summary>
        public string PlatformUserId { get; set; } = "";

        /// <summary>Optional display name from the platform.</summary>
        public string? DisplayName { get; set; }
    }

    /// <summary>
    /// Validates platform tokens and returns a stable platform-specific user ID.
    /// Implement per platform (Google, Apple, Steam) and register in DI.
    /// </summary>
    public interface IExternalAuthValidator
    {
        /// <summary>Platform identifier: "google", "apple", "steam", etc.</summary>
        string Platform { get; }

        /// <summary>
        /// Validate the platform token/ticket.
        /// Returns platform-specific user ID.
        /// Throws <see cref="ExternalAuthException"/> if validation fails.
        /// </summary>
        Task<ExternalAuthResult> ValidateAsync(string platformToken);
    }

    /// <summary>
    /// Thrown when platform token validation fails.
    /// </summary>
    public class ExternalAuthException : System.Exception
    {
        public ExternalAuthException(string message) : base(message) { }
        public ExternalAuthException(string message, System.Exception inner) : base(message, inner) { }
    }
}
