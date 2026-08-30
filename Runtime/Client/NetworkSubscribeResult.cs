using System.Collections.Generic;
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
        public byte[]? NamedRandomsBytes { get; init; }

        /// <summary>
        /// Resolved config version(s) — index 0 is the entity's legacy primary config when
        /// declared, remaining indices are <see cref="ServiceConfigAttribute"/> entries in
        /// declaration order. 0.33.0+ (was a single scalar <c>ConfigVersion</c> pre-0.33).
        /// </summary>
        public List<MetaConfigVersion>? ConfigVersions { get; init; }
    }
}
