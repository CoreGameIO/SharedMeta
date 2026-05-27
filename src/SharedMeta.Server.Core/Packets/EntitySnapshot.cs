using System;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Snapshot of entity state. <see cref="StateBytes"/> may be a slice into a pool-rented
    /// buffer owned by the producing <c>EntityGrain</c>; the buffer is reclaimed at the next
    /// <c>HandleCallAsync</c> entry on that grain (Orleans deep-copies <see cref="EntitySnapshot"/>
    /// across the SessionManager grain hop, so the source slice is safe to recycle by the
    /// time the next RPC arrives — in-silo single-thread grain execution).
    /// </summary>
    [GenerateSerializer, Immutable]
    public struct EntitySnapshot
    {
        [Id(0)] public ReadOnlyMemory<byte> StateBytes { get; set; }
        [Id(1)] public long CurrentSequenceNumber { get; set; }
        [Id(2)] public byte[]? OptimisticRandomBytes { get; set; }
        [Id(3)] public MetaConfigVersion ConfigVersion { get; set; }
        [Id(4)] public byte[]? NamedRandomsBytes { get; set; }

        /// <summary>
        /// Per-entity capability deltas for the subscribing player. Computed from this
        /// entity's resolved config version + the bound config's
        /// <c>[MetaConfigStructureBoundary]</c> declarations. Forwarded to the client via
        /// <c>SubscribeResponse.AugmentedCapabilities</c>.
        /// </summary>
        [Id(5)] public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }
    }
}
