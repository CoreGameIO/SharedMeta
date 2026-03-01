using System.Collections.Generic;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Packets;
using SharedMeta.Server;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Result of handling an RPC call from EntityGrain.
    /// Contains the entity sequence number and the operation result.
    ///
    /// Layer separation:
    /// - EntitySequenceNumber: managed by EntityGrain (Entity layer)
    /// - MainOperation + TriggerOperations: business data (Business layer)
    /// - SessionSequenceNumber: added by SessionManager (Transport layer)
    /// </summary>
    [GenerateSerializer]
    public class EntityCallResult
    {
        /// <summary>
        /// Entity-level sequence number after this operation.
        /// Managed by EntityGrain - incremented for each operation.
        /// </summary>
        [Id(0)] public long EntitySequenceNumber { get; set; }

        /// <summary>
        /// The primary operation result (RpcCall + RpcResponse).
        /// </summary>
        [Id(1)] public OperationResult MainOperation { get; set; } = new();

        /// <summary>
        /// Triggered operations executed after the main call.
        /// </summary>
        [Id(2)] public List<OperationResult>? TriggerOperations { get; set; }

        /// <summary>
        /// Cross-entity calls made during this operation (entityId + sequenceNumber + result).
        /// Used by SessionManager for broadcast suppression and by client for desync validation.
        /// </summary>
        [Id(4)] public List<CrossEntityCallInfo>? CrossEntityCalls { get; set; }

        /// <summary>
        /// Top-level error if dispatch failed.
        /// </summary>
        [Id(3)] public string? Error { get; set; }

        /// <summary>True if there was an error.</summary>
        public bool HasError => Error != null || MainOperation.Response.Error != null;

        /// <summary>Error message if any.</summary>
        public string? ErrorMessage => Error ?? MainOperation.Response.Error;

        /// <summary>Shortcut to MainOperation.Call.ServiceName.</summary>
        public string ServiceName => MainOperation.Call.ServiceName;

        /// <summary>Shortcut to MainOperation.Call.MethodName.</summary>
        public string MethodName => MainOperation.Call.MethodName;
    }
}
