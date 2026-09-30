using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // Method pairs that differ only in nullability. A reference-type annotation is not on the wire,
    // so it must not change the argument hash; Nullable<T> is, so it must.

    [MemoryPackable]
    public partial class NullabilityHashFixtureState : ISharedState
    {
        [MemoryPackOrder(0)] public string? Last { get; set; }
    }

    [MetaService(StateType = typeof(NullabilityHashFixtureState))]
    public interface INullabilityHashFixtureService : IMetaService
    {
        [MetaMethod(Mode = ExecutionMode.Server)]
        string Echo(string value);

        [MetaMethod(Mode = ExecutionMode.Server)]
        string? EchoAnnotated(string? value);

        [MetaMethod(Mode = ExecutionMode.Server)]
        int Count(int value);

        [MetaMethod(Mode = ExecutionMode.Server)]
        int? CountNullable(int? value);
    }

    [MetaServiceImpl(typeof(INullabilityHashFixtureService), typeof(NullabilityHashFixtureState))]
    public partial class NullabilityHashFixtureService : INullabilityHashFixtureService
    {
        public string Echo(string value) => State.Last = value;
        public string? EchoAnnotated(string? value) => State.Last = value;
        public int Count(int value) => value;
        public int? CountNullable(int? value) => value;
    }
}
