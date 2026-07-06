using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [ServiceConfig] pin / EntityScope.Global parity fixtures (0.33.0 Phase A).
    //  Mirrors EntityScopeFixtures.cs's Shared/Global pattern exactly, but declares
    //  its config via [ServiceConfig] instead of the legacy [MetaService(ConfigType=...)]
    //  — proves pin establishment (Shared) and CurrentClientVersion substitution
    //  (Global) work identically for [ServiceConfig] entries. No [MetaStateVersion]
    //  here — that's Phase B (schema-floor migration parity), tested separately.
    // ════════════════════════════════════════════════════════════════════════════

    // ── Shared scope ────────────────────────────────────────────────────────────

    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    [MetaConfigVersion(Client = "1.0.5", Config = "1.0.5")]
    [MetaConfigVersion(Client = "2.0.0", Config = "2.0.0")]
    public class ServiceConfigSharedScopeConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Patch { get; set; }
    }

    [SharedState]
    [EntityScope(EntityScope.Shared)]
    [MemoryPackable]
    public partial class ServiceConfigSharedScopeState : ISharedState
    {
        [MemoryPackOrder(0)] public List<(int Major, int Minor, int Patch)> ConfigsSeen { get; set; } = new();
    }

    [MetaService(StateType = typeof(ServiceConfigSharedScopeState))]
    [ServiceConfig(typeof(ServiceConfigSharedScopeConfig), "Config")]
    public interface IServiceConfigSharedScopeService : IMetaService
    {
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task RecordConfig();

        [MetaMethod(Mode = ExecutionMode.Query)]
        ConfigSnapshot GetLastConfig();
    }

    [MetaServiceImpl(typeof(IServiceConfigSharedScopeService), typeof(ServiceConfigSharedScopeState))]
    public partial class ServiceConfigSharedScopeService : IServiceConfigSharedScopeService
    {
        private ServiceConfigSharedScopeState S => State;

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

    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.0.0", Config = "2.0.0")]
    public class ServiceConfigGlobalScopeConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    [SharedState]
    [EntityScope(EntityScope.Global)]
    [MemoryPackable]
    public partial class ServiceConfigGlobalScopeState : ISharedState
    {
        [MemoryPackOrder(0)] public int LastMajor { get; set; }
    }

    [MetaService(StateType = typeof(ServiceConfigGlobalScopeState))]
    [ServiceConfig(typeof(ServiceConfigGlobalScopeConfig), "Config")]
    public interface IServiceConfigGlobalScopeService : IMetaService
    {
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<int> RecordConfig();

        [MetaMethod(Mode = ExecutionMode.Query)]
        int GetLastMajor();
    }

    [MetaServiceImpl(typeof(IServiceConfigGlobalScopeService), typeof(ServiceConfigGlobalScopeState))]
    public partial class ServiceConfigGlobalScopeService : IServiceConfigGlobalScopeService
    {
        public Task<int> RecordConfig()
        {
            State.LastMajor = Config.Major;
            return Task.FromResult(Config.Major);
        }

        public int GetLastMajor() => State.LastMajor;
    }
}
