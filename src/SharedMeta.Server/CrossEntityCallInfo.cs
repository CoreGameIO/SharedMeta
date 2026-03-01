using System;
using Orleans;

namespace SharedMeta.Server
{
    /// <summary>
    /// Information about a cross-entity call made during server-side execution.
    /// Collected by ServerMetaContext for broadcast suppression and desync validation.
    /// </summary>
    [GenerateSerializer]
    public class CrossEntityCallInfo
    {
        [Id(0)] public string EntityId { get; set; } = "";
        [Id(1)] public long EntitySequenceNumber { get; set; }
        [Id(2)] public byte[]? ResultBytes { get; set; }
        [Id(3)] public string ServiceName { get; set; } = "";
        [Id(4)] public string MethodName { get; set; } = "";
    }
}
