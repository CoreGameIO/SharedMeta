using Microsoft.Extensions.Logging;

namespace SharedMeta.Debug;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulating disconnect for {ConnectionId}")]
    public static partial void SimulatingDisconnect(this ILogger logger, string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropping broadcast for {ConnectionId}")]
    public static partial void DroppingBroadcast(this ILogger logger, string connectionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Entity deactivating: {EntityId}")]
    public static partial void EntityDeactivating(this ILogger logger, string entityId);
}
