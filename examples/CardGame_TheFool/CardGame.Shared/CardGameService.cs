using SharedMeta.Core;
using SharedMeta.Core.Framework;

namespace CardGame.Shared
{
    /// <summary>
    /// Callback for notifying player profiles about game results.
    /// Set by the server to allow cross-entity communication.
    /// </summary>
    public delegate void GameResultNotifier(string profileEntityId, string gameEntityId, GameResult result);

    [MetaServiceImpl(typeof(ICardGameService), typeof(GameState), typeof(IRandomService))]
    public partial class CardGameService : ICardGameService
    {
        /// <summary>
        /// Callback to notify player profiles about game results.
        /// Set by the server infrastructure.
        /// </summary>
        public GameResultNotifier? OnGameResult { get; set; }

        public void SetGameEntityId(string gameEntityId)
        {
            Context.State.GameEntityId = gameEntityId;
        }

        /// <summary>
        /// Get the current caller's player ID from Context.CallerId.
        /// Returns -1 if caller is not registered as a player.
        /// </summary>
        private int GetCallerPlayerId()
        {
            var callerId = Context.CallerId;
            if (string.IsNullOrEmpty(callerId)) return -1;

            var state = Context.State;
            if (state.ClientToPlayer.TryGetValue(callerId, out var playerId))
                return playerId;

            return -1;
        }

        /// <summary>
        /// Get the caller's Player object, or null if not registered.
        /// </summary>
        private Player? GetCallerPlayer()
        {
            var playerId = GetCallerPlayerId();
            if (playerId < 0) return null;

            var state = Context.State;
            var player = state.Players.FirstOrDefault(p => p.Id == playerId);
            player?.SetState(state);
            return player;
        }

        public void RegisterPlayer(string playerName, string profileEntityId)
        {
            var state = Context.State;
            var callerId = Context.CallerId;

            if (string.IsNullOrEmpty(callerId))
            {
                Console.WriteLine("[TheFool] RegisterPlayer: No CallerId in context");
                return;
            }

            // Already registered?
            if (state.ClientToPlayer.ContainsKey(callerId))
            {
                Console.WriteLine($"[TheFool] Client {callerId} already registered as player");
                return;
            }

            var playerId = state.Players.Count;
            state.Players.Add(new Player { Id = playerId, Name = playerName });
            state.PlayerHands.Add(new List<Card>());
            state.ClientToPlayer[callerId] = playerId;
            state.PlayerToProfileEntity[playerId] = profileEntityId;

            Console.WriteLine($"[TheFool] Registered {playerName} (ID={playerId}) for client {callerId}, profile={profileEntityId}");
        }

        public void NewGame(int cardsPerPlayer)
        {
            var state = Context.State;
            var playerCount = state.Players.Count;

            if (playerCount < 2)
            {
                Console.WriteLine("[TheFool] NewGame: Need at least 2 registered players");
                return;
            }

            state.Deck = CreateDeck();
            state.Table = new List<TablePair>();
            state.Winners = new List<int>();
            state.MaxHandSize = cardsPerPlayer;

            ShuffleDeck(state.Deck);

            // Reset and deal to existing registered players
            for (int i = 0; i < playerCount; i++)
            {
                var hand = state.PlayerHands[i];
                hand.Clear();
                for (int j = 0; j < cardsPerPlayer && state.Deck.Count > 0; j++)
                {
                    hand.Add(state.Deck[0]);
                    state.Deck.RemoveAt(0);
                }
            }

            // Trump: bottom card of remaining deck
            if (state.Deck.Count > 0)
            {
                state.TrumpCard = state.Deck[^1];
                state.TrumpSuit = state.TrumpCard.Suit;
            }

            state.CurrentAttackerId = 0;
            state.CurrentDefenderId = 1;
            state.Phase = GamePhase.Attacking;
            state.GameStarted = true;

            Console.WriteLine($"[TheFool] New game: {playerCount} players, {cardsPerPlayer} cards. Trump: {state.TrumpCard}");
        }

