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
            => registry.PublishAsync(typeof(TConfig), version, serializer.Pack(config));
    }
}
