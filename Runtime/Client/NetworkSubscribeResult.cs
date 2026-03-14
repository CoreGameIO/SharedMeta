using SharedMeta.Core;
using SharedMeta.Core.Network;

namespace SharedMeta.Client
{
    /// <summary>
    /// Result of subscribing to an entity via the network factory.
    /// Contains the network adapter and initial state data.
    /// </summary>
    public class NetworkSubscribeResult
    {
        public INetwork Network { get; init; } = null!;
        public byte[]? StateBytes { get; init; }
        public byte[]? OptimisticRandomBytes { get; init; }
        public MetaConfigVersion ConfigVersion { get; init; }
    }
}
