using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core
{
    [MemoryPackable, MessagePackObject]
    [GenerateSerializer]
    public partial struct VoidArgs
    {
    }
}