        public bool Attack(Card card)
        {
            var state = Context.State;
            var thrower = GetCallerPlayer();

            if (thrower == null)
                return false;

            // Validation
            if (state.Phase != GamePhase.Attacking)
                return false;

            // First card must come from currentAttacker; подкидка allows others after that
            if (state.Table.Count == 0 && thrower.Id != state.CurrentAttackerId)
                return false;

            // Подкидка: thrower must be active and not the defender
            if (thrower.Id == state.CurrentDefenderId)
                return false;

            if (state.Winners.Contains(thrower.Id))
                return false;

            var throwerHand = state.PlayerHands[thrower.Id];
            if (!throwerHand.Contains(card))
                return false;

            // Additional cards must match a rank already on table
            if (state.Table.Count > 0)
            {
                var tableRanks = new HashSet<Rank>();
                foreach (var pair in state.Table)
                {
                    tableRanks.Add(pair.AttackCard.Rank);
                    if (pair.DefenseCard != null)
                        tableRanks.Add(pair.DefenseCard.Rank);
                }
                if (!tableRanks.Contains(card.Rank))
                    return false;
            }

            // Can't throw more cards than defender can beat
            var defenderId = state.CurrentDefenderId!.Value;
            var defenderHand = state.PlayerHands[defenderId];
            var unbeatCount = state.Table.Count(p => p.DefenseCard == null);
            if (unbeatCount + 1 > defenderHand.Count)
                return false;

            // Apply changes
            throwerHand.Remove(card);
            state.Table.Add(new TablePair { AttackCard = card });
            state.Phase = GamePhase.Defending;

            // Debug: File.AppendAllText("trigger.log", $"[ATTACK] {thrower.Name} throws {card}, Table.Count={state.Table.Count}\n");
            Console.WriteLine($"[TheFool] {thrower.Name} throws {card}");
            return true;
        }

        public bool Defend(Card attackCard, Card defenseCard)
        {
            var state = Context.State;
            var defender = GetCallerPlayer();

            if (defender == null)
                return false;

            // Validation
            if (state.Phase != GamePhase.Defending)
                return false;

            if (defender.Id != state.CurrentDefenderId)
                return false;

            var defenderHand = state.PlayerHands[defender.Id];
            if (!defenderHand.Contains(defenseCard))
                return false;

            // Find the unbeaten pair on table
            var pair = state.Table.FirstOrDefault(p => p.AttackCard.Equals(attackCard) && p.DefenseCard == null);
            if (pair == null)
                return false;

            if (!Card.Beats(defenseCard, attackCard, state.TrumpSuit))
                return false;

            // Apply changes
            pair.DefenseCard = defenseCard;
            defenderHand.Remove(defenseCard);

            Console.WriteLine($"[TheFool] {defender.Name} beats {attackCard} with {defenseCard}");

            // All pairs beaten → back to Attacking (attacker may throw more or end)
            if (state.Table.All(p => p.DefenseCard != null))
            {
                state.Phase = GamePhase.Attacking;
            }

            // Note: Trigger OnDefendComplete is now auto-executed by generated dispatcher

            return true;
        }

        public bool TakeCards()
        {
            var state = Context.State;
            var defender = GetCallerPlayer();

            if (defender == null)
                return false;

            // Validation
            if (state.Phase != GamePhase.Defending)
                return false;

            if (defender.Id != state.CurrentDefenderId)
                return false;

            // Apply changes - defender takes all table cards
            var defenderHand = state.PlayerHands[defender.Id];
            foreach (var pair in state.Table)
            {
                defenderHand.Add(pair.AttackCard);
                if (pair.DefenseCard != null)
                    defenderHand.Add(pair.DefenseCard);
            }
            state.Table.Clear();

            Console.WriteLine($"[TheFool] {defender.Name} takes cards (hand: {defenderHand.Count})");

            RefillHands(state);
            CheckGameOver(state);

            if (state.Phase != GamePhase.GameOver)
            {
                // Same attacker, new defender (the one who took skips their attack turn)
                AdvanceRound(state, successfulDefense: false);
            }

            return true;
        }

        public bool EndAttack()
        {
            var state = Context.State;
            var attacker = GetCallerPlayer();

            if (attacker == null)
                return false;

            // Validation
            if (state.Phase != GamePhase.Attacking)
                return false;

            if (attacker.Id != state.CurrentAttackerId)
                return false;

            if (state.Table.Count == 0)
                return false;

            if (state.Table.Any(p => p.DefenseCard == null))
                return false;

            // Apply changes
            var defenderId = state.CurrentDefenderId!.Value;
            state.Table.Clear();

            Console.WriteLine($"[TheFool] {state.Players[defenderId].Name} defended successfully");

            RefillHands(state);
            CheckGameOver(state);

            if (state.Phase != GamePhase.GameOver)
                AdvanceRound(state, successfulDefense: true);

            return true;
        }

        // ============================================
        // Triggers - Auto-executed after method calls
        // ============================================

