using Microsoft.Extensions.Logging;

namespace SharedMeta.Orleans;

internal static partial class Log
{
    [LoggerMessage(EventId = 5001, EventName = "LobbyActivated", Level = LogLevel.Information, Message = "Activated for game mode: {GameMode}")]
    public static partial void LobbyActivated(this ILogger logger, string gameMode);

    [LoggerMessage(EventId = 5002, EventName = "PlayerJoinedQueue", Level = LogLevel.Information, Message = "{PlayerId} (entity={ProfileEntityId}) joined queue for {GameMode} ({WaitingCount} waiting, need {PlayerCount})")]
    public static partial void PlayerJoinedQueue(this ILogger logger, string playerId, string profileEntityId, string gameMode, int waitingCount, int playerCount);

    [LoggerMessage(EventId = 5003, EventName = "PlayerLeftQueue", Level = LogLevel.Information, Message = "{PlayerId} left queue for {GameMode}")]
    public static partial void PlayerLeftQueue(this ILogger logger, string playerId, string gameMode);

    [LoggerMessage(EventId = 5004, EventName = "TryFormMatch", Level = LogLevel.Debug, Message = "TryFormMatch: {WaitingCount} total waiting in {GameMode}")]
    public static partial void TryFormMatch(this ILogger logger, int waitingCount, string gameMode);

    [LoggerMessage(EventId = 5005, EventName = "MatchGroup", Level = LogLevel.Debug, Message = "Group playerCount={RequiredPlayers}: {Available} available")]
    public static partial void MatchGroup(this ILogger logger, int requiredPlayers, int available);

    [LoggerMessage(EventId = 5006, EventName = "MatchFormed", Level = LogLevel.Information, Message = "Match formed: {MatchId} with {PlayerCount} players")]
    public static partial void MatchFormed(this ILogger logger, string matchId, int playerCount);

    [LoggerMessage(EventId = 5007, EventName = "PlayerNotified", Level = LogLevel.Debug, Message = "Notified {ProfileEntityId} of {EventName}")]
    public static partial void PlayerNotified(this ILogger logger, string profileEntityId, string eventName);

    [LoggerMessage(EventId = 5008, EventName = "ErrorNotifyingPlayer", Level = LogLevel.Error, Message = "Error notifying {ProfileEntityId} of {EventName}")]
    public static partial void ErrorNotifyingPlayer(this ILogger logger, Exception ex, string profileEntityId, string eventName);

    [LoggerMessage(EventId = 5009, EventName = "LobbyListenerMissing", Level = LogLevel.Warning,
        Message = "No service inherits ILobbyListener — dropping {EventName} for {ProfileEntityId}. Add it to a [MetaService] interface's base list so the generator binds it.")]
    public static partial void LobbyListenerMissing(this ILogger logger, string profileEntityId, string eventName);
}
