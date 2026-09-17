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
    // VersionTolerant so future field additions don't break readers of pre-add bytes (in
    // particular SessionResponse.Operations cached in SessionManagerGrainState.PendingPackets,
    // which round-trips through Orleans persistence). Without this, adding [Id(N)] on this
    // struct in a future release makes existing on-disk persistent state un-readable AND
    // cascades garbage offsets into the inner OpBytes (which then fails MetaOperation Unpack
    // with "Sequence reached end").
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer, Immutable]
    public partial struct SessionOp
    {
        /// <summary>Target entity this operation belongs to.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public string EntityId { get; set; }

        /// <summary>0 = broadcast/push, greater than 0 = response to a specific client RPC.
        /// Client matches this to the pending TCS to complete the RPC call.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public long RequestId { get; set; }

        /// <summary>Pre-serialized MetaOperation. Empty when dispatch failed before producing
        /// a payload (check <see cref="Error"/>). Decode once on the client via
        /// <c>IMetaSerializer.Unpack&lt;MetaOperation&gt;</c>.</summary>
        [Id(2), Key(2), MemoryPackOrder(2), MemoryPackAllowSerialize] public ReadOnlyMemory<byte> OpBytes { get; set; }

        /// <summary>Top-level error if dispatch failed before the method was executed.
        /// Method-level errors are embedded inside <see cref="OpBytes"/> via
        /// <c>MetaOperation.Error</c>.</summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public string? Error { get; set; }

        /// <summary>Cross-entity call results (for CrossOptimistic desync validation).
        /// Populated when the main call made cross-entity calls on the server.</summary>
        [Id(4), Key(4), MemoryPackOrder(4)] public List<CrossEntityOperationInfo>? CrossEntityOperations { get; set; }

        /// <summary>
        /// 0.24.0+ Per-entity broadcast sequence number this op was produced under. Client
        /// tracks the highest seq per entity to populate
        /// <see cref="SubscriptionClaim.LastKnownEntitySequence"/> on the next Resume, which
        /// enables the entity grain's Continued cheap-path (no gap detected, no state shipped).
        /// Zero for ops that have no per-entity seq association (e.g. transient session-level
        /// error responses produced without going through the entity).
        /// </summary>
        [Id(5), Key(5), MemoryPackOrder(5)] public long EntitySequenceNumber { get; set; }

        /// <summary>True if there was a top-level error.</summary>
        [IgnoreMember, MemoryPackIgnore] public bool HasError => Error != null;

        /// <summary>Top-level error message if any.</summary>
        [IgnoreMember, MemoryPackIgnore] public string? ErrorMessage => Error;
    }
}
