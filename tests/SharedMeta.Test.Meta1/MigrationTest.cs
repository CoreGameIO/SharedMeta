using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ─── Config ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Config for migration tests. Reports its version so [MetaInit] can record
    /// which config version was pinned to each migration step.
    ///
    /// <para>0.21.0: declares <c>[MetaConfigVersion]</c> rules mapping the client app version
    /// passed via <c>MetaClientOptions.ClientAppVersion</c> to a config version. The test
    /// framework now drives migration by varying <c>clientAppVersion</c> per test (replacing
    /// the pre-0.21.0 <c>provider.SetVersion(...)</c> ambient-flag pattern).</para>
    /// </summary>
    [MetaConfigVersion(Client = "0.x.*", Config = "0.x.*")]   // sub-1.0 clients map below schema 1 threshold
    [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]   // 1.x → config 1.x
    [MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]   // 2.x → config 2.x
    [MetaConfigVersion(Client = "3.x.*", Config = "3.x.*")]   // 3.x → config 3.x
    public class MigrationConfig
    {
        /// <summary>The version at which this config instance was fetched.</summary>
        public int Major { get; set; }
        public int Minor { get; set; }
        public string Label { get; set; } = "";
    }

    // ─── State ────────────────────────────────────────────────────────────────

    /// <summary>
    /// State with two schema migration breakpoints:
    ///   schema 1: MigrationConfig >= 1.0 (first init / baseline)
    ///   schema 2: MigrationConfig >= 2.0 (upgrade)
    ///   schema 3: MigrationConfig >= 3.0 (second upgrade)
    /// </summary>
    [MemoryPackable]
    [MetaStateVersion(1, "1.0", typeof(MigrationConfig))]
    [MetaStateVersion(2, "2.0", typeof(MigrationConfig))]
    [MetaStateVersion(3, "3.0", typeof(MigrationConfig))]
    public partial class MigrationTestState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }

        /// <summary>
        /// Each element = (configMajor, configMinor) seen at a [MetaInit] invocation.
        /// Lets tests assert that each step received the correctly pinned config version.
        /// </summary>
        [MemoryPackOrder(1)] public List<(int Major, int Minor)> InitLog { get; set; } = new();
    }

    // ─── Service interface ────────────────────────────────────────────────────

    [MetaService(StateType = typeof(MigrationTestState), ConfigType = typeof(MigrationConfig), DefaultConfig = true)]
    public interface IMigrationTestService : IMetaService
    {
        /// <summary>
        /// Query mode: executes on server, returns result directly, no client-side replay.
        /// Safe for reading state that may differ from the client's cached copy (e.g. after
        /// lazy migration updated state.Value on the server without broadcasting).
        /// </summary>
        [MetaMethod(Alias = "GetValue", Mode = ExecutionMode.Query)]
        int GetValue();
    }

    // ─── Service implementation ───────────────────────────────────────────────

    [MetaServiceImpl(typeof(IMigrationTestService), typeof(MigrationTestState))]
    public partial class MigrationTestService : IMigrationTestService
    {
        private MigrationTestState S => State;

        public int GetValue() => S.Value;

        [MetaInit]
        public Task<int> Init(int version)
        {
            // Record the config version pinned by the framework for this step.
            var cfg = Config;                          // typed via generated Context partial
            S.InitLog.Add((cfg.Major, cfg.Minor));

            // Defensive: only apply each schema step once.
            if (version < 1) { S.Value = 1; return Task.FromResult(1); }
            if (version < 2) { S.Value = 2; return Task.FromResult(2); }
            if (version < 3) { S.Value = 3; return Task.FromResult(3); }

            return Task.FromResult(version);
        }
    }
}
