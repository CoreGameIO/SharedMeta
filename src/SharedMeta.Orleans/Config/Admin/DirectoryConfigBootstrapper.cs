using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core;
using SharedMeta.Server.Core.Config.Admin;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Built-in <see cref="IConfigBootstrapper"/> that scans a folder layout
    /// <c>{root}/{Type.Name}/{Major.Minor.Patch}.bin</c> and returns the highest
    /// <see cref="MetaConfigVersion"/> for the requested type. Pair with
    /// <see cref="ConfigsOptions.UseDirectorySeed"/> for the typical
    /// "image-baked bin files in a known folder" deployment.
    /// <para>
    /// Project owns delivery (dev YAML compiler, CI pipeline, manual copy, embedded extraction);
    /// the framework handles scanning + version selection. Subdirectory name defaults to
    /// <see cref="Type.Name"/>; the loader falls back to <see cref="Type.FullName"/> when the
    /// short-name folder is absent.
    /// </para>
    /// <para>
    /// 0.27.1: read-only — never writes to the filesystem. The two-phase contract caches the
    /// resolved <c>(folder, latest path)</c> per type from <see cref="GetVersionAsync"/> so
    /// <see cref="GetBytesAsync"/> doesn't rescan.
    /// </para>
    /// </summary>
    public sealed class DirectoryConfigBootstrapper : IConfigBootstrapper
    {
        private readonly string _root;
        private readonly ILogger<DirectoryConfigBootstrapper> _logger;
        private readonly Dictionary<Type, (string Path, MetaConfigVersion Version, string Folder)> _resolved = new();

        public DirectoryConfigBootstrapper(string root, ILogger<DirectoryConfigBootstrapper>? logger = null)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("Seed directory root is required.", nameof(root));
            _root = root;
            _logger = logger ?? NullLogger<DirectoryConfigBootstrapper>.Instance;
        }

        public Task<MetaConfigVersion?> GetVersionAsync(Type configType, CancellationToken cancellationToken)
        {
            var folder = ResolveFolder(configType);
            if (folder == null)
            {
                _logger.LogDebug(
                    "DirectoryConfigBootstrapper: no seed folder for {Type} under {Root}",
                    configType.Name, _root);
                return Task.FromResult<MetaConfigVersion?>(null);
            }

            var latest = Directory.EnumerateFiles(folder, "*.bin")
                .Select(path => (path, version: MetaConfigVersion.Parse(Path.GetFileNameWithoutExtension(path))))
                .Where(x => x.version != default)
                .OrderByDescending(x => x.version)
                .FirstOrDefault();

            if (latest.path == null)
            {
                _logger.LogWarning(
                    "DirectoryConfigBootstrapper: folder {Folder} has no parseable {{M.m.p}}.bin entries",
                    folder);
                return Task.FromResult<MetaConfigVersion?>(null);
            }

            _resolved[configType] = (latest.path, latest.version, folder);
            return Task.FromResult<MetaConfigVersion?>(latest.version);
        }

        public Task<ConfigBootstrapBytes?> GetBytesAsync(Type configType, MetaConfigVersion version, CancellationToken cancellationToken)
        {
            // Cache hit from the GetVersionAsync phase covers the common path.
            // Fall back to a fresh scan when GetBytesAsync is called standalone (e.g. tests).
            if (!_resolved.TryGetValue(configType, out var entry) || entry.Version != version)
            {
                var folder = ResolveFolder(configType);
                if (folder == null) return Task.FromResult<ConfigBootstrapBytes?>(null);
                var path = Path.Combine(folder, $"{version}.bin");
                if (!File.Exists(path))
                {
                    _logger.LogDebug(
                        "DirectoryConfigBootstrapper: {Type} v{Version} not present at {Path}",
                        configType.Name, version, path);
                    return Task.FromResult<ConfigBootstrapBytes?>(null);
                }
                entry = (path, version, folder);
            }

            var bytes = File.ReadAllBytes(entry.Path);
            _logger.LogInformation(
                "DirectoryConfigBootstrapper: {Type} v{Version} ({Size} B) ← {Path}",
                configType.Name, version, bytes.Length, entry.Path);

            return Task.FromResult<ConfigBootstrapBytes?>(new ConfigBootstrapBytes
            {
                Bytes = bytes,
                Origin = "directory",
                PublishedBy = "bootstrap",
                Notes = $"Seeded from {Path.GetFileName(entry.Folder)}/{Path.GetFileName(entry.Path)}",
            });
        }

        private string? ResolveFolder(Type configType)
        {
            var shortFolder = Path.Combine(_root, configType.Name);
            if (Directory.Exists(shortFolder)) return shortFolder;
            if (configType.FullName != null)
            {
                var fullFolder = Path.Combine(_root, configType.FullName);
                if (Directory.Exists(fullFolder)) return fullFolder;
            }
            return null;
        }
    }
}
