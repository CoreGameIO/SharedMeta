using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Snapshot of entity state.
    /// </summary>
    [GenerateSerializer, Immutable]
    public class EntitySnapshot
    {
        [Id(0)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
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
