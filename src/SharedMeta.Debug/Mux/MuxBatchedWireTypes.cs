using System.Collections.Generic;
using MemoryPack;
using MessagePack;
using Orleans;
using SharedMeta.Core.Transport;

namespace SharedMeta.Debug.Mux
{
    /// <summary>
    /// Single entry inside a batched RPC call. Pairs a session-tag with one request
    /// and a per-batch correlation id so the response side can match results.
    /// </summary>
    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    [Immutable]
    public partial class BatchRpcEntry
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int CorrelationId { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public int SessionTag { get; set; }
        [MemoryPackOrder(2), Id(2), Key(2)] public RpcCallRequest Request { get; set; } = new();
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    [Immutable]
    public partial class BatchRpcRequest
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public List<BatchRpcEntry> Entries { get; set; } = new();
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    [Immutable]
    public partial class BatchRpcResult
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public int CorrelationId { get; set; }
        [MemoryPackOrder(1), Id(1), Key(1)] public SessionResponse Response { get; set; } = new();
    }

    [MemoryPackable]
    [MessagePackObject]
    [GenerateSerializer]
    [Immutable]
    public partial class BatchRpcResponse
    {
        [MemoryPackOrder(0), Id(0), Key(0)] public List<BatchRpcResult> Results { get; set; } = new();
    }
}