        /// <summary>
        /// After Defend: auto-end attack if no one can throw more.
        /// Condition: All cards beaten (Phase == Attacking after Defend).
        /// </summary>
        [Trigger(On = "Defend", Condition = "ShouldAutoEndAttack")]
        public void OnDefendComplete()
        {
            var state = Context.State;

            // Only trigger when all cards are beaten (Phase == Attacking)
            if (state.Phase != GamePhase.Attacking)
                return;

            // If no one can throw more, auto-end the attack
            if (!CanAnyoneThrowMore(state))
            {
                EndAttackInternal(state);
            }
        }

        /// <summary>
        /// Checks if auto-end should trigger after Defend.
        /// </summary>
        public bool ShouldAutoEndAttack()
        {
            var state = Context.State;
            return state.Phase == GamePhase.Attacking && !CanAnyoneThrowMore(state);
        }

        /// <summary>
        /// Internal version of EndAttack that doesn't require caller validation.
        /// Used by triggers.
        /// </summary>
        private void EndAttackInternal(GameState state)
        {
            // Debug: Console.Error.WriteLine($"[TRIGGER] EndAttackInternal called, Table.Count={state.Table.Count}");

            if (state.Table.Count == 0)
                return;

            if (state.Table.Any(p => p.DefenseCard == null))
                return;

            var defenderId = state.CurrentDefenderId!.Value;
            state.Table.Clear();

            Console.WriteLine($"[TheFool] {state.Players[defenderId].Name} defended successfully");

            // Debug: Console.Error.WriteLine($"[TRIGGER] Before RefillHands: Deck={state.Deck.Count}");
            RefillHands(state);
            // Debug: Console.Error.WriteLine($"[TRIGGER] After RefillHands: Deck={state.Deck.Count}");

            CheckGameOver(state);
            // Debug: Console.Error.WriteLine($"[TRIGGER] After CheckGameOver: Phase={state.Phase}");

            if (state.Phase != GamePhase.GameOver)
            {
                Console.WriteLine($"[TheFool] Auto-advancing round...");
                AdvanceRound(state, successfulDefense: true);
            }
        }

        /// <summary>
        /// Check if any active non-defender player can throw a card matching table ranks.
        /// </summary>
        private static bool CanAnyoneThrowMore(GameState state)
        {
            if (state.Table.Count == 0)
            {
                // Debug: Console.Error.WriteLine("[DEBUG] CanAnyoneThrowMore: table empty, returning true");
                return true; // Empty table - anyone can throw
            }

            // Get all ranks on the table
            var tableRanks = new HashSet<Rank>();
            foreach (var pair in state.Table)
            {
                tableRanks.Add(pair.AttackCard.Rank);
                if (pair.DefenseCard != null)
                    tableRanks.Add(pair.DefenseCard.Rank);
            }

            // Debug: Console.Error.WriteLine($"[DEBUG] CanAnyoneThrowMore: tableRanks={string.Join(",", tableRanks)}");

            var defenderId = state.CurrentDefenderId!.Value;
            var defenderHand = state.PlayerHands[defenderId];
            var unbeatCount = state.Table.Count(p => p.DefenseCard == null);

            // Debug: Console.Error.WriteLine($"[DEBUG] CanAnyoneThrowMore: defenderId={defenderId}, defenderHandCount={defenderHand.Count}, unbeatCount={unbeatCount}");

            // Check each active non-defender player
            foreach (var player in state.Players)
            {
                if (state.Winners.Contains(player.Id))
                    continue;
                if (player.Id == defenderId)
                    continue;

                var hand = state.PlayerHands[player.Id];
                var matchingCards = hand.Where(c => tableRanks.Contains(c.Rank)).ToList();
                // Debug: Console.Error.WriteLine($"[DEBUG] Player {player.Id} has {matchingCards.Count} matching cards: {string.Join(",", matchingCards)}");

                foreach (var card in hand)
                {
                    // Card rank must match table
                    if (tableRanks.Contains(card.Rank))
                    {
                        // Check if defender can still beat more cards
                        if (unbeatCount + 1 <= defenderHand.Count)
                        {
                            // Debug: Console.Error.WriteLine($"[DEBUG] Player {player.Id} CAN throw {card}");
                            return true;
                        }
                    }
                }
            }

            // Debug: Console.Error.WriteLine("[DEBUG] CanAnyoneThrowMore: no one can throw, returning false");
            return false;
        }

        // ============================================
        // Private Helpers
        // ============================================

