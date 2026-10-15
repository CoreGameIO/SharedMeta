using System.Collections.Generic;
using System.Threading.Tasks;

namespace SharedMeta.Client
{
    /// <summary>
    /// 0.24.0+ Game-level callback invoked when the server reports the session is lost
    /// (<see cref="SharedMeta.Core.Transport.SessionConnectFailureReason.SessionUnknown"/>
    /// on a Resume attempt). Game code decides the recovery action — there is no implicit
    /// behaviour past dispatcher-default "reconnect with new sessionId and re-subscribe".
    /// <para>
    /// Typical causes for session loss: server restart without persistent session state,
    /// idle-timeout grain deactivation outside the persistence window, sessionId pruned
    /// from the server's known set. Pending in-flight RPCs at the moment of loss are
    /// automatically failed with <see cref="SessionLostException"/> before this handler
    /// is called — game logic decides whether to retry, replay, or drop them.
    /// </para>
    /// <para>
    /// Register via <c>MetaClientOptions.SessionRecoveryHandler</c>. If left null,
    /// <see cref="DefaultSessionRecoveryHandler"/> is used (returns
    /// <see cref="SessionRecoveryAction.Reconnect"/> — the safe default).
    /// </para>
    /// </summary>
    public interface IMetaSessionRecoveryHandler
    {
        /// <summary>
        /// Called when the server signaled that the previous session is gone. Returns the
        /// action the dispatcher should take next. Implementations may inspect
        /// <see cref="SessionLostInfo.KnownEntityIds"/> to decide whether a partial
        /// reconnect is feasible or a full restart is cleaner for the game.
        /// </summary>
        Task<SessionRecoveryAction> OnSessionLostAsync(SessionLostInfo info);
    }

    /// <summary>
    /// 0.24.0+ Action the dispatcher takes after <c>OnSessionLostAsync</c> returns.
    /// </summary>
    public enum SessionRecoveryAction
    {
        /// <summary>
        /// Default: dispatcher issues a fresh <c>StartNew</c> SessionConnect, then walks
        /// the in-memory <c>_subscribedEntities</c> map and re-subscribes to each entity
        /// on the new session. Game continues with fresh per-entity state delivered by
        /// the re-subscribe responses; local optimistic mutations that didn't reach the
        /// server are dropped (pending RPCs already failed with <see cref="SessionLostException"/>).
        /// </summary>
        Reconnect = 0,

        /// <summary>
        /// Full disconnect + reconnect cycle. Dispatcher calls
        /// <c>_connection.DisconnectAsync()</c> followed by <c>_connection.ConnectAsync()</c>,
        /// effectively returning to the app's cold-start path. Game must drive subscribe
        /// flow from scratch (typically via its startup wiring).
        /// </summary>
        Restart = 1,

        /// <summary>
        /// Dispatcher tears down the transport and stops automatic recovery. Game shows
        /// a "session lost" UI; further reconnect attempts are user-initiated via
        /// <c>MetaClient.ConnectAsync()</c>.
        /// </summary>
        Disconnect = 2,
    }

    /// <summary>
    /// 0.24.0+ Context passed to <see cref="IMetaSessionRecoveryHandler.OnSessionLostAsync"/>.
    /// </summary>
    public sealed class SessionLostInfo
    {
        /// <summary>The sessionId the client was trying to resume.</summary>
        public System.Guid OldSessionId { get; init; }

        /// <summary>
        /// EntityIds the dispatcher had subscribed to before the loss (snapshot of
        /// <c>_subscribedEntities.Keys</c>). The Reconnect action will iterate and
        /// re-subscribe to each one; Restart hands the list to the game for full
        /// re-init; Disconnect ignores it.
        /// </summary>
        public IReadOnlyList<string> KnownEntityIds { get; init; } = System.Array.Empty<string>();

        /// <summary>Server-supplied reason string (typically diagnostic, may be null).</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// 0.24.0+ Default handler — returns <see cref="SessionRecoveryAction.Reconnect"/>.
    /// Suits games where any lost session is recoverable by re-subscribing to entities
    /// the player was looking at; no game-specific UI needed.
    /// </summary>
    public sealed class DefaultSessionRecoveryHandler : IMetaSessionRecoveryHandler
    {
        public Task<SessionRecoveryAction> OnSessionLostAsync(SessionLostInfo info)
            => Task.FromResult(SessionRecoveryAction.Reconnect);
    }

    /// <summary>
    /// 0.24.0+ Thrown by failed pending RPCs at the moment the dispatcher detects session loss.
    /// Catch on the game side if you want explicit retry / drop logic per call site.
    /// </summary>
    public sealed class SessionLostException : System.Exception
    {
        public SessionLostException(string message) : base(message) { }
    }
}
