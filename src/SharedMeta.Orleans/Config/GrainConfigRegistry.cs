using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Server.Core.Config;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// <see cref="IConfigRegistry"/> implementation backed by Orleans grains. Get/Set bytes
    /// route through one <see cref="IConfigStoreGrain"/> per (configType, version); the
    /// per-type <see cref="IConfigDirectoryGrain"/> tracks the version set and fans out
    /// observer notifications on publish/unpublish so subscribed
    /// <c>BroadcastingConfigProvider&lt;TConfig&gt;</c> instances can invalidate caches.
    ///
    /// <para>Stateless — safe to register as a DI singleton on every silo. All state lives
    /// in the grains themselves.</para>
    /// </summary>
    public class GrainConfigRegistry : IConfigRegistry
    {
        private readonly IGrainFactory _grains;

        public GrainConfigRegistry(IGrainFactory grains)
        {
            _grains = grains ?? throw new ArgumentNullException(nameof(grains));
        }

        public Task<byte[]?> GetAsync(Type configType, MetaConfigVersion version)
            => _grains.GetGrain<IConfigStoreGrain>(StoreKey(configType, version)).GetAsync();

        public async Task<IReadOnlyList<MetaConfigVersion>> ListVersionsAsync(Type configType)
            => await _grains.GetGrain<IConfigDirectoryGrain>(DirectoryKey(configType)).ListVersionsAsync();

        public async Task PublishAsync(Type configType, MetaConfigVersion version, byte[] configBytes)
        {
            // Write bytes first so observers refetching after the notification see the new
            // content. If the notification fan-out fails partially, the underlying bytes are
            // still in place; subscribers will see the change on their next cache miss.
            await _grains.GetGrain<IConfigStoreGrain>(StoreKey(configType, version)).SetAsync(configBytes);
            await _grains.GetGrain<IConfigDirectoryGrain>(DirectoryKey(configType)).RecordPublishAsync(version);
        }

        public async Task UnpublishAsync(Type configType, MetaConfigVersion version)
        {
            // Clear bytes first, then notify — same ordering rationale as Publish.
            await _grains.GetGrain<IConfigStoreGrain>(StoreKey(configType, version)).ClearAsync();
            await _grains.GetGrain<IConfigDirectoryGrain>(DirectoryKey(configType)).RecordUnpublishAsync(version);
        }

        private static string StoreKey(Type configType, MetaConfigVersion v)
            => $"{ConfigTypeName(configType)}::{v.Major}.{v.Minor}.{v.Patch}";

        private static string DirectoryKey(Type configType) => ConfigTypeName(configType);

        private static string ConfigTypeName(Type t)
            => t.FullName ?? throw new InvalidOperationException(
                $"Config type {t.Name} has no FullName — anonymous/generic types are not supported by the registry.");
    }
}
