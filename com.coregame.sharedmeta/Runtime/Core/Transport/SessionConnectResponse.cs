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

        /// <summary>
        /// Server's current version. Populated when version checking is configured.
        /// Useful for displaying upgrade prompts on the client.
        /// </summary>
        [Id(8), Key(8)] public string? ServerVersion { get; set; }

        /// <summary>
        /// Minimum client version required by the server. Populated when the connection
        /// is rejected due to an incompatible client version.
        /// </summary>
        [Id(9), Key(9)] public string? MinClientVersion { get; set; }

        /// <summary>
        /// Maximum client version this server supports. Populated when the connection is
        /// rejected because the client is too new for this server — lets the client show
        /// "please wait for a server update" or switch to a compatible endpoint.
        /// </summary>
        [Id(10), Key(10)] public string? MaxClientVersion { get; set; }

        /// <summary>
        /// 0.22.0+: True when the server didn't find the client's
        /// <see cref="SessionConnectRequest.ClientSignatureHash"/> in its registry and
        /// needs a phase-2 follow-up. The client MUST send a
        /// <see cref="RegisterClientSignatureRequest"/> with the full signature before
        /// issuing any RPC. While this flag is set, <see cref="Capabilities"/> is null
        /// (or an empty default) and the client should treat the session as "not yet
        /// known to negotiate against."
        /// </summary>
        [Id(11), Key(11)] public bool NeedsSignatureRegistration { get; set; }

        /// <summary>
        /// 0.22.0+: Server's compatibility verdict for the client's declared signature.
        /// Null when negotiation is disabled or when
        /// <see cref="NeedsSignatureRegistration"/> is true (capabilities will arrive in
        /// the phase-2 <see cref="RegisterClientSignatureResponse"/>). Empty lists =
        /// no restrictions (the common case for an up-to-date client).
        /// </summary>
        [Id(12), Key(12)] public ClientCapabilities? Capabilities { get; set; }
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
        [Id(7), Key(7)] public int ConfigPatchVersion { get; set; }
    }
}
