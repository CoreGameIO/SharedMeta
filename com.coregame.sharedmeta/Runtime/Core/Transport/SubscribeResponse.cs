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
    }
}
