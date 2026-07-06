using System;
using System.Collections.Generic;
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
        // 0.33.0+ Id 3 (ConfigVersion, single MetaConfigVersion) retired — see ConfigVersions
        // below. Ephemeral grain-to-grain snapshot (not persisted storage), but kept as a
        // tombstone for consistency with the rest of the codebase's field-numbering discipline.
        [Id(4)] public byte[]? NamedRandomsBytes { get; set; }

        /// <summary>Resolved config version(s) — index 0 legacy primary when declared, remaining <see cref="ServiceConfigAttribute"/> entries.</summary>
        [Id(6)] public List<MetaConfigVersion>? ConfigVersions { get; set; }

        /// <summary>
        /// Per-entity capability deltas for the subscribing player. Computed from this
        /// entity's resolved config version + the bound config's
        /// <c>[MetaConfigStructureBoundary]</c> declarations. Forwarded to the client via
        /// <c>SubscribeResponse.AugmentedCapabilities</c>.
        /// </summary>
        [Id(5)] public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }
    }
}
