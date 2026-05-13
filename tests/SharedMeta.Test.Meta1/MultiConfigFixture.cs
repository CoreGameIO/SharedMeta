using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Multi-config [MetaStateVersion] AND-gate fixture (0.21.0).
    //  Two distinct config types with INDEPENDENT [MetaConfigVersion] rule sets so
    //  the test can produce client versions that satisfy one threshold but not the
    //  other — exercising the AND semantics in ComputeRequiredStateSchema.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Always at 2.x regardless of client version — fixed mapping. Lets the AND-gate
    /// test produce a clientVersion that satisfies the ConfigA side of schema 2 but
    /// (via independent rules on <see cref="MultiConfigB"/>) fails the ConfigB side.
    /// </summary>
    [MetaConfigVersion(Client = "0.x.*", Config = "2.0.0")]
    [MetaConfigVersion(Client = "1.x.*", Config = "2.0.0")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.0.0")]
    public class MultiConfigA
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    /// <summary>
    /// Mirrors client version. <c>1.x</c> → 1.x; <c>2.x</c> → 2.x.
    /// </summary>
    [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]
    public class MultiConfigB
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    /// <summary>
    /// Schema 2 requires BOTH <c>MultiConfigA ≥ 2.0</c> AND <c>MultiConfigB ≥ 2.0</c>.
    /// Client at "1.0.0": ConfigA=2.0 (matches), ConfigB=1.0 (fails) → AND fails → no migrate.
    /// Client at "2.0.0": ConfigA=2.0 + ConfigB=2.0 → both match → migrate.
    /// </summary>
    [SharedState]
    [MetaStateVersion(2, "2.0", typeof(MultiConfigA))]
    [MetaStateVersion(2, "2.0", typeof(MultiConfigB))]
    [MemoryPackable]
    public partial class MultiConfigState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
    }

    [MetaService(StateType = typeof(MultiConfigState), ConfigType = typeof(MultiConfigA), DefaultConfig = true)]
    public interface IMultiConfigService : IMetaService
    {
        [MetaMethod(Mode = ExecutionMode.Query)]
        int GetValue();
    }

    [MetaServiceImpl(typeof(IMultiConfigService), typeof(MultiConfigState))]
    public partial class MultiConfigService : IMultiConfigService
    {
        [MetaInit]
        public Task<int> Init(int version)
        {
            if (version < 1) { State.Value = 1; return Task.FromResult(1); }
            if (version < 2) { State.Value = 2; return Task.FromResult(2); }
            return Task.FromResult(version);
        }

        public int GetValue() => State.Value;
    }
}
