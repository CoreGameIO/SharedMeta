using System;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core.Packets
{
    /// <summary>
    /// Cross-entity call result for transport between server and client.
    /// Used for CrossOptimistic desync validation.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer, Immutable]
    public partial struct CrossEntityOperationInfo
    {
        [Id(0), Key(0)] public string EntityId { get; set; }
        [Id(1), Key(1)] public long EntitySequenceNumber { get; set; }
        [Id(2), Key(2)] public ushort MethodId { get; set; }
        [Id(3), Key(3)] public byte[]? ResultBytes { get; set; }
    }
}
