using System;
using System.Collections.Generic;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Result of subscribing to an entity.
    /// </summary>
    [GenerateSerializer]
    public class EntitySubscriptionResult
    {
        [Id(0)] public bool Success { get; set; }
        [Id(1)] public string? Error { get; set; }
        [Id(2)] public ReadOnlyMemory<byte> StateBytes { get; set; }
        [Id(3)] public long EntitySequenceNumber { get; set; }
        [Id(4)] public byte[]? OptimisticRandomBytes { get; set; }
        // 0.33.0+ Id 5 (ConfigVersion, single MetaConfigVersion) retired — see ConfigVersions below.
        [Id(6)] public byte[]? NamedRandomsBytes { get; set; }

        /// <summary>Resolved config version(s) — index 0 legacy primary when declared, remaining <see cref="ServiceConfigAttribute"/> entries.</summary>
        [Id(9)] public List<MetaConfigVersion>? ConfigVersions { get; set; }
        /// <summary>
        /// Structured rejection details when Success=false and the failure is a
        /// version-compatibility mismatch (Breaking schema gate, RejectedMethods entry, etc.).
        /// Propagated to <c>SubscribeResponse.FeatureRequirement</c> on the wire.
        /// </summary>
        [Id(7)] public FeatureRequirement? FeatureRequirement { get; set; }

        /// <summary>
        /// Per-entity capability deltas (RejectedServices / ForceServerPatchServices) computed
        /// from this entity's resolved config version + its bound config's
        /// <c>[MetaConfigStructureBoundary]</c> entries. Forwarded to the client via
        /// <c>SubscribeResponse.AugmentedCapabilities</c>.
        /// </summary>
        [Id(8)] public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }
    }
}
