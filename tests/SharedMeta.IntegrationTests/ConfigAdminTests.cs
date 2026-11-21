using System;
using System.Linq;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Server.Core.Config.Admin;
using Xunit;

namespace SharedMeta.UnitTests
{
    /// <summary>
    /// 0.27.0+ — verifies the new audit/admin contract shape: DTOs serialize correctly,
    /// MetaConfigVersion.GetBranchKey produces the canonical "M.m" form. Grain impls are
    /// covered by the integration tests once we wire a smoke harness; this file focuses
    /// on the contract pieces that pure unit tests can hit.
    /// </summary>
    public class ConfigAdminTests
    {
        [Fact]
        public void MetaConfigVersion_GetBranchKey_DropsPatch()
        {
            Assert.Equal("0.0", new MetaConfigVersion(0, 0, 0).GetBranchKey());
            Assert.Equal("1.4", new MetaConfigVersion(1, 4, 0).GetBranchKey());
            Assert.Equal("1.4", new MetaConfigVersion(1, 4, 12).GetBranchKey());
            Assert.Equal("2.0", new MetaConfigVersion(2, 0, 99).GetBranchKey());
        }

        [Fact]
        public void MetaConfigVersion_ToString_AlwaysIncludesPatch()
        {
            // Anchors the contract used by ConfigVersionInfo.Version: the admin grain
            // round-trips MetaConfigVersion.ToString() into the metadata key, so the
            // canonical "M.m.p" form must survive patch=0.
            Assert.Equal("0.1.0", new MetaConfigVersion(0, 1, 0).ToString());
            Assert.Equal("0.1.7", new MetaConfigVersion(0, 1, 7).ToString());
        }

        [Fact]
        public void ConfigVersionInfo_DefaultsAreEmpty_ButNotNull()
        {
            var info = new ConfigVersionInfo();
            Assert.Equal("", info.Version);
            Assert.Equal("", info.PublishedBy);
            Assert.Equal("", info.Origin);
            Assert.Null(info.Notes);
            Assert.Equal(0, info.SizeBytes);
        }

        [Fact]
        public void ConfigOverview_BranchesArrayIsEmptyByDefault()
        {
            var overview = new ConfigOverview();
            Assert.Empty(overview.Branches);
        }

        [Fact]
        public async Task IConfigBootstrapper_CanReturnNull()
        {
            // Contract: returning null from GetVersionAsync<TConfig> is the documented
            // "no seed available" signal — framework skips the type entirely.
            IConfigBootstrapper b = new NullBootstrapper();
            var version = await b.GetVersionAsync<object>(default);
            Assert.Null(version);
        }

        [Fact]
        public void ConfigBootstrapBytes_HasReasonableDefaults()
        {
            var bytes = new ConfigBootstrapBytes
            {
                Bytes = new byte[] { 1, 2, 3 },
            };
            Assert.Equal("bootstrap", bytes.Origin);
            Assert.Equal("bootstrap", bytes.PublishedBy);
            Assert.Null(bytes.Notes);
        }

        private static readonly DateTimeOffset SamplePublishedAt =
            new(2026, 3, 14, 15, 9, 26, TimeSpan.Zero);

        [Fact]
        public void ConfigVersionInfo_PublishedAtUtcTicks_RoundTrips()
        {
            var info = new ConfigVersionInfo { PublishedAtUtcTicks = SamplePublishedAt.UtcTicks };

            var copy = MemoryPack.MemoryPackSerializer.Deserialize<ConfigVersionInfo>(
                MemoryPack.MemoryPackSerializer.Serialize(info))!;
            Assert.Equal(SamplePublishedAt.UtcTicks, copy.PublishedAtUtcTicks);

            var mp = MessagePack.MessagePackSerializer.Deserialize<ConfigVersionInfo>(
                MessagePack.MessagePackSerializer.Serialize(info));
            Assert.Equal(SamplePublishedAt.UtcTicks, mp.PublishedAtUtcTicks);
        }

#pragma warning disable CS0618 // legacy PublishedAt is the subject under test
        [Fact]
        public void ConfigVersionInfo_MigratesLegacyMemoryPackRecord()
        {
            // Legacy shape: 6 members, no trailing ticks.
            var bytes = MemoryPack.MemoryPackSerializer.Serialize(
                new ConfigVersionInfo { Version = "1.2.3", Origin = "upload", PublishedAt = SamplePublishedAt });
            Assert.Equal(7, bytes[0]);
            var legacy = bytes[..^sizeof(long)];
            legacy[0] = 6;

            var info = MemoryPack.MemoryPackSerializer.Deserialize<ConfigVersionInfo>(legacy)!;
            Assert.Equal("1.2.3", info.Version);
            Assert.Equal("upload", info.Origin);
            Assert.Equal(0, info.PublishedAtUtcTicks);

            Assert.True(info.MigrateLegacyPublishedAt());
            Assert.Equal(SamplePublishedAt.UtcTicks, info.PublishedAtUtcTicks);
            Assert.Equal(default, info.PublishedAt);
            Assert.False(info.MigrateLegacyPublishedAt());
        }

        [Fact]
        public void ConfigVersionInfo_MigratesLegacyMessagePackRecord()
        {
            var bytes = MessagePack.MessagePackSerializer.Serialize(
                new ConfigVersionInfo { Version = "1.2.3", PublishedAt = SamplePublishedAt });
            // Legacy shape: fixarray of 6 instead of 7, without the trailing ticks (0 = one-byte fixint).
            Assert.Equal(0x97, bytes[0]);
            Assert.Equal(0x00, bytes[^1]);
            var legacy = bytes[..^1];
            legacy[0] = 0x96;

            var info = MessagePack.MessagePackSerializer.Deserialize<ConfigVersionInfo>(legacy);

            Assert.True(info.MigrateLegacyPublishedAt());
            Assert.Equal(SamplePublishedAt.UtcTicks, info.PublishedAtUtcTicks);
        }

        [Fact]
        public void ConfigVersionInfo_Migrate_KeepsExistingTicks()
        {
            var info = new ConfigVersionInfo { PublishedAtUtcTicks = 42, PublishedAt = SamplePublishedAt };
            Assert.False(info.MigrateLegacyPublishedAt());
            Assert.Equal(42, info.PublishedAtUtcTicks);

            Assert.False(new ConfigVersionInfo().MigrateLegacyPublishedAt());
        }
#pragma warning restore CS0618

        private sealed class NullBootstrapper : IConfigBootstrapper
        {
            public Task<MetaConfigVersion?> GetVersionAsync<TConfig>(System.Threading.CancellationToken ct) where TConfig : class
                => Task.FromResult<MetaConfigVersion?>(null);
            public Task<ConfigBootstrapBytes?> GetBytesAsync<TConfig>(MetaConfigVersion version, System.Threading.CancellationToken ct) where TConfig : class
                => Task.FromResult<ConfigBootstrapBytes?>(null);
        }
    }
}
