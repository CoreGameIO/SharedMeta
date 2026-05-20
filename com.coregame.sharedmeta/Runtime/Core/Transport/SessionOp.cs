using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;
using SharedMeta.Core.Packets;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Unified operation from server to client. Used for BOTH broadcasts and RPC responses —
    /// only <see cref="RequestId"/> distinguishes them. The payload is a pre-serialized
    /// <see cref="MetaOperation"/> as raw bytes — the client deserializes <see cref="OpBytes"/>
    /// once via <c>IMetaSerializer.Unpack&lt;MetaOperation&gt;</c> to read its fields.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer, Immutable]
    public partial class SessionOp
    {
        /// <summary>Target entity this operation belongs to.</summary>
        [Id(0), Key(0)] public string EntityId { get; set; } = "";

        /// <summary>0 = broadcast/push, greater than 0 = response to a specific client RPC.
        /// Client matches this to the pending TCS to complete the RPC call.</summary>
        [Id(1), Key(1)] public long RequestId { get; set; }

        /// <summary>Pre-serialized MetaOperation. Empty when dispatch failed before producing
        /// a payload (check <see cref="Error"/>). Decode once on the client via
        /// <c>IMetaSerializer.Unpack&lt;MetaOperation&gt;</c>.</summary>
        [Id(2), Key(2)] public byte[] OpBytes { get; set; } = Array.Empty<byte>();

        /// <summary>Top-level error if dispatch failed before the method was executed.
        /// Method-level errors are embedded inside <see cref="OpBytes"/> via
        /// <c>MetaOperation.Error</c>.</summary>
        [Id(3), Key(3)] public string? Error { get; set; }

        /// <summary>Cross-entity call results (for CrossOptimistic desync validation).
        /// Populated when the main call made cross-entity calls on the server.</summary>
        [Id(4), Key(4)] public List<CrossEntityOperationInfo>? CrossEntityOperations { get; set; }

        /// <summary>True if there was a top-level error.</summary>
        [IgnoreMember] public bool HasError => Error != null;

        /// <summary>Top-level error message if any.</summary>
        [IgnoreMember] public string? ErrorMessage => Error;
    }
}
