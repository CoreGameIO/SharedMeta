using Microsoft.Extensions.Logging;

namespace SharedMeta.Orleans;

internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Activated for game mode: {GameMode}")]
    public static partial void LobbyActivated(this ILogger logger, string gameMode);

    [LoggerMessage(Level = LogLevel.Information, Message = "{PlayerId} (entity={ProfileEntityId}) joined queue for {GameMode} ({WaitingCount} waiting, need {PlayerCount})")]
    public static partial void PlayerJoinedQueue(this ILogger logger, string playerId, string profileEntityId, string gameMode, int waitingCount, int playerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "{PlayerId} left queue for {GameMode}")]
    public static partial void PlayerLeftQueue(this ILogger logger, string playerId, string gameMode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "TryFormMatch: {WaitingCount} total waiting in {GameMode}")]
    public static partial void TryFormMatch(this ILogger logger, int waitingCount, string gameMode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Group playerCount={RequiredPlayers}: {Available} available")]
    public static partial void MatchGroup(this ILogger logger, int requiredPlayers, int available);

    [LoggerMessage(Level = LogLevel.Information, Message = "Match formed: {MatchId} with {PlayerCount} players")]
    public static partial void MatchFormed(this ILogger logger, string matchId, int playerCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "State type '{StateTypeName}' not found in resolver")]
    public static partial void StateTypeNotFound(this ILogger logger, string stateTypeName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notified {ProfileEntityId}.{MethodName} via grain")]
    public static partial void PlayerNotified(this ILogger logger, string profileEntityId, string methodName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error notifying player {ProfileEntityId}.{MethodName}")]
    public static partial void ErrorNotifyingPlayer(this ILogger logger, Exception ex, string profileEntityId, string methodName);
}
