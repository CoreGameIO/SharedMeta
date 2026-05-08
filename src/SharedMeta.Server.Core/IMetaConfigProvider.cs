using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Server-side provider for static game configuration.
    /// Implement this interface and register in DI to provide config to entities.
    /// Config is available via Context.Config in service methods.
    /// </summary>
    /// <typeparam name="TConfig">The config type (marked with [MetaConfig]).</typeparam>
    public interface IMetaConfigProvider<TConfig> where TConfig : class
    {
        /// <summary>
        /// Default config version for new sessions (Major.Minor).
        /// Major = schema version (must match client code).
        /// Minor = data version (changes when config values change).
        /// </summary>
        MetaConfigVersion CurrentVersion { get; }

        /// <summary>
        /// Get config for a specific version.
        /// Called during entity activation and on subscribe.
        /// </summary>
        /// <param name="version">The config version.</param>
        /// <returns>The config instance.</returns>
        TConfig GetConfig(MetaConfigVersion version);

        /// <summary>
        /// Resolve the latest available config version within a specific Major.Minor branch.
        /// Called when a client connects and the matching <see cref="MetaConfigVersionAttribute"/>
        /// rule specifies <c>*</c> for the Patch component — meaning "latest patch in this branch".
        ///
        /// Example: pattern "1.x.*" → caller resolves to the newest 1.3.N via
        /// <c>ResolveLatestMatching(1, 3)</c>.
        ///
        /// The default implementation returns <see cref="CurrentVersion"/> unchanged (no
        /// patch-branch tracking). Override in providers that manage multiple patch versions.
        /// </summary>
        MetaConfigVersion ResolveLatestMatching(int major, int minor)
            => CurrentVersion;

        /// <summary>
        /// Resolve the config version for a specific connecting client's app version using
        /// the <see cref="MetaConfigVersionAttribute"/> rules on the config class.
        /// Returns <see cref="CurrentVersion"/> when no rule matches or when no
        /// <see cref="MetaConfigVersionResolver"/> was supplied.
        /// </summary>
        MetaConfigVersion ResolveForClient(string? clientAppVersion, MetaConfigVersionResolver? resolver)
        {
            if (resolver == null || string.IsNullOrEmpty(clientAppVersion))
                return CurrentVersion;

            var request = resolver.Resolve(clientAppVersion);
            if (request == null) return CurrentVersion;

            return request.PatchIsLatest
                ? ResolveLatestMatching(request.Major, request.Minor)
                : new MetaConfigVersion(request.Major, request.Minor, request.PatchMin);
        }

        /// <summary>
        /// Get the download URL for a specific config version.
        /// Called by the transport layer when client requests a config download.
        /// Return null if client is expected to have config bundled.
        /// </summary>
        /// <param name="version">The config version requested by the client.</param>
        string? GetDownloadUrl(MetaConfigVersion version) => null;
    }
}
