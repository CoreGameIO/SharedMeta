using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [ServiceConfig] fixture (0.33.0) — a service with a legacy primary ConfigType PLUS
    //  two independently-versioned/published [ServiceConfig] entries (this combination
    //  exercises the Obsolete-but-functional back-compat path — legacy ConfigType and new
    //  ServiceConfig coexist). Resolved synchronously in every execution mode (unlike
    //  multi-config siblings / StatelessMetaService, which are server-only). Balance/Season
    //  carry their own [MetaConfigVersion] rules, distinct from each other and from the
    //  primary, so tests can assert independent resolution — not sharing the primary's branch.
    // ════════════════════════════════════════════════════════════════════════════

    // Not [MetaConfig(Default = true)] — CounterConfig already claims that role in this shared
    // test assembly ("only one config class should be marked as default per assembly"). Uses
    // explicit ConfigType instead.
    [MetaConfig]
    public class AdditionalFixturePrimaryConfig
    {
        public int Value { get; set; } = 1;
    }

    /// <summary>Always resolves to Major.Minor = 1.0 regardless of client version.</summary>
    [MetaConfigVersion(Client = "1.x.*", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.x.*", Config = "1.0.0")]
    public class AdditionalFixtureBalanceConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    /// <summary>Mirrors client version — 1.x clients get Season 1.x, 2.x clients get Season 2.x.</summary>
    [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]
    public class AdditionalFixtureSeasonConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    [SharedState]
    [MemoryPackable]
    public partial class AdditionalConfigState : ISharedState
    {
        [MemoryPackOrder(0)] public int LastPrimary { get; set; }
        [MemoryPackOrder(1)] public int LastBalanceMajor { get; set; }
        [MemoryPackOrder(2)] public int LastSeasonMajor { get; set; }
        [MemoryPackOrder(3)] public int LastSeasonMinor { get; set; }
    }

#pragma warning disable CS0618 // ConfigType is Obsolete — deliberately exercised here for legacy+ServiceConfig coexistence coverage.
    [MetaService(StateType = typeof(AdditionalConfigState), ConfigType = typeof(AdditionalFixturePrimaryConfig))]
#pragma warning restore CS0618
    [ServiceConfig(typeof(AdditionalFixtureBalanceConfig), "Balance")]
    [ServiceConfig(typeof(AdditionalFixtureSeasonConfig), "Season")]
    public interface IAdditionalConfigService : IMetaService
    {
        /// <summary>
        /// Reads legacy primary Config + both [ServiceConfig] entries into State, returns
        /// Season.Major so tests can assert which branch resolved. Optimistic — runs on BOTH
        /// client (predictive) and server; unlike multi-config-sibling / StatelessMetaService's
        /// server-only Path 1, [ServiceConfig] resolves synchronously everywhere.
        /// </summary>
        [MetaMethod(Alias = "ReadAll", Mode = ExecutionMode.Optimistic)]
        int ReadAll();

        /// <summary>Server-mode variant, for tests that want to pin the server's authoritative read.</summary>
        [MetaMethod(Alias = "ReadAllServer", Mode = ExecutionMode.Server)]
        int ReadAllServer();
    }

    [MetaServiceImpl(typeof(IAdditionalConfigService), typeof(AdditionalConfigState))]
    public partial class AdditionalConfigService : IAdditionalConfigService
    {
        public int ReadAll()
        {
            State.LastPrimary = Config.Value;
            State.LastBalanceMajor = Balance.Major;
            State.LastSeasonMajor = Season.Major;
            State.LastSeasonMinor = Season.Minor;
            return Season.Major;
        }

        public int ReadAllServer() => ReadAll();
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Fully symmetric fixture — NO legacy ConfigType at all, only [ServiceConfig]
    //  entries. Proves the "no privileged primary" design: every declared config is
    //  resolved through the same mechanism, positionally, with no special-cased index 0.
    // ════════════════════════════════════════════════════════════════════════════

    [MetaConfigVersion(Client = "1.x.*", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.0.0")]
    public class SymmetricFixtureShopConfig
    {
        public int Major { get; set; }
    }

    [MetaConfigVersion(Client = "1.x.*", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.x.*", Config = "1.0.0")]
    public class SymmetricFixtureVaultConfig
    {
        public int Major { get; set; }
    }

    [SharedState]
    [MemoryPackable]
    public partial class SymmetricConfigState : ISharedState
    {
        [MemoryPackOrder(0)] public int LastShopMajor { get; set; }
        [MemoryPackOrder(1)] public int LastVaultMajor { get; set; }
    }

    [MetaService(StateType = typeof(SymmetricConfigState))]
    [ServiceConfig(typeof(SymmetricFixtureShopConfig), "Shop")]
    [ServiceConfig(typeof(SymmetricFixtureVaultConfig), "Vault")]
    public interface ISymmetricConfigService : IMetaService
    {
        [MetaMethod(Alias = "ReadBoth", Mode = ExecutionMode.Optimistic)]
        int ReadBoth();
    }

    [MetaServiceImpl(typeof(ISymmetricConfigService), typeof(SymmetricConfigState))]
    public partial class SymmetricConfigService : ISymmetricConfigService
    {
        public int ReadBoth()
        {
            State.LastShopMajor = Shop.Major;
            State.LastVaultMajor = Vault.Major;
            return Shop.Major;
        }
    }
}
