using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [ServiceConfig] schema-floor migration parity fixture (0.33.0 Phase B).
    //  Mirrors MigrationTest.cs's pattern exactly, but the [MetaStateVersion] AND-gate
    //  is declared against a [ServiceConfig]-linked type — NO legacy ConfigType at all —
    //  proving [MetaInit] step timing, [NoMigrate] schema-floor pinning, and the
    //  Breaking-schema compat gate all work for [ServiceConfig] entries, not just the
    //  legacy primary.
    // ════════════════════════════════════════════════════════════════════════════

    [MetaConfigVersion(Client = "0.x.*", Config = "0.x.*")]
    [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]
    public class ServiceConfigMigrationConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    /// <summary>
    /// Two schema breakpoints against a [ServiceConfig]-linked type (no legacy ConfigType):
    ///   schema 1: ServiceConfigMigrationConfig >= 1.0
    ///   schema 2: ServiceConfigMigrationConfig >= 2.0 (Breaking — rejects old clients once reached)
    /// </summary>
    [MemoryPackable]
    [MetaStateVersion(1, "1.0", typeof(ServiceConfigMigrationConfig))]
    [MetaStateVersion(2, "2.0", typeof(ServiceConfigMigrationConfig), Breaking = true)]
    public partial class ServiceConfigMigrationState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
        [MemoryPackOrder(1)] public List<(int Major, int Minor)> InitLog { get; set; } = new();
    }

    [MetaService(StateType = typeof(ServiceConfigMigrationState))]
    [ServiceConfig(typeof(ServiceConfigMigrationConfig), "Config")]
    public interface IServiceConfigMigrationService : IMetaService
    {
        [MetaMethod(Alias = "GetValue", Mode = ExecutionMode.Query)]
        int GetValue();

        /// <summary>
        /// [NoMigrate]: must never trigger migration and must see Config pinned to the
        /// schema-floor branch for CurrentStateSchemaVersion — not the caller's live branch.
        /// </summary>
        [MetaMethod(Alias = "GetFloorConfigMajor", Mode = ExecutionMode.Server), NoMigrate]
        Task<int> GetFloorConfigMajor();
    }

    [MetaServiceImpl(typeof(IServiceConfigMigrationService), typeof(ServiceConfigMigrationState))]
    public partial class ServiceConfigMigrationService : IServiceConfigMigrationService
    {
        private ServiceConfigMigrationState S => State;

        public int GetValue() => S.Value;

        public Task<int> GetFloorConfigMajor() => Task.FromResult(Config.Major);

        [MetaInit]
        public Task<int> Init(int version)
        {
            S.InitLog.Add((Config.Major, Config.Minor));
            if (version < 1) { S.Value = 1; return Task.FromResult(1); }
            if (version < 2) { S.Value = 2; return Task.FromResult(2); }
            return Task.FromResult(version);
        }
    }
}
