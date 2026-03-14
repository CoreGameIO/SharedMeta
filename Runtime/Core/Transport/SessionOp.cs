using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;
using SharedMeta.Core.Packets;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Unified operation from server to client.
    /// Used for BOTH broadcasts and RPC responses — only RequestId distinguishes them.
    /// Contains the primary operation, optional triggered operations, and routing info.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SessionOp
    {
        /// <summary>
        /// Target entity this operation belongs to.
        /// </summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>
        /// 0 = broadcast/push, greater than 0 = response to a specific client RPC.
        /// Client matches this to the pending TCS to complete the RPC call.
        /// </summary>
        [Id(1), Key(1)] public long RequestId { get; set; }

        /// <summary>
        /// The primary operation (call info + response/replay data).
        /// For broadcasts: contains the method that was called and its replay payload.
        /// For RPC responses: contains the call info and result bytes + replay payload.
        /// </summary>
        [Id(2), Key(2)] public OperationResult MainOperation { get; set; } = new();

        /// <summary>
        /// Triggered operations executed after the main call (if any).
        /// Triggers are server-side methods invoked as a consequence of the main method.
        /// Both caller and subscribers need to replay them.
        /// </summary>
        [Id(3), Key(3)] public List<OperationResult>? TriggerOperations { get; set; }

        /// <summary>
        /// Top-level error if dispatch failed before the method was executed.
        /// For method-level errors, check MainOperation.Response.Error.
        /// </summary>
        [Id(4), Key(4)] public string? Error { get; set; }

        /// <summary>
        /// Cross-entity call results (for CrossOptimistic desync validation).
        /// Populated when the main call made cross-entity calls on the server.
        /// </summary>
        [Id(5), Key(5)] public List<CrossEntityOperationInfo>? CrossEntityOperations { get; set; }

        /// <summary>True if there was any error.</summary>
        [IgnoreMember] public bool HasError => Error != null || MainOperation.Response.Error != null;

        /// <summary>Combined error message.</summary>
        [IgnoreMember] public string? ErrorMessage => Error ?? MainOperation.Response.Error;

        /// <summary>Shortcut to MainOperation.Call.ServiceName.</summary>
        [IgnoreMember] public string ServiceName => MainOperation.Call.ServiceName;

        /// <summary>Shortcut to MainOperation.Call.MethodName.</summary>
        [IgnoreMember] public string MethodName => MainOperation.Call.MethodName;
    }
}
