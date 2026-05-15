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
        [Id(2)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        [Id(3)] public long EntitySequenceNumber { get; set; }
        [Id(4)] public byte[]? OptimisticRandomBytes { get; set; }
        [Id(5)] public MetaConfigVersion ConfigVersion { get; set; }
        [Id(6)] public byte[]? NamedRandomsBytes { get; set; }
        /// <summary>
        /// 0.22.0+: structured rejection details when Success=false and the failure was a
        /// version-compatibility mismatch (Breaking schema gate, RejectedMethods entry, etc.).
        /// Populated by SessionManagerGrain when EntityGrain throws <c>IncompatibleFeatureException</c>;
        /// propagated to <c>SubscribeResponse.FeatureRequirement</c> by MetaConnectionHandler.
        /// </summary>
        [Id(7)] public FeatureRequirement? FeatureRequirement { get; set; }

        /// <summary>
        /// 0.22.0+ Per-entity capability deltas (RejectedServices / ForceServerPatchServices).
        /// EntityGrain computes these from its resolved config version + the entity's bound
        /// config's <c>[MetaConfigStructureBoundary]</c> entries. SessionManagerGrain caches
        /// them by entityId for broadcast-fan-out tailoring, and the value is forwarded to the
        /// client via <c>SubscribeResponse.AugmentedCapabilities</c>.
        /// </summary>
        [Id(8)] public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }
    }
}
