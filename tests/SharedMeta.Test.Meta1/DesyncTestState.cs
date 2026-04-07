using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MemoryPackable]
    public partial class DesyncTestState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
        [MemoryPackOrder(1)] public int Counter { get; set; }
        [MemoryPackOrder(2)] public string Label { get; set; } = "";
    }
}
