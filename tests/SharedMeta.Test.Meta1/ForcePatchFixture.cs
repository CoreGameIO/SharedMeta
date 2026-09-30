using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Force-patch fixture — the only service in this assembly with a version band
    //  (Version 1 over MinCompatibleVersion 0). A client signature declaring Bump at v0
    //  negotiates ForceServerPatch for it, which is how tests stand up a legacy subscriber
    //  without a second client build. OnBumped is a trigger of Bump with its own band, so a
    //  signature can force-patch the trigger alone.
    // ════════════════════════════════════════════════════════════════════════════

    [MemoryPackable]
    public partial class ForcePatchFixtureState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
        [MemoryPackOrder(1)] public int Bumps { get; set; }
        [MemoryPackOrder(2)] public int LastRoll { get; set; }
        [MemoryPackOrder(3)] public int Rolls { get; set; }
    }

    [MetaService(StateType = typeof(ForcePatchFixtureState))]
    public interface IForcePatchFixtureService : IMetaService
    {
        [MetaMethod(Alias = "Bump", Mode = ExecutionMode.Optimistic, Version = 1)]
        int Bump(int amount);

        [MetaMethod(Alias = "OnBumped", Mode = ExecutionMode.Optimistic, Version = 1)]
        void OnBumped();

        [MetaMethod(Alias = "BumpRandom", Mode = ExecutionMode.Optimistic, Version = 1)]
        int BumpRandom();

        [MetaMethod(Mode = ExecutionMode.ServerReplace)]
        int BumpReplace(int amount);

        [MetaMethod(Mode = ExecutionMode.ServerPatch)]
        int BumpPatch(int amount);

        [MetaMethod(Mode = ExecutionMode.Optimistic)]
        void OnReplaced();
    }

    /// <summary>A second service on the same state, so a client can hold this one alone.</summary>
    [MetaService(StateType = typeof(ForcePatchFixtureState))]
    public interface IForcePatchFixtureViewService : IMetaService
    {
        [MetaMethod(Mode = ExecutionMode.Optimistic)]
        int Roll();
    }

    [MetaServiceImpl(typeof(IForcePatchFixtureViewService), typeof(ForcePatchFixtureState))]
    public partial class ForcePatchFixtureViewService : IForcePatchFixtureViewService
    {
        public int Roll()
        {
            State.LastRoll = Context.Random!.Next(1_000_000);
            State.Rolls++;
            return State.LastRoll;
        }
    }

    [MetaServiceImpl(typeof(IForcePatchFixtureService), typeof(ForcePatchFixtureState))]
    public partial class ForcePatchFixtureService : IForcePatchFixtureService
    {
        public int Bump(int amount)
        {
            State.Value += amount;
            return State.Value;
        }

        public int BumpRandom()
        {
            State.Value += Context.Random!.Next(100);
            return State.Value;
        }

        public int BumpReplace(int amount)
        {
            State.Value += amount;
            return State.Value;
        }

        public int BumpPatch(int amount)
        {
            State.Value += amount;
            return State.Value;
        }

        [Trigger(On = "BumpReplace")]
        public void OnReplaced()
        {
            State.Bumps++;
        }

        [Trigger(On = "Bump")]
        public void OnBumped()
        {
            State.Bumps++;
        }
    }
}
