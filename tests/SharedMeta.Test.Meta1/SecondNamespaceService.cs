using MemoryPack;
using MessagePack;
using Orleans;
using SharedMeta.Core;

// Deliberately NOT SharedMeta.Test.Meta1 — this is the whole point of the fixture.
//
// `GameMethodIds` used to be emitted once, into the namespace of whichever service the
// collection pipeline happened to see first, while every per-service emitter (dispatcher,
// api client, query, server api, recorder) references `{its own interface's namespace}
// .Generated.GameMethodIds`. Those two agree only while an assembly has exactly one
// namespace — which every test project here did, so nothing ever caught it. A second
// namespace produced CS0234 for whichever one lost the race, and which one that was
// depended on collection order.
//
// This file exists to be compiled. If the table stops being emitted per-namespace, this
// assembly stops building, which is a louder failure than any assertion.
namespace SharedMeta.Test.Meta1.SecondNamespace
{
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class OutpostState : ISharedState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public int Supplies { get; set; }
    }

    [MetaService(StateType = typeof(OutpostState))]
    public interface IOutpostService : IMetaService
    {
        [MetaMethod(Alias = "Deliver", Mode = ExecutionMode.Server)]
        int Deliver(int amount);
    }

    [MetaServiceImpl(typeof(IOutpostService), typeof(OutpostState))]
    public partial class OutpostService : IOutpostService
    {
        public int Deliver(int amount)
        {
            Context.State.Supplies += amount;
            return Context.State.Supplies;
        }
    }
}
