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
        private readonly string? _staticMaxClientVersion;
        private volatile string? _grainMinClientVersion;
        private volatile string? _grainMaxClientVersion;
        private long _cacheExpiryTicks;

        /// <summary>
        /// The server's current version (e.g. "1.3.0").
        /// Set once at startup — cannot change without a server restart.
        /// Sent to clients in every <c>SessionConnectResponse</c>.
        /// </summary>
        public string? ServerVersion { get; }

        /// <summary>The currently effective minimum client version.</summary>
        public string? MinClientVersion => _grainMinClientVersion ?? _staticMinClientVersion;

        /// <summary>
        /// The currently effective maximum client version. Clients with a version strictly above
        /// this (by major or minor) are rejected with "client too new for this server."
        /// Null = no upper bound.
        /// </summary>
        public string? MaxClientVersion => _grainMaxClientVersion ?? _staticMaxClientVersion;

        public ClientVersionPolicy(
            string? serverVersion = null,
            string? minClientVersion = null,
            string? maxClientVersion = null,
            IGrainFactory? grainFactory = null)
        {
            ServerVersion = serverVersion;
            _staticMinClientVersion = minClientVersion;
            _staticMaxClientVersion = maxClientVersion;
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

            var serverVersion    = ServerVersion;
            var minClientVersion = MinClientVersion;
            var maxClientVersion = MaxClientVersion;
            var error = Validate(clientVersion, serverVersion, minClientVersion, maxClientVersion);

            return new ClientVersionValidationResult(serverVersion, minClientVersion, maxClientVersion, error);
        }

        private async Task RefreshIfStaleAsync()
        {
            if (_grainFactory == null) return;
            if (DateTime.UtcNow.Ticks <= Interlocked.Read(ref _cacheExpiryTicks)) return;

            try
            {
                var grain = _grainFactory.GetGrain<IVersionPolicyGrain>("global");
                _grainMinClientVersion = await grain.GetMinClientVersionAsync();
                _grainMaxClientVersion = await grain.GetMaxClientVersionAsync();
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

        private static string? Validate(
            string? clientVersion,
            string? serverVersion,
            string? minClientVersion,
            string? maxClientVersion)
        {
            if (clientVersion == null) return null; // old client without version — allow through

            if (!TryParseVersion(clientVersion, out int cMaj, out int cMin, out int cPatch))
                return $"Invalid client version format: '{clientVersion}'. Expected major.minor.patch.";

            // Lower bound check — strictly compare against MinClientVersion (not serverVersion).
            // The accepted range is [MinClientVersion, MaxClientVersion] inclusive; the server's
            // own version doesn't constrain the lower bound. A server on 2.0 with Min=1.1 / Max=2.x
            // legitimately accepts clients with majors 1 AND 2.
            if (minClientVersion != null)
            {
                if (TryParseVersion(minClientVersion, out int minMaj, out int minMin, out int minPatch))
                {
                    bool tooOld =
                        cMaj < minMaj ||
                        (cMaj == minMaj && cMin < minMin) ||
                        (cMaj == minMaj && cMin == minMin && cPatch < minPatch);

                    if (tooOld)
                        return $"Client version {clientVersion} is outdated. " +
                               $"Minimum required: {minClientVersion}. Please upgrade your client.";
                }
                // If MinClientVersion is unparseable — misconfigured server, don't block clients.
            }

            // Upper bound check — reject clients that are too new for this server.
            // Pattern supports "*" for patch component (e.g. "2.3.*" = max minor 3 of major 2).
            if (maxClientVersion != null)
            {
                var maxParts = maxClientVersion.Split('.');
                if (maxParts.Length >= 2
                    && int.TryParse(maxParts[0], out int maxMaj)
                    && int.TryParse(maxParts[1], out int maxMin))
                {
                    bool patchWildcard = maxParts.Length < 3 || maxParts[2] == "*";

                    if (cMaj > maxMaj)
                        return $"Client version {clientVersion} is too new for this server " +
                               $"(maximum supported: {maxClientVersion}). Please downgrade or wait for a server update.";

                    if (cMaj == maxMaj && !patchWildcard)
                    {
                        if (!int.TryParse(maxParts[2], out int maxPatch)) goto skipMax;
                        if (cMin > maxMin || (cMin == maxMin && cPatch > maxPatch))
                            return $"Client version {clientVersion} is too new for this server " +
                                   $"(maximum supported: {maxClientVersion}). Please downgrade or wait for a server update.";
                    }
                    else if (cMaj == maxMaj && patchWildcard && cMin > maxMin)
                    {
                        return $"Client version {clientVersion} is too new for this server " +
                               $"(maximum supported: {maxClientVersion}). Please downgrade or wait for a server update.";
                    }
                }
            }

            skipMax:
            return null;
        }

        private static bool TryParseVersion(string v, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            var parts = v.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor)) return false;
            patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
            return true;
        }
    }

    /// <summary>
    /// Outcome of <see cref="ClientVersionPolicy.ValidateAsync"/>. <see cref="Error"/> is null when
    /// the client version is acceptable; otherwise it carries a human-readable rejection reason.
    /// Version metadata is always populated so the caller can echo it back to the client.
    /// </summary>
    public readonly struct ClientVersionValidationResult
    {
        public string? ServerVersion { get; }
        public string? MinClientVersion { get; }
        public string? MaxClientVersion { get; }
        public string? Error { get; }
        public bool IsAllowed => Error == null;

        public ClientVersionValidationResult(
            string? serverVersion,
            string? minClientVersion,
            string? maxClientVersion,
            string? error)
        {
            ServerVersion    = serverVersion;
            MinClientVersion = minClientVersion;
            MaxClientVersion = maxClientVersion;
            Error            = error;
        }
    }
}
