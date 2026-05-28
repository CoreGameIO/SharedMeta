using System;
using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Slim return type for cross-entity calls (<c>HandleCallFromEntityAsync</c>). The source
    /// grain only needs the method return value plus the target's post-call sequence number
    /// for client-side desync validation — it never reads the full MetaOperation. By contrast,
    /// <see cref="EntityCallResult"/> (returned from <c>HandleCallAsync</c> to SessionManager)
    /// carries the full pre-serialized <c>OpBytes</c> for the wire frame.
    /// </summary>
    [GenerateSerializer, Immutable]
    public struct CrossEntityCallReturn
    {
        /// <summary>Target entity's sequence number after this operation. Used by the source
        /// grain to populate <c>CrossEntityCallInfo.EntitySequenceNumber</c> for client replay.</summary>
        [Id(0)] public long EntitySequenceNumber { get; set; }

        /// <summary>Method return value. Backed by a GC byte[] (class-level <c>[Immutable]</c>
        /// lets Orleans share by reference on in-silo hops). Empty when the target method
        /// returned void or when an error prevented dispatch.</summary>
        [Id(1)] public ReadOnlyMemory<byte> ResultBytes { get; set; }

        /// <summary>Top-level error message if dispatch failed. The source grain throws on
        /// non-null to propagate the failure back to its own caller.</summary>
        [Id(2)] public string? Error { get; set; }

        /// <summary>True when a top-level error was reported.</summary>
        public bool HasError => Error != null;
    }
}
