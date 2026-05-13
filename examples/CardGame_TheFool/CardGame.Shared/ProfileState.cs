using MemoryPack;
using Orleans;
using SharedMeta.Core;

namespace CardGame.Shared
{
    /// <summary>
    /// Player profile state.
    /// Each player has their own profile entity with this state.
    /// </summary>
    [MemoryPackable, GenerateSerializer]
    [SharedState]
    public partial class ProfileState : ISharedState
    {
        /// <summary>
        /// Player's unique identifier (same as entity ID).
        /// </summary>
        [MemoryPackOrder(0), Id(0)] public string PlayerId { get; set; } = "";

        /// <summary>
        /// Player's display name.
        /// </summary>
        [MemoryPackOrder(1), Id(1)] public string DisplayName { get; set; } = "";

        /// <summary>
        /// Total number of wins.
        /// </summary>
        [MemoryPackOrder(2), Id(2)] public int Wins { get; set; }

        /// <summary>
        /// Total number of losses.
        /// </summary>
        [MemoryPackOrder(3), Id(3)] public int Losses { get; set; }

        /// <summary>
        /// Total games played.
        /// </summary>
        [MemoryPackOrder(4), Id(4)] public int GamesPlayed { get; set; }

        /// <summary>
        /// Total gifts received from other players.
        /// </summary>
        [MemoryPackOrder(5), Id(5)] public int GiftsReceived { get; set; }

        /// <summary>
        /// Current game entity ID (null if not in game).
        /// </summary>
        [MemoryPackOrder(6), Id(6)] public string? CurrentGameId { get; set; }

        /// <summary>
        /// Whether player is currently searching for a match.
        /// </summary>
        [MemoryPackOrder(7), Id(7)] public bool IsSearching { get; set; }

        /// <summary>
        /// Game mode being searched for.
        /// </summary>
        [MemoryPackOrder(8), Id(8)] public string? SearchGameMode { get; set; }
    }
}
