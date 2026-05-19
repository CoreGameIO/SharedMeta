using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Result of subscribing to an entity.
    /// </summary>
    [GenerateSerializer, Immutable]
    public class EntitySubscriptionResult
    {
        [Id(0)] public bool Success { get; set; }
        [Id(1)] public string? Error { get; set; }
        [Id(2)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        [Id(3)] public long EntitySequenceNumber { get; set; }
        [Id(4)] public byte[]? OptimisticRandomBytes { get; set; }
        [Id(5)] public MetaConfigVersion ConfigVersion { get; set; }
        [Id(6)] public byte[]? NamedRandomsBytes { get; set; }
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
