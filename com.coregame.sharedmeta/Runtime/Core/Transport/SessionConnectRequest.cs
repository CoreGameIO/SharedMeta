using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to establish or resume a session with the server.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SessionConnectRequest
    {
        /// <summary>Player identifier for this session.</summary>
        [Id(0), Key(0)] public string PlayerId { get; set; } = "";

        /// <summary>
        /// Session ID for resuming an existing session.
        /// Null for new sessions.
        /// </summary>
        [Id(1), Key(1)] public Guid? SessionId { get; set; }

        /// <summary>
        /// Last acknowledged sequence number from previous session.
        /// Server will resend any missed packets after this sequence.
        /// </summary>
        [Id(2), Key(2)] public long LastAcknowledgedSequence { get; set; }

        /// <summary>
        /// Client's method signature hashes for validation.
        /// Key: "ServiceName.MethodAlias", Value: FNV-1a hash of signature.
        /// Use GameServiceDiscoveryBase.GetMethodSignatures() to populate.
        /// </summary>
        [Id(3), Key(3)] public Dictionary<string, ulong>? MethodSignatures { get; set; }

        /// <summary>
        /// Client's application version in "major.minor.patch" format (e.g. "1.2.3").
        /// Used by the server to enforce minimum version compatibility.
        /// Null if the client does not send version information.
        /// </summary>
        [Id(4), Key(4)] public string? ClientVersion { get; set; }
    }
}
