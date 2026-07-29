using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core.Framework;

namespace SharedMeta.Orleans.Grains
{
    /// <summary>
    /// Result of a match request.
    /// </summary>
    [GenerateSerializer]
    public class LobbyMatchRequestResult
    {
        [Id(0)] public bool Success { get; set; }
        [Id(1)] public string? Error { get; set; }
        [Id(2)] public int QueuePosition { get; set; }
        [Id(3)] public int EstimatedWaitSeconds { get; set; }
    }

    /// <summary>
    /// A player waiting in the matchmaking queue.
    /// </summary>
    [GenerateSerializer]
    public class LobbyWaitingPlayer
    {
        [Id(0)] public string ProfileEntityId { get; set; } = "";
        [Id(1)] public string PlayerId { get; set; } = "";
        [Id(2)] public int PlayerCount { get; set; }
        [Id(3)] public string GameMode { get; set; } = "";
    }

    /// <summary>
    /// Lobby grain interface for matchmaking.
    /// Uses a singleton pattern - one lobby grain per game mode.
    /// </summary>
    public interface ILobbyGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Request to join matchmaking queue.
        /// </summary>
        /// <param name="profileEntityId">The profile entity ID of the requesting player.</param>
        /// <param name="playerId">The player's display ID.</param>
        /// <param name="playerCount">Number of players needed for the match.</param>
        /// <returns>Result with queue position and estimated wait time.</returns>
        Task<LobbyMatchRequestResult> RequestMatchAsync(string profileEntityId, string playerId, int playerCount);

        /// <summary>
        /// Cancel a pending match request.
        /// </summary>
        /// <param name="profileEntityId">The profile entity ID to remove from queue.</param>
        /// <returns>True if the player was removed from queue.</returns>
        Task<bool> CancelMatchAsync(string profileEntityId);

        /// <summary>
        /// Get current queue status.
        /// </summary>
        Task<int> GetQueueLengthAsync();
    }
}
