using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace SharedMeta.Core.Auth
{
    /// <summary>
    /// Pluggable auth provider used by <c>MetaAuth</c>.
    /// Implement this to replace the default HTTP-based auth flow — for example
    /// with a Unity-specific transport (UnityWebRequest), a local in-process
    /// backend, or a third-party auth service (Firebase, PlayFab, etc.).
    /// Set the active provider via <c>MetaAuth.Provider</c> at startup.
    /// </summary>
    public interface IMetaAuthProvider
    {
        /// <summary>Login with a device identifier.</summary>
        /// <param name="authUrl">Auth endpoint URL (may be ignored by non-HTTP providers).</param>
        /// <param name="deviceId">Stable device identifier.</param>
        Task<MetaLoginResult> LoginAsync(
            string authUrl, string deviceId, CancellationToken cancellation);

        /// <summary>Login via a platform token (Google, Apple, Steam, etc.).</summary>
        Task<MetaLoginResult> LoginWithPlatformAsync(
            string authUrl, string platform, string platformToken, CancellationToken cancellation);

        /// <summary>
        /// Exchange a refresh token for a new access token (and a rotated refresh token). Throws if the
        /// refresh token is invalid/expired/revoked — the caller falls back to a full login.
        /// </summary>
        Task<MetaLoginResult> RefreshAsync(
            string authUrl, string refreshToken, CancellationToken cancellation);

        /// <summary>Link a platform account to the currently authenticated player.</summary>
        Task<bool> LinkAsync(
            string authUrl, string platform, string platformToken, string accessToken, CancellationToken cancellation);

        /// <summary>Unlink an auth key from the currently authenticated player.</summary>
        Task<bool> UnlinkAsync(
            string authUrl, string authKey, string accessToken, CancellationToken cancellation);

        /// <summary>
        /// Reset (force-unlink) a device from the current player.
        /// After reset, the next login with this deviceId will create a new player.
        /// </summary>
        Task<bool> ResetDeviceAsync(
            string authUrl, string deviceId, string accessToken, CancellationToken cancellation);
    }
}
