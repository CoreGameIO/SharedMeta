using System;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response from the server after connecting/subscribing to an entity.
    /// Contains the initial state snapshot and current sequence number.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ConnectResponse
    {
        /// <summary>
        /// Serialized initial state.
        /// </summary>
        [Id(0), Key(0)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Current entity sequence number.
        /// Used to track the ordering of broadcasts for this entity.
        /// </summary>
        [Id(1), Key(1)] public long CurrentSequenceNumber { get; set; }

        /// <summary>
        /// Serialized optimistic random state for deterministic replay.
        /// </summary>
        [Id(2), Key(2)] public byte[]? OptimisticRandomBytes { get; set; }

        // 0.33.0+ Id/Key 3/4/6 (ConfigMajor/Minor/PatchVersion ints) retired — a service can now
        // declare multiple independently-versioned configs ([ServiceConfig]), so a single
        // Major/Minor/Patch triple can no longer represent "the" resolved version. Never reuse
        // ids 3/4/6. Replaced by ConfigVersions below: index 0 is the legacy primary ConfigType's
        // resolved version when declared (what ids 3/4/6 used to carry alone), remaining indices
        // are [ServiceConfig] entries in declaration order. MetaConfigVersion is already fully
        // wire-serializable, so no int-triple packing is needed for the new field.

        /// <summary>Serialized named-random states (packed positional list) for deterministic replay.</summary>
        [Id(5), Key(5)] public byte[]? NamedRandomsBytes { get; set; }

        /// <summary>
        /// 0.22.0+ Per-entity capability overlay returned by the server's subscribe path.
        /// Passes through to <c>DispatcherNetworkAdapter.EntityCapabilities</c> on the
        /// per-entity adapter so generated <c>*ApiClient</c> can consult both session-level
        /// and entity-level capabilities at the gate.
        /// </summary>
        [Id(7), Key(7)] public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }

        /// <summary>
        /// Resolved <see cref="MetaConfigVersion"/> for the entity's legacy primary config
        /// (index 0, when declared) and every <see cref="SharedMeta.Core.ServiceConfigAttribute"/>
        /// entry (remaining indices, declaration order). Null/empty means "no config system".
        /// </summary>
        [Id(8), Key(8)] public System.Collections.Generic.List<MetaConfigVersion>? ConfigVersions { get; set; }
    }

    /// <summary>
    /// Reason for transport disconnection.
    /// </summary>
    [GenerateSerializer]
    public enum TransportDisconnectReason
    {
        /// <summary>Client requested disconnect.</summary>
        ClientRequested,

        /// <summary>Server closed the connection.</summary>
        ServerDisconnect,

        /// <summary>Network error or timeout.</summary>
        NetworkError,

        /// <summary>Unknown reason.</summary>
        Unknown
    }
}
