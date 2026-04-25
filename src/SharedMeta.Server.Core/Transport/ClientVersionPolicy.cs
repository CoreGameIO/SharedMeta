using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core.Transport
{
    /// <summary>
    /// Per-silo cache and validator for the cluster-wide client-version gate.
    /// Registered as a singleton by AddMetaServices() / ConfigureMeta(). Holds the server's own
    /// version plus the effective minimum client version, refreshed from <see cref="IVersionPolicyGrain"/>
    /// every <see cref="CacheTtl"/> seconds so admin changes propagate without a silo restart.
    ///
    /// Call <see cref="ValidateAsync"/> from the connect path — it transparently refreshes the
    /// cache and returns a result object containing both the verdict and the data needed for
    /// the connect response (ServerVersion, MinClientVersion, optional Error).
    ///
    /// Precedence (highest → lowest):
    ///   1. Grain override — set via <see cref="IVersionPolicyGrain.SetMinClientVersionAsync"/>.
    ///   2. Static config — <see cref="MetaTransportOptions.MinClientVersion"/> (set at startup).
    ///
    /// Setting the grain value to null clears the override and falls back to static config.
    /// </summary>
    public class ClientVersionPolicy
    {
        /// <summary>How long a grain-fetched value is considered fresh. Default: 60 seconds.</summary>
        public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        private readonly IGrainFactory? _grainFactory;
        private readonly string? _staticMinClientVersion;
        private volatile string? _grainMinClientVersion;
        private long _cacheExpiryTicks;

        /// <summary>
        /// The server's current version (e.g. "1.3.0").
        /// Set once at startup — cannot change without a server restart.
        /// Sent to clients in every <c>SessionConnectResponse</c>.
        /// </summary>
        public string? ServerVersion { get; }

        /// <summary>
        /// The currently effective minimum client version, after applying the grain override.
        /// Returns the grain value when set, otherwise the static config value.
        /// </summary>
        public string? MinClientVersion => _grainMinClientVersion ?? _staticMinClientVersion;

        public ClientVersionPolicy(
            string? serverVersion = null,
            string? minClientVersion = null,
            IGrainFactory? grainFactory = null)
        {
            ServerVersion = serverVersion;
            _staticMinClientVersion = minClientVersion;
            _grainFactory = grainFactory;
            // Start expired so the first ValidateAsync always fetches from the grain.
            Interlocked.Exchange(ref _cacheExpiryTicks, 0L);
        }

        /// <summary>
        /// Refreshes the grain cache when stale and validates the supplied client version against
        /// the effective server/min versions. Returns a result struct containing both the verdict
        /// and the version metadata that the caller needs to put in the connect response.
        /// </summary>
        public async Task<ClientVersionValidationResult> ValidateAsync(string? clientVersion)
        {
            await RefreshIfStaleAsync();

            var serverVersion = ServerVersion;
            var minClientVersion = MinClientVersion;
            var error = Validate(clientVersion, serverVersion, minClientVersion);

            return new ClientVersionValidationResult(serverVersion, minClientVersion, error);
        }

        private async Task RefreshIfStaleAsync()
        {
            if (_grainFactory == null) return;
            if (DateTime.UtcNow.Ticks <= Interlocked.Read(ref _cacheExpiryTicks)) return;

            try
            {
                var grain = _grainFactory.GetGrain<IVersionPolicyGrain>("global");
                _grainMinClientVersion = await grain.GetMinClientVersionAsync();
            }
            catch
            {
                // Grain unavailable (storage not configured, silo starting up, etc.).
                // Keep existing cached value; the finally block bumps expiry so we retry
                // in CacheTtl instead of hammering an unavailable grain.
            }
            finally
            {
                Interlocked.Exchange(ref _cacheExpiryTicks, (DateTime.UtcNow + CacheTtl).Ticks);
            }
        }

        private static string? Validate(string? clientVersion, string? serverVersion, string? minClientVersion)
        {
            if (minClientVersion == null || serverVersion == null) return null;
            if (clientVersion == null) return null; // old client without version — allow through

            if (!TryParseVersion(clientVersion, out int cMaj, out int cMin, out int cPatch))
                return $"Invalid client version format: '{clientVersion}'. Expected major.minor.patch.";

            if (!TryParseVersion(serverVersion, out int sMaj, out _, out _))
                return null; // misconfigured server — don't block clients

            if (!TryParseVersion(minClientVersion, out _, out int minMin, out int minPatch))
                return null; // misconfigured server — don't block clients

            if (cMaj != sMaj)
                return $"Client version {clientVersion} has incompatible major version (server requires major {sMaj}). Please upgrade your client.";

            if (cMin < minMin || (cMin == minMin && cPatch < minPatch))
                return $"Client version {clientVersion} is outdated. Minimum required: {minClientVersion}. Please upgrade your client.";

            return null;
        }

        private static bool TryParseVersion(string v, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            var parts = v.Split('.');
            if (parts.Length < 3) return false;
            return int.TryParse(parts[0], out major)
                && int.TryParse(parts[1], out minor)
                && int.TryParse(parts[2], out patch);
        }
    }

    /// <summary>
    /// Outcome of <see cref="ClientVersionPolicy.ValidateAsync"/>. <see cref="Error"/> is null when
    /// the client version is acceptable; otherwise it carries a human-readable rejection reason.
    /// <see cref="ServerVersion"/> and <see cref="MinClientVersion"/> are always populated so the
    /// caller can echo them back to the client (so a rejected client knows which version to install).
    /// </summary>
    public readonly struct ClientVersionValidationResult
    {
        public string? ServerVersion { get; }
        public string? MinClientVersion { get; }
        public string? Error { get; }
        public bool IsAllowed => Error == null;

        public ClientVersionValidationResult(string? serverVersion, string? minClientVersion, string? error)
        {
            ServerVersion = serverVersion;
            MinClientVersion = minClientVersion;
            Error = error;
        }
    }
}
