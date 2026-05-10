using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.SplitConfig.Models
{
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SplitConsumerState : ISharedState
    {
        [MemoryPackOrder(0)] public int Counter { get; set; }
    }
}
