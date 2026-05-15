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

        /// <summary>Config major version (schema). 0 = no config.</summary>
        [Id(3), Key(3)] public int ConfigMajorVersion { get; set; }

        /// <summary>Config minor version (data). 0 = no config.</summary>
        [Id(4), Key(4)] public int ConfigMinorVersion { get; set; }

        /// <summary>Serialized named-random states (packed positional list) for deterministic replay.</summary>
        [Id(5), Key(5)] public byte[]? NamedRandomsBytes { get; set; }

        /// <summary>Config patch version (content). 0 = no patch versioning / pre-0.19.0 entity.</summary>
        [Id(6), Key(6)] public int ConfigPatchVersion { get; set; }

        /// <summary>
        /// 0.22.0+ Per-entity capability overlay returned by the server's subscribe path.
        /// Passes through to <c>DispatcherNetworkAdapter.EntityCapabilities</c> on the
        /// per-entity adapter so generated <c>*ApiClient</c> can consult both session-level
        /// and entity-level capabilities at the gate.
        /// </summary>
        [Id(7), Key(7)] public EntityAugmentedCapabilities? AugmentedCapabilities { get; set; }
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
