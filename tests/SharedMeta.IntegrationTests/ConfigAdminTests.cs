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
            // Contract: returning null is the documented "no seed available" signal.
            IConfigBootstrapper b = new NullBootstrapper();
            var seed = await b.LoadAsync(typeof(object), default);
            Assert.Null(seed);
        }

        [Fact]
        public void ConfigBootstrapSeed_HasReasonableDefaults()
        {
            var seed = new ConfigBootstrapSeed
            {
                Version = new MetaConfigVersion(1, 0, 0),
                Bytes = new byte[] { 1, 2, 3 },
            };
            Assert.Equal("bootstrap", seed.Origin);
            Assert.Equal("bootstrap", seed.PublishedBy);
            Assert.Null(seed.Notes);
        }

        private sealed class NullBootstrapper : IConfigBootstrapper
        {
            public Task<ConfigBootstrapSeed?> LoadAsync(Type configType, System.Threading.CancellationToken ct)
                => Task.FromResult<ConfigBootstrapSeed?>(null);
        }
    }
}
