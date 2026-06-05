using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Config
{
    /// <summary>
    /// Server-side admin API for the versioned config store. Holds bytes keyed by
    /// (configType, <see cref="MetaConfigVersion"/>). Read by
    /// <c>BroadcastingConfigProvider&lt;TConfig&gt;</c> on cache miss; written by admin code
    /// (tooling, migration scripts, in-game-editor) when a new version is published.
    ///
    /// <para>The interface itself is Orleans-free so user code (and unit tests with an
    /// in-memory stub) can take a dependency on it without pulling Orleans into a
    /// shared/test project. The grain-backed implementation lives in
    /// <c>SharedMeta.Orleans</c> as <c>GrainConfigRegistry</c> — one
    /// <c>IConfigStoreGrain</c> per (typeFullName, MetaConfigVersion) holds the bytes,
    /// and a per-type <c>IConfigDirectoryGrain</c> tracks the published version set plus
    /// pushes change notifications to subscribed providers.</para>
    ///
    /// <para>Byte-level contract: configs are stored as serialized blobs from
    /// <c>IMetaSerializer.Pack&lt;TConfig&gt;</c>; readers deserialize via the same serializer.
    /// This keeps the registry generic-agnostic and lets a single Orleans cluster host
    /// configs from any number of distinct config types without per-type grain code.</para>
    /// </summary>
    public interface IConfigRegistry
    {
        /// <summary>
        /// Read the serialized bytes for a specific (configType, version) pair.
        /// Returns null when no such version was ever published, when it was unpublished,
        /// or when the underlying grain state is empty.
        /// </summary>
        Task<byte[]?> GetAsync(Type configType, MetaConfigVersion version);

        /// <summary>
        /// List every published version of <paramref name="configType"/>, ascending by
        /// <see cref="MetaConfigVersion"/> comparison. Empty list when nothing has been
        /// published. Backed by the per-type directory grain; cheap to call on the hot path.
        /// </summary>
        Task<IReadOnlyList<MetaConfigVersion>> ListVersionsAsync(Type configType);

        /// <summary>
        /// Persist <paramref name="configBytes"/> as the canonical content for the given
        /// (configType, version) pair and notify all subscribed providers across the
        /// cluster. Republishing an existing version overwrites the bytes — the registered
        /// observers receive an "updated" notification and invalidate that version's cache
        /// entry. New version (not previously listed) is appended to the directory.
        /// </summary>
        Task PublishAsync(Type configType, MetaConfigVersion version, byte[] configBytes);

        /// <summary>
        /// Remove a published version. Cache entries on subscribed providers are invalidated;
        /// subsequent <see cref="GetAsync"/> calls return null. Use sparingly — entities
        /// pinned to the removed version will fail config resolution on reactivation.
        /// </summary>
        Task UnpublishAsync(Type configType, MetaConfigVersion version);
    }

    /// <summary>
    /// Typed convenience helpers — pack/unpack via the project's <see cref="IMetaSerializer"/>
    /// so call sites read naturally:
    /// <c>await registry.PublishAsync(version, myConfig, serializer);</c>
    /// </summary>
    public static class ConfigRegistryExtensions
    {
        /// <summary>
        /// Read and deserialize a typed config snapshot. Returns null when the version is
        /// unknown to the registry. Throws whatever the serializer throws on malformed bytes
        /// (typically a deserialization exception) — callers should treat that as data
        /// corruption, not a missing version.
        /// </summary>
        public static async Task<TConfig?> GetAsync<TConfig>(
            this IConfigRegistry registry, MetaConfigVersion version, IMetaSerializer serializer)
            where TConfig : class
        {
            var bytes = await registry.GetAsync(typeof(TConfig), version);
            return bytes is { Length: > 0 } ? serializer.Unpack<TConfig>(bytes) : null;
        }

        /// <summary>
        /// Serialize <paramref name="config"/> with the supplied <paramref name="serializer"/>
        /// and store under <typeparamref name="TConfig"/> + <paramref name="version"/>. Triggers
        /// the standard observer notification.
        /// </summary>
        public static Task PublishAsync<TConfig>(
            this IConfigRegistry registry, MetaConfigVersion version, TConfig config, IMetaSerializer serializer)
            where TConfig : class
            => registry.PublishAsync(typeof(TConfig), version, serializer.Pack(config).ToArray());

        /// <summary>
        /// 0.26.4+: Publish only if the bytes actually differ from what's already stored for
        /// the same (configType, version). Returns an outcome enum so the caller can
        /// distinguish "first-time publish", "no-op same content", and "overwrote drifted
        /// content". When <paramref name="failOnDrift"/> is true, throws
        /// <see cref="ConfigContentDriftException"/> on drift instead of overwriting — use this
        /// in production / CI to enforce "content changes require a version bump"; leave
        /// false in dev for the convenience of editing YAML and restarting without bumping.
        /// <para>
        /// Diagnostic helper for startup-time bootstrap (manifest → registry republish): the
        /// raw <see cref="IConfigRegistry.PublishAsync"/> always overwrites silently, which
        /// hides workflow errors like "edited YAML but forgot to bump the version".
        /// </para>
        /// </summary>
        public static async Task<ConfigPublishOutcome> PublishIfChangedAsync(
            this IConfigRegistry registry,
            Type configType,
            MetaConfigVersion version,
            byte[] configBytes,
            bool failOnDrift = false)
        {
            if (registry is null) throw new ArgumentNullException(nameof(registry));
            if (configType is null) throw new ArgumentNullException(nameof(configType));
            if (configBytes is null) throw new ArgumentNullException(nameof(configBytes));

            var existing = await registry.GetAsync(configType, version).ConfigureAwait(false);
            if (existing is null)
            {
                await registry.PublishAsync(configType, version, configBytes).ConfigureAwait(false);
                return ConfigPublishOutcome.PublishedNew;
            }

            if (existing.Length == configBytes.Length
                && new ReadOnlySpan<byte>(existing).SequenceEqual(configBytes))
            {
                return ConfigPublishOutcome.Unchanged;
            }

            if (failOnDrift)
            {
                throw new ConfigContentDriftException(configType, version, existing.Length, configBytes.Length);
            }

            await registry.PublishAsync(configType, version, configBytes).ConfigureAwait(false);
            return ConfigPublishOutcome.OverwrittenAfterDrift;
        }
    }

    /// <summary>
    /// 0.26.4+: Outcome of <see cref="ConfigRegistryExtensions.PublishIfChangedAsync"/>.
    /// </summary>
    public enum ConfigPublishOutcome
    {
        /// <summary>The (configType, version) pair was not in the registry — bytes stored as new.</summary>
        PublishedNew,
        /// <summary>The version existed with byte-identical content — no write was performed.</summary>
        Unchanged,
        /// <summary>The version existed with different content — bytes were overwritten (drift detected and accepted).</summary>
        OverwrittenAfterDrift,
    }

    /// <summary>
    /// 0.26.4+: Thrown by <see cref="ConfigRegistryExtensions.PublishIfChangedAsync"/> when
    /// <c>failOnDrift</c> is true and the supplied bytes differ from what's already
    /// published under the same (configType, version). Signals a workflow error — content
    /// was edited without bumping the version, which would silently change the published
    /// snapshot under an already-distributed version label.
    /// </summary>
    public sealed class ConfigContentDriftException : Exception
    {
        public Type ConfigType { get; }
        public MetaConfigVersion Version { get; }
        public int ExistingBytesLength { get; }
        public int NewBytesLength { get; }

        public ConfigContentDriftException(
            Type configType, MetaConfigVersion version, int existingBytesLength, int newBytesLength)
            : base($"Config content drift for {configType.FullName} v{version.Major}.{version.Minor}.{version.Patch}: " +
                   $"published is {existingBytesLength} bytes, new is {newBytesLength} bytes. " +
                   "Bump the config version (Major/Minor/Patch) instead of mutating the content for an already-published version.")
        {
            ConfigType = configType;
            Version = version;
            ExistingBytesLength = existingBytesLength;
            NewBytesLength = newBytesLength;
        }
    }
}
