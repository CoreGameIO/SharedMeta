using System.Threading.Tasks;

namespace SharedMeta.Core
{
    /// <summary>
    /// Client-side cache for versioned MetaConfig data.
    /// Implementations can store configs in memory, on disk, or in PlayerPrefs.
    /// </summary>
    public interface IMetaConfigCache
    {
        /// <summary>
        /// Try to get a cached config by type name and version.
        /// Returns null if not cached or version doesn't match.
        /// </summary>
        object? TryGet(string configTypeName, MetaConfigVersion version);

        /// <summary>
        /// Store a config in the cache.
        /// </summary>
        void Put(string configTypeName, MetaConfigVersion version, object config);
    }

    /// <summary>
    /// Client-side downloader for MetaConfig data.
    /// Downloads config from a URL provided by the server.
    /// </summary>
    public interface IMetaConfigDownloader
    {
        /// <summary>
        /// Download config bytes from the given URL.
        /// </summary>
        Task<byte[]> DownloadAsync(string url);
    }
}
