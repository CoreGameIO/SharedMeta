using System;
using System.IO;
using SharedMeta.Core;
using SharedMeta.Core.Logging;

namespace SharedMeta.Client
{
    /// <summary>
    /// File-based config cache for <see cref="DownloadingConfigProvider{TConfig}"/>.
    /// Stores serialized config bytes on disk with version in the filename
    /// (<c>{TConfigFullName}.v{Major}.{Minor}.bin</c>); on <see cref="Put"/> the previous
    /// version files for this config type are deleted so the cache directory doesn't grow
    /// unbounded across releases.
    /// </summary>
    public sealed class FileConfigCache<TConfig> : IClientMetaConfigCache<TConfig>, IClearableConfigCache where TConfig : class
    {
        private readonly string _cacheDir;
        private readonly IMetaSerializer _serializer;
        private readonly string _safeName;

        public FileConfigCache(string cacheDir, IMetaSerializer serializer)
        {
            _cacheDir = cacheDir ?? throw new ArgumentNullException(nameof(cacheDir));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _safeName = (typeof(TConfig).FullName ?? typeof(TConfig).Name).Replace('.', '_');
            Directory.CreateDirectory(cacheDir);
        }

        public TConfig? TryGet(MetaConfigVersion version)
        {
            var path = GetFilePath(version);
            if (!File.Exists(path))
            {
                MetaLog.Debug($"[FileConfigCache<{typeof(TConfig).Name}>] MISS v{version.Major}.{version.Minor}");
                return null;
            }
            try
            {
                var bytes = File.ReadAllBytes(path);
                var config = _serializer.Unpack<TConfig>(bytes);
                MetaLog.Debug($"[FileConfigCache<{typeof(TConfig).Name}>] HIT v{version.Major}.{version.Minor} ({bytes.Length} bytes)");
                return config;
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[FileConfigCache<{typeof(TConfig).Name}>] Read error: {ex.Message}");
                return null;
            }
        }

        public void Put(MetaConfigVersion version, TConfig config)
        {
            try
            {
                var path = GetFilePath(version);
                var bytes = _serializer.Pack(config).ToArray();
                File.WriteAllBytes(path, bytes);
                MetaLog.Debug($"[FileConfigCache<{typeof(TConfig).Name}>] Stored v{version.Major}.{version.Minor} ({bytes.Length} bytes)");
                CleanOldVersions(path);
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[FileConfigCache<{typeof(TConfig).Name}>] Write error: {ex.Message}");
            }
        }

        /// <summary>
        /// Debug/dev command: delete every cached file for this config type so the next
        /// subscribe re-downloads. Used by <c>MetaClient.ClearConfigCaches()</c> — handy after
        /// re-publishing a config under the same version during development.
        /// </summary>
        public void Clear()
        {
            try
            {
                int removed = 0;
                foreach (var file in Directory.GetFiles(_cacheDir, $"{_safeName}.v*.bin"))
                {
                    File.Delete(file);
                    removed++;
                }
                MetaLog.Debug($"[FileConfigCache<{typeof(TConfig).Name}>] Cleared {removed} cached file(s)");
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[FileConfigCache<{typeof(TConfig).Name}>] Clear error: {ex.Message}");
            }
        }

        private string GetFilePath(MetaConfigVersion version) =>
            Path.Combine(_cacheDir, $"{_safeName}.v{version.Major}.{version.Minor}.bin");

        private void CleanOldVersions(string currentPath)
        {
            try
            {
                foreach (var file in Directory.GetFiles(_cacheDir, $"{_safeName}.v*.bin"))
                {
                    if (file != currentPath)
                    {
                        File.Delete(file);
                        MetaLog.Debug($"[FileConfigCache<{typeof(TConfig).Name}>] Removed old {Path.GetFileName(file)}");
                    }
                }
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[FileConfigCache<{typeof(TConfig).Name}>] Cleanup error: {ex.Message}");
            }
        }
    }
}
