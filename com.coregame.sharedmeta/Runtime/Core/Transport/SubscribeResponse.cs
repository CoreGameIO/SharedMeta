using System;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response to entity subscription request.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SubscribeResponse
    {
        /// <summary>True if subscription was successful.</summary>
        [Id(0), Key(0)] public bool Success { get; set; }

        /// <summary>Error message if subscription failed.</summary>
        [Id(1), Key(1)] public string? Error { get; set; }

        /// <summary>Serialized current state of the entity.</summary>
        [Id(2), Key(2)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Current entity sequence number.
        /// Client uses this to initialize its operation ordering queue.
        /// </summary>
        [Id(3), Key(3)] public long EntitySequenceNumber { get; set; }

        /// <summary>Serialized optimistic random state for deterministic replay.</summary>
        [Id(4), Key(4)] public byte[]? OptimisticRandomBytes { get; set; }

        /// <summary>Config major version (schema). 0 = no config.</summary>
        [Id(5), Key(5)] public int ConfigMajorVersion { get; set; }

        /// <summary>Config minor version (data). 0 = no config.</summary>
        [Id(6), Key(6)] public int ConfigMinorVersion { get; set; }

        /// <summary>Serialized named-random states (packed positional list) for deterministic replay.</summary>
        [Id(7), Key(7)] public byte[]? NamedRandomsBytes { get; set; }

        /// <summary>Config patch version (content). 0 = no patch versioning / pre-0.19.0 entity.</summary>
        [Id(8), Key(8)] public int ConfigPatchVersion { get; set; }

        /// <summary>
        /// 0.22.0+: structured rejection payload — populated when subscribe failed because
        /// the client lacks support for a feature (state-schema breaking change, missing
        /// method version, config structure boundary). When non-null, <see cref="Success"/>
        /// is false and <see cref="Error"/> carries the human-readable form; the client
        /// framework rethrows <see cref="IncompatibleFeatureException"/> instead of a
        /// generic exception so game UI can render a "feature requires update" notification.
        /// </summary>
        [Id(9), Key(9)] public FeatureRequirement? FeatureRequirement { get; set; }

        /// <summary>
        /// 0.22.0+ Per-entity capability deltas, computed by <c>EntityGrain</c> from this entity's
        /// resolved config version + <c>[MetaConfigStructureBoundary]</c> declarations. Stacks on
        /// top of session-level <see cref="ClientSignatureAnnotated"/> at the gate / dispatch / broadcast
        /// fan-out layer. Null when negotiation is disabled / no per-entity deltas apply.
        /// </summary>
        [Id(10), Key(10)] public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }
    }
}
