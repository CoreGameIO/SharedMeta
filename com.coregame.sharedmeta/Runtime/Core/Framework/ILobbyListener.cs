using System.Collections.Generic;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core.Framework
{
    /// <summary>
    /// Matchmaking callbacks the lobby invokes on a player's entity. Wire it up in two places:
    /// inherit it on the <c>[MetaService]</c> interface that owns the player's state (dispatch
    /// and the generated APIs are typed on that interface), and mark each implementing method
    /// <c>[MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]</c> on the
    /// <c>[MetaServiceImpl]</c> class. The attribute has to go on the implementation because the
    /// declarations here live in the framework assembly and carry no syntax in the game's
    /// compilation; putting it there is what gives each method a game method id, a dispatcher
    /// entry and client-side replay.
    /// </summary>
    /// <remarks>
    /// A plain contract, not a framework hook — nothing here is special-cased by the dispatcher.
    /// <c>[MetaServiceContract]</c> makes the generator emit <c>ILobbyListenerServerApi</c>
    /// beside it, which is how <c>LobbyGrain</c> reaches a player's entity without being able to
    /// name the game's service type.
    /// </remarks>
    [MetaServiceContract]
    public interface ILobbyListener
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