        private void RefillHands(GameState state)
        {
            if (state.Deck.Count == 0) return;

            // Attacker refills first, then defender, then others
            var order = new List<int>();
            if (state.CurrentAttackerId.HasValue)
                order.Add(state.CurrentAttackerId.Value);
            if (state.CurrentDefenderId.HasValue)
                order.Add(state.CurrentDefenderId.Value);

            foreach (var player in state.Players)
            {
                if (!order.Contains(player.Id) && !state.Winners.Contains(player.Id))
                    order.Add(player.Id);
            }

            foreach (var playerId in order)
            {
                var hand = state.PlayerHands[playerId];
                while (hand.Count < state.MaxHandSize && state.Deck.Count > 0)
                {
                    hand.Add(state.Deck[0]);
                    state.Deck.RemoveAt(0);
                }
            }
        }

        private void CheckGameOver(GameState state)
        {
            // Players with empty hand + empty deck have won
            if (state.Deck.Count == 0)
            {
                foreach (var player in state.Players)
                {
                    if (!state.Winners.Contains(player.Id) && state.PlayerHands[player.Id].Count == 0)
                    {
                        state.Winners.Add(player.Id);
                        Console.WriteLine($"[TheFool] {player.Name} wins!");
                    }
                }
            }

            var activePlayers = state.Players.Where(p => !state.Winners.Contains(p.Id)).ToList();
            if (activePlayers.Count <= 1)
            {
                state.Phase = GamePhase.GameOver;
                state.GameStarted = false;

                if (activePlayers.Count == 1)
                {
                    state.LoserId = activePlayers[0].Id;
                    Console.WriteLine($"[TheFool] {activePlayers[0].Name} is the Fool!");
                }
                else
                {
                    state.LoserId = null;
                    Console.WriteLine("[TheFool] Draw!");
                }

                // Notify all player profiles about the game result
                NotifyProfilesAboutGameResult(state);
            }
        }

        private void NotifyProfilesAboutGameResult(GameState state)
        {
            if (OnGameResult == null) return;

            var gameEntityId = state.GameEntityId;
            if (string.IsNullOrEmpty(gameEntityId)) return;

            foreach (var player in state.Players)
            {
                if (!state.PlayerToProfileEntity.TryGetValue(player.Id, out var profileEntityId))
                    continue;

                GameResult result;
                if (state.LoserId == null)
                {
                    result = GameResult.Draw;
                }
                else if (state.LoserId == player.Id)
                {
                    result = GameResult.Loss;
                }
                else
                {
                    result = GameResult.Win;
                }

                try
                {
                    OnGameResult(profileEntityId, gameEntityId, result);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TheFool] Failed to notify profile {profileEntityId}: {ex.Message}");
                }
            }
        }

        private static void AdvanceRound(GameState state, bool successfulDefense)
        {
            int newAttackerId;
            if (successfulDefense)
            {
                // Defender becomes new attacker
                newAttackerId = state.CurrentDefenderId!.Value;
            }
            else
            {
                // Same attacker stays
                newAttackerId = state.CurrentAttackerId!.Value;
            }

            // If intended attacker already won, find next active
            if (state.Winners.Contains(newAttackerId))
                newAttackerId = GetNextActivePlayer(state, newAttackerId);

            var newDefenderId = GetNextActivePlayer(state, newAttackerId);
            state.CurrentAttackerId = newAttackerId;
            state.CurrentDefenderId = newDefenderId;
            state.Phase = GamePhase.Attacking;

            Console.WriteLine($"[TheFool] Round: {state.Players[newAttackerId].Name} attacks {state.Players[newDefenderId].Name}");
        }

        private static int GetNextActivePlayer(GameState state, int afterId)
        {
            var activePlayers = state.Players
                .Where(p => !state.Winners.Contains(p.Id))
                .OrderBy(p => p.Id)
                .ToList();

            if (activePlayers.Count < 2)
                return activePlayers.Count == 1 ? activePlayers[0].Id : afterId;

            var idx = activePlayers.FindIndex(p => p.Id == afterId);
            if (idx >= 0)
                return activePlayers[(idx + 1) % activePlayers.Count].Id;

            return activePlayers[0].Id;
        }

        private List<Card> CreateDeck()
        {
            var deck = new List<Card>();
            foreach (Suit suit in Enum.GetValues<Suit>())
                foreach (Rank rank in Enum.GetValues<Rank>())
                    deck.Add(new Card { Suit = suit, Rank = rank });
            return deck;
        }

        private void ShuffleDeck(List<Card> deck)
        {
            for (int i = deck.Count - 1; i > 0; i--)
            {
                int j = RandomService.Next(i + 1);
                (deck[i], deck[j]) = (deck[j], deck[i]);
            }
        }
    }
}
