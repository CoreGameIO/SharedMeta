using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response from a query call. Simple success/error with result bytes.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class QueryCallResponse
    {
        [Id(0), Key(0)] public bool Success { get; set; }
        [Id(1), Key(1)] public byte[]? ResultBytes { get; set; }
        [Id(2), Key(2)] public string? Error { get; set; }
    }
}
