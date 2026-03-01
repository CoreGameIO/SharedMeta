using Microsoft.Extensions.Logging;

namespace SharedMeta.Transport.SignalR;

internal static partial class Log
{
    // ── MetaHub ──────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "SessionConnect START - ConnectionId={ConnectionId}, PlayerId={PlayerId}")]
    public static partial void SessionConnectStart(this ILogger logger, string connectionId, string? playerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SessionConnect handler returned: Success={Success}, SessionId={SessionId}")]
    public static partial void SessionConnectResult(this ILogger logger, bool success, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "SessionConnect exception")]
    public static partial void SessionConnectError(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected: {ConnectionId}")]
    public static partial void HubConnected(this ILogger logger, string connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnected: {ConnectionId} (player={PlayerId})")]
    public static partial void HubDisconnected(this ILogger logger, string connectionId, string? playerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Disconnected: {ConnectionId} (no handler)")]
    public static partial void HubDisconnectedNoHandler(this ILogger logger, string connectionId);

    // ── SignalRBroadcastSender ────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "SendBroadcast: opsCount={OpsCount}")]
    public static partial void SendBroadcast(this ILogger logger, int opsCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error sending broadcast")]
    public static partial void ErrorSendingBroadcast(this ILogger logger, Exception ex);
}
