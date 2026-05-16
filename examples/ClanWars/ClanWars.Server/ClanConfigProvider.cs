using ClanWars.Shared;
using SharedMeta.Core;
using SharedMeta.Server.Core;

namespace ClanWars.Server
{
    /// <summary>
    /// Minimal hardcoded provider with two declared versions (1.0 and 2.0). v2 differs from
    /// v1 only in the additive friendship + officer fields — values are nominal here, the
    /// stress-test cares about the version difference, not the numeric content.
    /// </summary>
    public class ClanConfigProvider : IMetaConfigProvider<ClanConfig>
    {
        private static readonly ClanConfig V1 = new()
        {
            CreateClanCost = 100,
            MaxMembers = 100,
            // v2-only fields stay zero in v1 — irrelevant for v1 method paths.
        };

        private static readonly ClanConfig V2 = new()
        {
            CreateClanCost = 100,
            MaxMembers = 100,
            MaxFriendships = 10,
            MaxOfficers = 5,
        };

        public ClanConfig GetConfig(MetaConfigVersion version)
        {
            if (version.Major >= 2) return V2;
            return V1;
        }
    }
}
