using System.Collections.Generic;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core.Framework
{
    /// <summary>
    /// Subscriber interface for Lobby service events.
    /// Meta services implement this to receive matchmaking notifications.
    /// </summary>
    public interface ILobbySubscriber : IFrameworkServiceSubscriber
    {
        /// <summary>
        /// Called when a match has been found for the player.
        /// </summary>
        void OnMatchFound(MatchFoundEvent @event);

        /// <summary>
        /// Called when matchmaking was cancelled (by player or timeout).
        /// </summary>
        void OnMatchCancelled(MatchCancelledEvent @event);

        /// <summary>
        /// Called when matchmaking status changes (players joined/left queue).
        /// </summary>
        void OnMatchmakingUpdate(MatchmakingUpdateEvent @event);
    }

    /// <summary>
    /// Event sent when a match has been found.
    /// </summary>
    [MemoryPackable, MessagePackObject]
    [GenerateSerializer]
    public partial class MatchFoundEvent
    {
        /// <summary>
        /// Unique identifier for the match/game session.
        /// </summary>
        [Id(0), Key(0)] public string MatchId { get; set; } = "";

        /// <summary>
        /// List of player IDs in the match.
        /// </summary>
        [Id(1), Key(1)] public List<string> PlayerIds { get; set; } = new();

        /// <summary>
        /// The game mode/type requested.
        /// </summary>
        [Id(2), Key(2)] public string GameMode { get; set; } = "";

        /// <summary>
        /// Server-assigned slot/position for this player.
        /// </summary>
        [Id(3), Key(3)] public int PlayerSlot { get; set; }
    }

    /// <summary>
    /// Event sent when matchmaking is cancelled.
    /// </summary>
    [MemoryPackable, MessagePackObject]
    [GenerateSerializer]
    public partial class MatchCancelledEvent
    {
        /// <summary>
        /// Reason for cancellation.
        /// </summary>
        [Id(0), Key(0)] public MatchCancelReason Reason { get; set; }

        /// <summary>
        /// Optional message with details.
        /// </summary>
        [Id(1), Key(1)] public string? Message { get; set; }
    }

    /// <summary>
    /// Reasons for matchmaking cancellation.
    /// </summary>
    [GenerateSerializer]
    public enum MatchCancelReason
    {
        /// <summary>Player requested cancellation.</summary>
        PlayerCancelled,
        /// <summary>Matchmaking timed out.</summary>
        Timeout,
        /// <summary>Server error occurred.</summary>
        ServerError,
        /// <summary>Not enough players found.</summary>
        InsufficientPlayers
    }

    /// <summary>
    /// Event sent when matchmaking status updates.
    /// </summary>
    [MemoryPackable, MessagePackObject]
    [GenerateSerializer]
    public partial class MatchmakingUpdateEvent
    {
        /// <summary>
        /// Current number of players in queue for this game mode.
        /// </summary>
        [Id(0), Key(0)] public int PlayersInQueue { get; set; }

        /// <summary>
        /// Estimated wait time in seconds (0 = unknown).
        /// </summary>
        [Id(1), Key(1)] public int EstimatedWaitSeconds { get; set; }

        /// <summary>
        /// Current matchmaking status.
        /// </summary>
        [Id(2), Key(2)] public MatchmakingStatus Status { get; set; }
    }

    /// <summary>
    /// Matchmaking status values.
    /// </summary>
    [GenerateSerializer]
    public enum MatchmakingStatus
    {
        /// <summary>Not in matchmaking.</summary>
        None,
        /// <summary>Searching for match.</summary>
        Searching,
        /// <summary>Match found, waiting for confirmation.</summary>
        Found,
        /// <summary>Match confirmed, ready to start.</summary>
        Ready,
        /// <summary>Matchmaking cancelled.</summary>
        Cancelled
    }
}
