using System;
using System.IO;
using System.Linq;
using SharedMeta.Core;
using SharedMeta.Core.Logging;

namespace SharedMeta.Client
{
    /// <summary>
    /// File-based config cache. Stores serialized config bytes on disk
    /// with version in the filename: {TypeName}.v{Major}.{Minor}.bin.
    /// Old versions are automatically cleaned up when a new version is stored.
    /// </summary>
    public class FileConfigCache : IMetaConfigCache
    {
        private readonly string _cacheDir;
        private readonly IMetaSerializer _serializer;

        public FileConfigCache(string cacheDir, IMetaSerializer serializer)
        {
            _cacheDir = cacheDir ?? throw new ArgumentNullException(nameof(cacheDir));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            Directory.CreateDirectory(cacheDir);
        }

        public object? TryGet(string configTypeName, MetaConfigVersion version)
        {
            var path = GetFilePath(configTypeName, version);
            if (!File.Exists(path))
            {
                MetaLog.Debug($"[FileConfigCache] MISS: {configTypeName} v{version.Major}.{version.Minor}");
                return null;
            }

            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(configTypeName))
                    .FirstOrDefault(t => t != null);
                if (type == null)
                {
                    MetaLog.Warning($"[FileConfigCache] Type not found: {configTypeName}");
                    return null;
                }

                var bytes = File.ReadAllBytes(path);
                var config = _serializer.Unpack(type, bytes);
                MetaLog.Debug($"[FileConfigCache] HIT: {configTypeName} v{version.Major}.{version.Minor} ({bytes.Length} bytes)");
                return config;
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[FileConfigCache] Read error for {configTypeName}: {ex.Message}");
                return null;
            }
        }

        public void Put(string configTypeName, MetaConfigVersion version, object config)
        {
            try
            {
                var path = GetFilePath(configTypeName, version);
                var bytes = _serializer.Pack(config);
                File.WriteAllBytes(path, bytes);
                MetaLog.Debug($"[FileConfigCache] Stored: {configTypeName} v{version.Major}.{version.Minor} ({bytes.Length} bytes)");

                CleanOldVersions(configTypeName, path);
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[FileConfigCache] Write error for {configTypeName}: {ex.Message}");
            }
        }

        private string GetFilePath(string configTypeName, MetaConfigVersion version)
        {
            var safeName = configTypeName.Replace('.', '_');
            return Path.Combine(_cacheDir, $"{safeName}.v{version.Major}.{version.Minor}.bin");
        }

        private void CleanOldVersions(string configTypeName, string currentPath)
        {
            var safeName = configTypeName.Replace('.', '_');
            try
            {
                foreach (var file in Directory.GetFiles(_cacheDir, $"{safeName}.v*.bin"))
                {
                    if (file != currentPath)
                    {
                        File.Delete(file);
                        MetaLog.Debug($"[FileConfigCache] Removed old: {Path.GetFileName(file)}");
                    }
                }
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[FileConfigCache] Cleanup error: {ex.Message}");
            }
        }
    }
}
