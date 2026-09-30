using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Force-patch fixture — the only service in this assembly with a version band
    //  (Version 1 over MinCompatibleVersion 0). A client signature declaring Bump at v0
    //  negotiates ForceServerPatch for it, which is how tests stand up a legacy subscriber
    //  without a second client build.
    // ════════════════════════════════════════════════════════════════════════════

    [MemoryPackable]
    public partial class ForcePatchFixtureState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
    }

    [MetaService(StateType = typeof(ForcePatchFixtureState))]
    public interface IForcePatchFixtureService : IMetaService
    {
        [MetaMethod(Alias = "Bump", Mode = ExecutionMode.Optimistic, Version = 1)]
        int Bump(int amount);
    }

    [MetaServiceImpl(typeof(IForcePatchFixtureService), typeof(ForcePatchFixtureState))]
    public partial class ForcePatchFixtureService : IForcePatchFixtureService
    {
        public int Bump(int amount)
        {
            State.Value += amount;
            return State.Value;
        }
    }
}
