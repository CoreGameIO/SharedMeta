using System;
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
    /// Project still owns delivery — how bin files get there (dev YAML compiler, CI
    /// pipeline, manual copy, embedded extraction) — but the framework handles
    /// scanning + version selection. Subdirectory name defaults to
    /// <see cref="Type.Name"/>; the loader falls back to <see cref="Type.FullName"/>
    /// when the short-name folder is absent.
    /// </para>
    /// </summary>
    public sealed class DirectoryConfigBootstrapper : IConfigBootstrapper
    {
        private readonly string _root;
        private readonly ILogger<DirectoryConfigBootstrapper> _logger;

        public DirectoryConfigBootstrapper(string root, ILogger<DirectoryConfigBootstrapper>? logger = null)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("Seed directory root is required.", nameof(root));
            _root = root;
            _logger = logger ?? NullLogger<DirectoryConfigBootstrapper>.Instance;
        }

        public Task<ConfigBootstrapSeed?> LoadAsync(Type configType, CancellationToken cancellationToken)
        {
            var folder = ResolveFolder(configType);
            if (folder == null)
            {
                _logger.LogDebug(
                    "DirectoryConfigBootstrapper: no seed folder for {Type} under {Root}",
                    configType.Name, _root);
                return Task.FromResult<ConfigBootstrapSeed?>(null);
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
                return Task.FromResult<ConfigBootstrapSeed?>(null);
            }

            var bytes = File.ReadAllBytes(latest.path);
            var seed = new ConfigBootstrapSeed
            {
                Version = latest.version,
                Bytes = bytes,
                Origin = "directory",
                PublishedBy = "bootstrap",
                Notes = $"Seeded from {Path.GetFileName(folder)}/{Path.GetFileName(latest.path)}",
            };

            _logger.LogInformation(
                "DirectoryConfigBootstrapper: {Type} v{Version} ({Size} B) ← {Path}",
                configType.Name, latest.version, bytes.Length, latest.path);

            return Task.FromResult<ConfigBootstrapSeed?>(seed);
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
