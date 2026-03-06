using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Resolves config download URLs by state type name and version.
    /// Registered in DI and used by transport endpoints (MetaHub, HTTP polling, etc.)
    /// to serve config URLs without going through the grain chain.
    /// Generated code implements this by delegating to IMetaConfigProvider instances.
    /// </summary>
    public interface IConfigDownloadUrlResolver
    {
        /// <summary>
        /// Get the download URL for a config.
        /// </summary>
        /// <param name="stateTypeName">The state type name (short or full).</param>
        /// <param name="version">The config version requested by the client.</param>
        /// <returns>The download URL, or null if not available.</returns>
        string? GetDownloadUrl(string stateTypeName, MetaConfigVersion version);
    }
}
