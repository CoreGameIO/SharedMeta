using System;
using System.Collections.Generic;
using Orleans;
using SharedMeta.Core.Packets;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Result of handling an RPC call from EntityGrain. MetaOperation does not cross this
    /// boundary: the grain serializes its pooled MetaOperation to <see cref="OpBytes"/>
    /// before returning. SessionManagerGrain forwards the payload unchanged into the wire frame.
    /// </summary>
    [GenerateSerializer, Immutable]
    public struct EntityCallResult
    {
        /// <summary>Entity-level sequence number after this operation.</summary>
        [Id(0)] public long EntitySequenceNumber { get; set; }

        /// <summary>Pre-serialized MetaOperation (full response shape). Backed by a GC byte[];
        /// the struct is <c>[Immutable]</c> so Orleans shares by reference on in-silo hops.
        /// Empty when dispatch failed before producing a payload.</summary>
        [Id(1)] public ReadOnlyMemory<byte> OpBytes { get; set; }

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
