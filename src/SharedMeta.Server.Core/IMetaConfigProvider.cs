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
        /// Get the download URL for a specific config version.
        /// Called by the transport layer when client requests a config download.
        /// Return null if client is expected to have config bundled.
        /// </summary>
        /// <param name="version">The config version requested by the client.</param>
        string? GetDownloadUrl(MetaConfigVersion version) => null;
    }
}
