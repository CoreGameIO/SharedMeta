using Orleans;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Result of a connection attempt to SessionManager.
    /// </summary>
    [GenerateSerializer]
    public class SessionConnectionResult
    {
        /// <summary>Whether the connection was accepted.</summary>
        [Id(0)] public bool Success { get; set; }

        /// <summary>Error message if connection failed.</summary>
        [Id(1)] public string? Error { get; set; }

        /// <summary>The current session ID (may differ from requested if new session created).</summary>
        [Id(2)] public Guid SessionId { get; set; }

        /// <summary>Missed responses since the last acknowledged sequence number.</summary>
        [Id(3)] public List<SessionResponse> MissedPackets { get; set; } = new();

        /// <summary>Whether this is a new session (vs resuming existing).</summary>
        [Id(4)] public bool IsNewSession { get; set; }

        /// <summary>Current server UTC ticks for clock synchronization.</summary>
        [Id(5)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// Entities re-subscribed after transport disconnect recovery.
        /// Contains full state for entities where the client may have missed updates.
        /// </summary>
        [Id(6)] public List<ResubscribedEntity>? ResubscribedEntities { get; set; }
    }

    /// <summary>
    /// Info about an entity that was re-subscribed during session reconnect.
    /// </summary>
    [GenerateSerializer]
    public class ResubscribedEntity
    {
        [Id(0)] public string EntityId { get; set; } = "";
        [Id(1)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        [Id(2)] public long EntitySequenceNumber { get; set; }
        [Id(3)] public byte[]? OptimisticRandomBytes { get; set; }
    }
}
