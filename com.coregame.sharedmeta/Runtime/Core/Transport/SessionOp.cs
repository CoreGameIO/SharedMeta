using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;
using SharedMeta.Core.Packets;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Unified operation from server to client.
    /// Used for BOTH broadcasts and RPC responses — only <see cref="RequestId"/> distinguishes them.
    /// Carries a single canonical <see cref="MetaOperation"/> payload (the main op + any nested
    /// triggers via <see cref="MetaOperation.Triggers"/>) plus routing/dispatch fields.
    /// <para>
    /// Pre-0.24 this type held <c>MainOperation: OperationResult</c> +
    /// <c>TriggerOperations: List&lt;OperationResult&gt;</c>; both halves now live in
    /// <see cref="Op"/> with triggers nested as <c>Op.Triggers</c>.
    /// </para>
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer, Immutable]
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
        /// The canonical operation payload (call info + response + nested triggers).
        /// For broadcasts: contains the method that was called and its replay payload / patch / state.
        /// For RPC responses: contains the call info, result bytes, replay payload, patch / state.
        /// </summary>
        [Id(2), Key(2)] public MetaOperation Op { get; set; } = new();

        /// <summary>
        /// Top-level error if dispatch failed before the method was executed.
        /// For method-level errors, check <see cref="MetaOperation.Error"/> on <see cref="Op"/>.
        /// </summary>
        [Id(3), Key(3)] public string? Error { get; set; }

        /// <summary>
        /// Cross-entity call results (for CrossOptimistic desync validation).
        /// Populated when the main call made cross-entity calls on the server.
        /// </summary>
        [Id(4), Key(4)] public List<CrossEntityOperationInfo>? CrossEntityOperations { get; set; }

        /// <summary>True if there was any error.</summary>
        [IgnoreMember] public bool HasError => Error != null || Op.Error != null;

        /// <summary>Combined error message.</summary>
        [IgnoreMember] public string? ErrorMessage => Error ?? Op.Error;

        /// <summary>Shortcut to <see cref="MetaOperation.ServiceName"/> on <see cref="Op"/>.</summary>
        [IgnoreMember] public string ServiceName => Op.ServiceName;

        /// <summary>Shortcut to <see cref="MetaOperation.MethodName"/> on <see cref="Op"/>.</summary>
        [IgnoreMember] public string MethodName => Op.MethodName;
    }
}
