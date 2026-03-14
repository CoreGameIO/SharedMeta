using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core
{
    /// <summary>
    /// Optional debug information for payload contents.
    /// Generated when RuntimeMetaConfig.EnablePayloadDebug is true.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class PayloadDebug
    {
        [Id(0), Key(0)] public List<string> PayloadItemInfo { get; set; } = new();
    }
}
