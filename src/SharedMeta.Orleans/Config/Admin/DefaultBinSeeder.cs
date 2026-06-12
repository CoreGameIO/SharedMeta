using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedMeta.Core;
using SharedMeta.Server.Core;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ "Get me started" helper. For every <c>[MetaConfig]</c> type the generator
    /// discovered (via <see cref="IConfigByteSource.Configs"/>), writes
    /// <c>{root}/{Type.Name}/{version}.bin</c> using the serializer-packed default
    /// instance — but only if the file doesn't exist yet. Cleanest hookup point is
    /// <see cref="ConfigsOptions.OnBeforeSeed"/>:
    /// <code>
    /// o.OnBeforeSeed = (sp, _) =&gt; {
    ///     DefaultBinSeeder.WriteMissingDefaults(sp, "data/drafts", "0.1.0");
    ///     return Task.CompletedTask;
    /// };
    /// </code>
    ///
    /// <para>Designed for first-run developer ergonomics: fresh checkout → <c>dotnet run</c> →
    /// every config publishes its default values; editing a config DTO or supplying a hand-baked
    /// .bin under the same path takes over from there. Production stands ship the .bin files in
    /// the image and never hit the missing-file branch.</para>
    ///
    /// <para>Requirements:</para>
    /// <list type="bullet">
    /// <item>The config type must expose a public parameterless constructor (<c>Activator.CreateInstance</c>).
    ///       Types without one are skipped with a warning.</item>
    /// <item><see cref="IMetaSerializer"/> must be registered (always true under
    ///       <c>ConfigureMeta</c>).</item>
    /// </list>
    /// </summary>
    public static class DefaultBinSeeder
    {
        /// <summary>
        /// Walk every <see cref="IConfigByteSource.Configs"/> entry; for each, write a default-instance
        /// bin to <c>{root}/{Type.Name}/{version}.bin</c> when that file doesn't already exist.
        /// </summary>
        /// <param name="services">Service provider — must resolve <see cref="IConfigByteSource"/> and <see cref="IMetaSerializer"/>.</param>
        /// <param name="root">Seed root (typically the same path passed to <c>UseDirectorySeed</c>).</param>
        /// <param name="version">Canonical <c>Major.Minor.Patch</c> string to file the default under.</param>
        public static void WriteMissingDefaults(IServiceProvider services, string root, string version)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root must be non-empty.", nameof(root));
            if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("version must be non-empty.", nameof(version));

            var byteSource = services.GetService<IConfigByteSource>();
            if (byteSource is null) return;
            var serializer = services.GetRequiredService<IMetaSerializer>();
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("DefaultBinSeeder");

            foreach (var entry in byteSource.Configs)
            {
                var destFolder = Path.Combine(root, entry.ConfigType.Name);
                var destPath = Path.Combine(destFolder, $"{version}.bin");
                if (File.Exists(destPath)) continue;

                object instance;
                try
                {
                    instance = Activator.CreateInstance(entry.ConfigType)
                        ?? throw new InvalidOperationException($"Activator returned null for {entry.ConfigType.FullName}.");
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "DefaultBinSeeder: skip {Type} — needs a public parameterless ctor to seed a default bin",
                        entry.ConfigType.FullName);
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = PackViaReflection(serializer, entry.ConfigType, instance);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "DefaultBinSeeder: skip {Type} — serializer failed to pack default instance",
                        entry.ConfigType.FullName);
                    continue;
                }

                Directory.CreateDirectory(destFolder);
                File.WriteAllBytes(destPath, bytes);
                logger?.LogInformation(
                    "DefaultBinSeeder: {Type} v{Version} ({Size} B) → {Path}",
                    entry.ConfigType.Name, version, bytes.Length, destPath);
            }
        }

        /// <summary>
        /// Reflective dispatch over <c>IMetaSerializer.PackForExternalUsage&lt;T&gt;(T)</c> — the
        /// caller has only an open <see cref="Type"/>, so we close the generic at runtime.
        /// </summary>
        private static byte[] PackViaReflection(IMetaSerializer serializer, Type configType, object instance)
        {
            var method = typeof(IMetaSerializer)
                .GetMethod(nameof(IMetaSerializer.PackForExternalUsage))!
                .MakeGenericMethod(configType);
            return (byte[])method.Invoke(serializer, new[] { instance })!;
        }
    }
}
