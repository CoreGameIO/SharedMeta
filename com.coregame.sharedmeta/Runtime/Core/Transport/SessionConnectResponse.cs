using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response to session connection request.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SessionConnectResponse
    {
        /// <summary>True if connection was successful.</summary>
        [Id(0), Key(0)] public bool Success { get; set; }

        /// <summary>Error message if connection failed.</summary>
        [Id(1), Key(1)] public string? Error { get; set; }

        /// <summary>Assigned session ID.</summary>
        [Id(2), Key(2)] public Guid SessionId { get; set; }

        /// <summary>True if this is a new session, false if resuming.</summary>
        [Id(3), Key(3)] public bool IsNewSession { get; set; }

        /// <summary>
        /// Responses missed during disconnect (for session resume).
        /// Empty for new sessions. Each response has its own SequenceNumber.
        /// </summary>
        [Id(4), Key(4)] public List<SessionResponse> MissedPackets { get; set; } = new();

        /// <summary>
        /// List of method signature mismatches (null if all match).
        /// Populated when client sends MethodSignatures in request.
        /// </summary>
        [Id(5), Key(5)] public List<string>? SignatureMismatches { get; set; }

        /// <summary>
        /// Current server UTC ticks for initial clock synchronization.
        /// </summary>
        [Id(6), Key(6)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// Entities re-subscribed by server after transport disconnect recovery.
        /// Contains fresh state for each entity the client was subscribed to before disconnect.
        /// </summary>
        [Id(7), Key(7)] public List<ResubscribedEntityInfo>? ResubscribedEntities { get; set; }
    }

    /// <summary>
    /// Info about an entity re-subscribed during session reconnect (transport DTO).
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ResubscribedEntityInfo
    {
        [Id(0), Key(0)] public string EntityId { get; set; } = "";
        [Id(1), Key(1)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        [Id(2), Key(2)] public long EntitySequenceNumber { get; set; }
        [Id(3), Key(3)] public byte[]? OptimisticRandomBytes { get; set; }
        [Id(4), Key(4)] public int ConfigMajorVersion { get; set; }
        [Id(5), Key(5)] public int ConfigMinorVersion { get; set; }
        [Id(6), Key(6)] public byte[]? NamedRandomsBytes { get; set; }
    }
}
