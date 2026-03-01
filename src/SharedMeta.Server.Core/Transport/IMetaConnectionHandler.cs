using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Transport
{
    /// <summary>
    /// Interface for sending broadcasts back to the transport layer.
    /// Transport-agnostic - works with SignalR, WebSocket, or any other transport.
    /// </summary>
    public interface IBroadcastSender
    {
        /// <summary>
        /// Send a response/broadcast to the client.
        /// Uses unified SessionResponse for both direct responses and broadcasts.
        /// For broadcasts, SessionSequenceNumber = -1.
        /// </summary>
        void SendBroadcast(SessionResponse message);

        /// <summary>
        /// Notify client that session was terminated.
        /// </summary>
        void SendSessionTerminated(string reason);

        /// <summary>
        /// Notify client that an entity is deactivating.
        /// </summary>
        void SendEntityDeactivating(string entityId);

        /// <summary>
        /// Clear all queued data (broadcasts, termination, deactivations).
        /// Called when a new session is established on the same connection,
        /// to prevent stale data from the old session being delivered.
        /// </summary>
        void Reset() { }
    }

    /// <summary>
    /// Handler for meta operations on a single connection.
    /// Transport-agnostic - doesn't know about SignalR, WebSocket, etc.
    /// Knows about ISessionManager and Orleans grains.
    /// </summary>
    public interface IMetaConnectionHandler : IDisposable
    {
        /// <summary>
        /// Player ID for this connection (set after SessionConnect).
        /// </summary>
        string? PlayerId { get; }

        /// <summary>
        /// Session ID for this connection.
        /// </summary>
        Guid SessionId { get; }

        /// <summary>
        /// Whether session is connected.
        /// </summary>
        bool IsSessionConnected { get; }

        /// <summary>
        /// Connect to a session.
        /// </summary>
        Task<SessionConnectResponse> SessionConnectAsync(SessionConnectRequest request);

        /// <summary>
        /// Subscribe to an entity.
        /// </summary>
        Task<SubscribeResponse> SubscribeAsync(SubscribeRequest request);

        /// <summary>
        /// Unsubscribe from an entity.
        /// </summary>
        Task<UnsubscribeResponse> UnsubscribeAsync(UnsubscribeRequest request);

        /// <summary>
        /// Execute an RPC call.
        /// Returns unified SessionResponse with result and sequence numbers.
        /// </summary>
        Task<SessionResponse> RpcCallAsync(RpcCallRequest request);

        /// <summary>
        /// Acknowledge received broadcasts up to a sequence number.
        /// </summary>
        Task<AcknowledgeResponse> AcknowledgeSequenceAsync(AcknowledgeRequest request);

        /// <summary>
        /// Client explicitly leaves — full cleanup, session cannot be resumed.
        /// </summary>
        Task GracefulDisconnectAsync();

        /// <summary>
        /// Called when transport connection is closed (timeout/network loss).
        /// Saves subscriptions for potential reconnect.
        /// </summary>
        Task OnDisconnectedAsync();
    }

    /// <summary>
    /// Delegate for validating client method signatures.
    /// Returns null if all signatures match, or list of mismatches.
    /// </summary>
    /// <param name="clientSignatures">Client's method signature hashes.</param>
    /// <returns>List of mismatches, or null if all match.</returns>
    public delegate List<string>? SignatureValidator(Dictionary<string, ulong> clientSignatures);

    /// <summary>
    /// Factory for creating connection handlers.
    /// </summary>
    public interface IMetaConnectionHandlerFactory
    {
        /// <summary>
        /// Create a new handler for a connection.
        /// </summary>
        /// <param name="connectionId">Transport connection ID</param>
        /// <param name="broadcastSender">Interface for sending broadcasts back to transport</param>
        IMetaConnectionHandler Create(string connectionId, IBroadcastSender broadcastSender);
    }
}
