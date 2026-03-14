using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Packets
{
    /// <summary>
    /// Single operation result - combines call information with response.
    /// Used for both main operations and triggers.
    /// Contains everything needed to replay the operation on other clients.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class OperationResult
    {
        /// <summary>
        /// Information about the call (needed for replay on other clients).
        /// Contains ServiceName, MethodName, and argument Payload.
        /// </summary>
        [Id(0), Key(0)] public RpcCall Call { get; set; } = new();

        /// <summary>
        /// Execution result (ResultBytes, ReplayPayload for deterministic replay).
        /// </summary>
        [Id(1), Key(1)] public RpcResponse Response { get; set; } = new();
    }
}
