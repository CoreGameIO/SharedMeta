using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Session connection result from server.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class SessionConnectResult
    {
        /// <summary>Whether connection was accepted.</summary>
        [Id(0), Key(0)] public bool Success { get; set; }

        /// <summary>Error message if connection failed.</summary>
        [Id(1), Key(1)] public string? Error { get; set; }

        /// <summary>The current session ID.</summary>
        [Id(2), Key(2)] public Guid SessionId { get; set; }

        /// <summary>Whether this is a new session (vs resuming).</summary>
        [Id(3), Key(3)] public bool IsNewSession { get; set; }

        /// <summary>Missed responses since last acknowledged sequence.</summary>
        [Id(4), Key(4)] public List<SessionResponse> MissedPackets { get; set; } = new();

        /// <summary>Current server UTC ticks for clock synchronization.</summary>
        [Id(5), Key(5)] public long ServerTimeTicks { get; set; }

        /// <summary>Entities re-subscribed by server after transport disconnect recovery.</summary>
        [Id(6), Key(6)] public List<ResubscribedEntityInfo>? ResubscribedEntities { get; set; }
    }

    /// <summary>
    /// Connection status for UI feedback.
    /// </summary>
    public enum ConnectionStatus
    {
        /// <summary>Connected and session active.</summary>
        Connected,
        /// <summary>Transport lost, attempting to reconnect.</summary>
        Reconnecting,
        /// <summary>Transport reconnected, re-establishing session.</summary>
        Reconnected,
        /// <summary>Connection lost (not reconnecting).</summary>
        Disconnected,
        /// <summary>Reconnection failed after all retries.</summary>
        Failed
    }

    /// <summary>
    /// Client-side dispatcher for multiplexing multiple entities over a single connection.
    /// Manages request/response correlation and broadcast routing.
    /// One dispatcher per player/client.
    /// </summary>
    public interface IClientDispatcher : IDisposable
    {
        /// <summary>
        /// Fired when connection status changes. Provides status and optional detail message.
        /// </summary>
        event Action<ConnectionStatus, string?>? OnConnectionStatusChanged;

        /// <summary>
        /// Fired when session is superseded by a new connection with the same player ID.
        /// The old client should clean up and optionally restart the session.
        /// </summary>
        event Action<string>? OnSessionSuperseded;
        /// <summary>
        /// The underlying connection.
        /// </summary>
        IConnection Connection { get; }

        /// <summary>
        /// Subscribe to an entity and receive initial state.
        /// Auto-creates the entity on server if it doesn't exist.
        /// </summary>
        /// <param name="entityId">Entity to subscribe to</param>
        /// <param name="stateTypeName">State type name for auto-creation</param>
        /// <returns>Connection response with initial state</returns>
        Task<ConnectResponse> SubscribeAsync(string entityId, string? stateTypeName = null);

        /// <summary>
        /// Unsubscribe from an entity.
        /// </summary>
        Task UnsubscribeAsync(string entityId);

        /// <summary>
        /// Send an RPC call to an entity and receive response.
        /// Returns the single RPC result operation. Broadcasts are separated
        /// and delivered internally via OnBroadcast handlers.
        /// </summary>
        /// <param name="entityId">Target entity</param>
        /// <param name="call">The RPC call</param>
        /// <param name="stateTypeName">State type name (required for Orleans routing)</param>
        /// <returns>The RPC result operation</returns>
        Task<SessionOp> SendAsync(string entityId, RpcCall call, string? stateTypeName = null);

        /// <summary>
        /// Register a handler for broadcasts from a specific entity.
        /// </summary>
        /// <param name="entityId">Entity to listen to</param>
        /// <param name="handler">Handler for broadcast operations</param>
        /// <returns>Subscription that can be disposed to unsubscribe</returns>
        IDisposable OnBroadcast(string entityId, Action<SessionOp> handler);

        /// <summary>
        /// Get missing broadcasts for gap recovery.
        /// </summary>
        /// <param name="entityId">Entity ID</param>
        /// <param name="fromSequence">Start sequence (exclusive)</param>
        /// <returns>List of missing broadcasts</returns>
        Task<List<RpcCall>?> GetMissingBroadcastsAsync(string entityId, long fromSequence);

        /// <summary>
        /// Check if subscribed to an entity.
        /// </summary>
        bool IsSubscribed(string entityId);

        /// <summary>
        /// Player ID for this session. Must be set before calling ConnectSessionAsync.
        /// </summary>
        string? PlayerId { get; set; }

        /// <summary>
        /// True if session has been established with server.
        /// </summary>
        bool IsSessionConnected { get; }

        /// <summary>
        /// Current session ID.
        /// </summary>
        Guid SessionId { get; }

        /// <summary>
        /// Current client-side request ID counter.
        /// </summary>
        long CurrentRequestId { get; }

        /// <summary>
        /// Last acknowledged sequence number.
        /// </summary>
        long LastAcknowledgedSequence { get; }

        /// <summary>
        /// Connect/reconnect with session ID.
        /// If resuming an existing session, returns missed packets.
        /// </summary>
        /// <param name="sessionId">Session ID (Guid.NewGuid() for new session).</param>
        /// <param name="lastAcknowledgedSequence">Last sequence the client received (0 for new).</param>
        /// <returns>Connection result with missed packets if resuming.</returns>
        Task<SessionConnectResult> ConnectSessionAsync(Guid sessionId, long lastAcknowledgedSequence);

        /// <summary>
        /// Acknowledge that all broadcasts up to this sequence have been processed.
        /// Allows server to clean up stored packets.
        /// </summary>
        /// <param name="sequenceNumber">Highest processed sequence number.</param>
        Task AcknowledgeSequenceAsync(long sequenceNumber);

        /// <summary>
        /// When true, broadcasts are processed immediately as they arrive (from transport thread).
        /// When false (default), broadcasts are queued and must be drained by calling ProcessPendingBroadcasts().
        /// Set to true for console apps/tests. Leave false for game engines (Unity, etc.).
        /// </summary>
        bool ImmediateMode { get; set; }

        /// <summary>
        /// Process all queued broadcasts. Call this from the game loop when ImmediateMode is false.
        /// Returns the number of broadcasts processed.
        /// </summary>
        int ProcessPendingBroadcasts();

        /// <summary>
        /// Suppress broadcast processing. Reentrant (reference counted).
        /// While suppressed, ProcessPendingBroadcasts() returns immediately.
        /// </summary>
        void SuppressBroadcasts();

        /// <summary>
        /// Resume broadcast processing. When the last suppress is released,
        /// pending broadcasts are drained immediately.
        /// </summary>
        void ResumeBroadcasts();
    }
}
