using System.Collections.Generic;
using Orleans;
using SharedMeta.Core.Memory;
using SharedMeta.Core.Packets;
using SharedMeta.Server;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Result of handling an RPC call from EntityGrain. MetaOperation does not cross this
    /// boundary: the grain serializes its pooled MetaOperation to <see cref="OpBytes"/>
    /// before returning. SessionManagerGrain forwards the payload unchanged into the wire
    /// frame and releases the pool ref-count when delivery is acknowledged.
    /// </summary>
    [GenerateSerializer]
    public class EntityCallResult
    {
        /// <summary>Entity-level sequence number after this operation.</summary>
        [Id(0)] public long EntitySequenceNumber { get; set; }

        /// <summary>Pre-serialized MetaOperation (full response shape) as a pool-rented buffer.
        /// Empty (default) when dispatch failed before producing a payload. The sending grain
        /// does NOT release this — the receiving SessionManagerGrain (or wire pipeline) owns the
        /// buffer once it crosses the grain boundary and calls
        /// <c>PooledPayloadRegistry.Release</c> after delivery / eviction. When the registry is
        /// not wired (legacy path) the slot Ref is zero and the inner Memory carries a plain
        /// byte[] that GC reclaims naturally.</summary>
        [Id(1)] public PooledPayload OpBytes { get; set; }

        /// <summary>Cross-entity calls made during this operation (entityId + sequenceNumber +
        /// result). Used by client for desync validation.</summary>
        [Id(2)] public List<CrossEntityOperationInfo>? CrossEntityCalls { get; set; }

        /// <summary>Top-level error if dispatch failed before the method body ran.
        /// Method-body errors are embedded inside <see cref="OpBytes"/> via
        /// <c>MetaOperation.Error</c>.</summary>
        [Id(3)] public string? Error { get; set; }

        /// <summary>True if a top-level error was reported.</summary>
        public bool HasError => Error != null;

        /// <summary>Top-level error message if any.</summary>
        public string? ErrorMessage => Error;
    }
}
