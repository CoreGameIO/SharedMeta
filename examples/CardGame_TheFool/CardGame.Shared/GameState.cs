using MemoryPack;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Framework;

namespace CardGame.Shared
{
    [GenerateSerializer]
    public enum GamePhase
    {
        NotStarted,
        Attacking,
        Defending,
        GameOver
    }

    /// <summary>
    /// A pair of cards on the table: an attack card and optionally a defense card that beats it.
    /// </summary>
    [MemoryPackable, GenerateSerializer]
    public partial class TablePair
    {
        [MemoryPackOrder(0), Id(0)] public Card AttackCard { get; set; } = null!;
        [MemoryPackOrder(1), Id(1)] public Card? DefenseCard { get; set; }
    }

    [MemoryPackable, GenerateSerializer]
    public partial class GameState : ISharedState
    {
        [MemoryPackOrder(0), Id(0)] public List<Card> Deck { get; set; } = new();
        [MemoryPackOrder(1), Id(1)] public List<List<Card>> PlayerHands { get; set; } = new();
        [MemoryPackOrder(2), Id(2)] public List<Player> Players { get; set; } = new();
        [MemoryPackOrder(3), Id(3)] public Card? TrumpCard { get; set; }
        [MemoryPackOrder(4), Id(4)] public Suit TrumpSuit { get; set; }
        [MemoryPackOrder(5), Id(5)] public bool GameStarted { get; set; }
        [MemoryPackOrder(6), Id(6)] public int? CurrentAttackerId { get; set; }
        [MemoryPackOrder(7), Id(7)] public int? CurrentDefenderId { get; set; }

        /// <summary>Current game phase.</summary>
        [MemoryPackOrder(8), Id(8)] public GamePhase Phase { get; set; } = GamePhase.NotStarted;

        /// <summary>Cards currently on the table (attack/defense pairs).</summary>
        [MemoryPackOrder(9), Id(9)] public List<TablePair> Table { get; set; } = new();

        /// <summary>Player IDs who exited the game (hand empty when deck was empty).</summary>
        [MemoryPackOrder(10), Id(10)] public List<int> Winners { get; set; } = new();

        /// <summary>Max cards per hand (refill target).</summary>
        [MemoryPackOrder(11), Id(11)] public int MaxHandSize { get; set; } = 6;

        /// <summary>
        /// Maps client connection ID to player ID.
        /// Used to identify which player is making a request.
        /// </summary>
        [MemoryPackOrder(12), Id(12)] public Dictionary<string, int> ClientToPlayer { get; set; } = new();

        /// <summary>
        /// Maps player ID to profile entity ID.
        /// Used to notify profiles about game results.
        /// </summary>
        [MemoryPackOrder(13), Id(13)] public Dictionary<int, string> PlayerToProfileEntity { get; set; } = new();

        /// <summary>
        /// Game entity ID (for result notifications).
        /// </summary>
        [MemoryPackOrder(14), Id(14)] public string GameEntityId { get; set; } = "";

        /// <summary>
        /// Player ID of the loser (the fool). Null if game not over or draw.
        /// </summary>
        [MemoryPackOrder(15), Id(15)] public int? LoserId { get; set; }
    }

    /// <summary>
    /// Represents a player in the game.
    /// </summary>
    [MemoryPackable, GenerateSerializer]
    public partial class Player
    {
        [MemoryPackOrder(0), Id(0)] public int Id { get; set; }
        [MemoryPackOrder(1), Id(1)] public string Name { get; set; } = "";

        [MemoryPackIgnore]
        public List<Card> Hand => _state?.PlayerHands.ElementAtOrDefault(Id) ?? new();

        [MemoryPackIgnore]
        [NonSerialized]
        private GameState? _state;

        public void SetState(GameState state) => _state = state;
    }

    /// <summary>
    /// Transforms Player objects to/from int IDs for network serialization.
    /// </summary>
    [Transformer]
    public class PlayerTransformer : IStateArgumentTransformer<Player, int, GameState>
    {
        public int Box(Player value, GameState state) => value.Id;

        public Player Unbox(int value, GameState state)
        {
            var player = state.Players.FirstOrDefault(p => p.Id == value)
                ?? throw new InvalidOperationException($"Player with ID {value} not found");
            player.SetState(state);
            return player;
        }
    }
}
