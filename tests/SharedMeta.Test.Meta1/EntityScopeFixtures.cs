using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Fixtures for EntityScope integration tests (0.21.0).
    //
    //  Two states:
    //   • SharedScopeState  — [EntityScope(Shared)], pin established on first subscribe;
    //     subsequent joiners validated against pin (patch downgrade tolerated, Major.Minor
    //     mismatch rejects).
    //   • GlobalScopeState  — [EntityScope(Global)], no pin; migration driven by
    //     IConfigVersionResolver.CurrentClientVersion. Compat gate rejects clients whose
    //     resolved config can't cover the current state schema.
    // ════════════════════════════════════════════════════════════════════════════

    // ── Shared scope ────────────────────────────────────────────────────────────

    /// <summary>
    /// Config for the Shared-scope fixture. Maps client app version → config version 1:1
    /// via literal rules so tests can directly assert which version got pinned / served.
    /// </summary>
    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    [MetaConfigVersion(Client = "1.0.5", Config = "1.0.5")]
    [MetaConfigVersion(Client = "2.0.0", Config = "2.0.0")]
    public class SharedScopeConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Patch { get; set; }
    }

    [SharedState]
    [EntityScope(EntityScope.Shared)]
    [MemoryPackable]
    public partial class SharedScopeState : ISharedState
    {
        /// <summary>(Major, Minor, Patch) of <c>Context.Config</c> seen at each RecordConfig call.</summary>
        [MemoryPackOrder(0)] public List<(int Major, int Minor, int Patch)> ConfigsSeen { get; set; } = new();
    }

    [MetaService(StateType = typeof(SharedScopeState), ConfigType = typeof(SharedScopeConfig), DefaultConfig = true)]
    public interface ISharedScopeService : IMetaService
    {
        /// <summary>Server-mode call that records the Config version it saw into state.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task RecordConfig();

        /// <summary>Query the last (Major, Minor, Patch) recorded.</summary>
        [MetaMethod(Mode = ExecutionMode.Query)]
        ConfigSnapshot GetLastConfig();
    }

    /// <summary>
    /// Wire-safe snapshot tuple. xUnit assertions read .Major / .Minor / .Patch directly.
    /// </summary>
    [MemoryPackable]
    public partial class ConfigSnapshot
    {
        [MemoryPackOrder(0)] public int Major { get; set; }
        [MemoryPackOrder(1)] public int Minor { get; set; }
        [MemoryPackOrder(2)] public int Patch { get; set; }
    }

    [MetaServiceImpl(typeof(ISharedScopeService), typeof(SharedScopeState))]
    public partial class SharedScopeService : ISharedScopeService
    {
        private SharedScopeState S => State;

        public Task RecordConfig()
        {
            S.ConfigsSeen.Add((Config.Major, Config.Minor, Config.Patch));
            return Task.CompletedTask;
        }

        public ConfigSnapshot GetLastConfig()
        {
            if (S.ConfigsSeen.Count == 0)
                return new ConfigSnapshot { Major = 0, Minor = 0, Patch = 0 };
            var last = S.ConfigsSeen[S.ConfigsSeen.Count - 1];
            return new ConfigSnapshot { Major = last.Major, Minor = last.Minor, Patch = last.Patch };
        }
    }

    // ── Global scope ────────────────────────────────────────────────────────────

    /// <summary>
    /// Config for the Global-scope fixture. Schema 2 (declared on <see cref="GlobalScopeState"/>)
    /// requires <c>GlobalScopeConfig >= 2.0</c>, so a 1.x client cannot subscribe once the
    /// state has been migrated to schema 2 by the server's <see cref="IConfigVersionResolver.CurrentClientVersion"/>.
    /// </summary>
    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.0.0", Config = "2.0.0")]
    public class GlobalScopeConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    [SharedState]
    [EntityScope(EntityScope.Global)]
    [MetaStateVersion(2, "2.0", typeof(GlobalScopeConfig))]
    [MemoryPackable]
    public partial class GlobalScopeState : ISharedState
    {
        /// <summary>Marker: 1 = schema-1 base init ran, 2 = schema-2 migration ran.</summary>
        [MemoryPackOrder(0)] public int Value { get; set; }
        /// <summary>(Major, Minor) of <c>Context.Config</c> at the last <c>[MetaInit]</c> step.</summary>
        [MemoryPackOrder(1)] public int InitConfigMajor { get; set; }
        [MemoryPackOrder(2)] public int InitConfigMinor { get; set; }
    }

    [MetaService(StateType = typeof(GlobalScopeState), ConfigType = typeof(GlobalScopeConfig), DefaultConfig = true)]
    public interface IGlobalScopeService : IMetaService
    {
        [MetaMethod(Mode = ExecutionMode.Query)]
        int GetValue();

        [MetaMethod(Mode = ExecutionMode.Query)]
        ConfigSnapshot GetInitConfig();
    }

    [MetaServiceImpl(typeof(IGlobalScopeService), typeof(GlobalScopeState))]
    public partial class GlobalScopeService : IGlobalScopeService
    {
        private GlobalScopeState S => State;

        [MetaInit]
        public Task<int> Init(int version)
        {
            S.InitConfigMajor = Config.Major;
            S.InitConfigMinor = Config.Minor;
            if (version < 1) { S.Value = 1; return Task.FromResult(1); }
            if (version < 2) { S.Value = 2; return Task.FromResult(2); }
            return Task.FromResult(version);
        }

        public int GetValue() => S.Value;

        public ConfigSnapshot GetInitConfig()
            => new ConfigSnapshot { Major = S.InitConfigMajor, Minor = S.InitConfigMinor, Patch = 0 };
    }
}
