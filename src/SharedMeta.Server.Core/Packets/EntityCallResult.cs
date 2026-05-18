using System.Collections.Generic;
using Orleans;
using SharedMeta.Core.Packets;
using SharedMeta.Server;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Result of handling an RPC call from EntityGrain.
    /// Carries an entity-level sequence number, a canonical <see cref="MetaOperation"/> payload
    /// (call + response + nested triggers), and cross-entity call info.
    ///
    /// Layer separation:
    /// - <see cref="EntitySequenceNumber"/>: managed by EntityGrain (Entity layer)
    /// - <see cref="Op"/>: business data — what was called + what came out (Business layer)
    /// - <c>SessionSequenceNumber</c>: added by SessionManager (Transport layer)
    ///
    /// Pre-0.24 this type carried <c>MainOperation: OperationResult</c> and
    /// <c>TriggerOperations: List&lt;OperationResult&gt;</c>; both halves now live in
    /// <see cref="Op"/> and <see cref="MetaOperation.Triggers"/>.
    /// </summary>
    [GenerateSerializer, Immutable]
    public class EntityCallResult
    {
        /// <summary>
        /// Entity-level sequence number after this operation.
        /// Managed by EntityGrain - incremented for each operation.
        /// </summary>
        [Id(0)] public long EntitySequenceNumber { get; set; }

        /// <summary>
        /// The canonical operation payload (call info + response + nested triggers).
        /// </summary>
        [Id(1)] public MetaOperation Op { get; set; } = new();

        /// <summary>
        /// Cross-entity calls made during this operation (entityId + sequenceNumber + result).
        /// Used by SessionManager for broadcast suppression and by client for desync validation.
        /// </summary>
        [Id(2)] public List<CrossEntityCallInfo>? CrossEntityCalls { get; set; }

        /// <summary>
        /// Top-level error if dispatch failed before the method body ran.
        /// For method-body errors, check <see cref="MetaOperation.Error"/> on <see cref="Op"/>.
        /// </summary>
        [Id(3)] public string? Error { get; set; }

        /// <summary>True if there was an error.</summary>
        public bool HasError => Error != null || Op.Error != null;

        /// <summary>Error message if any.</summary>
        public string? ErrorMessage => Error ?? Op.Error;

        /// <summary>Shortcut to <see cref="MetaOperation.ServiceName"/> on <see cref="Op"/>.</summary>
        public string ServiceName => Op.ServiceName;

        /// <summary>Shortcut to <see cref="MetaOperation.MethodName"/> on <see cref="Op"/>.</summary>
        public string MethodName => Op.MethodName;
    }
}
