using MemoryPack;
using SharedMeta.Core;

namespace CardGame.Shared
{
    [MetaService(StateType = typeof(GameState))]
    public interface ICardGameService : IMetaService
    {
        /// <summary>
        /// Set the game entity ID for result notifications.
        /// Called by server when creating the game entity.
        /// </summary>
        [MetaMethod(Alias = "SetGameEntityId", Mode = ExecutionMode.Server)]
        void SetGameEntityId(string gameEntityId);

        /// <summary>
        /// Register current client as a player. Called before NewGame.
        /// Server mode to ensure sequential registration.
        /// </summary>
        /// <param name="playerName">Display name for the player</param>
        /// <param name="profileEntityId">Profile entity ID for result notifications</param>
        [MetaMethod(Alias = "RegisterPlayer", Mode = ExecutionMode.Server)]
        void RegisterPlayer(string playerName, string profileEntityId);

        // Server mode required: uses IRandomService for deck shuffle
        [MetaMethod(Alias = "NewGame", Mode = ExecutionMode.Server)]
        void NewGame(int cardsPerPlayer);

        /// <summary>
        /// Throw a card at the defender. First card must come from currentAttacker;
        /// subsequent cards (подкидка) can come from any active non-defending player.
        /// Caller is identified via Context.CallerId.
        /// Returns false if move is invalid.
        /// </summary>
        [MetaMethod(Alias = "Attack", SkipServerOnFalse = true)]
        bool Attack(Card card);

        /// <summary>
        /// Beat an attack card on the table with a defense card from hand.
        /// Caller (defender) is identified via Context.CallerId.
        /// Returns false if defense is invalid.
        /// </summary>
        [MetaMethod(Alias = "Defend", SkipServerOnFalse = true)]
        bool Defend(Card attackCard, Card defenseCard);

        /// <summary>
        /// Defender concedes and takes all cards from the table.
        /// Caller (defender) is identified via Context.CallerId.
        /// Returns false if cannot take cards.
        /// </summary>
        [MetaMethod(Alias = "TakeCards", SkipServerOnFalse = true)]
        bool TakeCards();

        /// <summary>
        /// End the current attack round (all cards must be beaten).
        /// Caller (attacker) is identified via Context.CallerId.
        /// Triggers refill and role swap. Returns false if cannot end attack.
        /// </summary>
        [MetaMethod(Alias = "EndAttack", SkipServerOnFalse = true)]
        bool EndAttack();

        // ============================================
        // Triggers - Auto-executed after method calls
        // ============================================

        /// <summary>
        /// Trigger: Auto-end attack if no one can throw more after Defend.
        /// </summary>
        [Trigger(On = "Defend", Condition = "ShouldAutoEndAttack")]
        void OnDefendComplete();

        /// <summary>
        /// Condition for OnDefendComplete trigger.
        /// Returns true if all cards beaten and no one can throw more.
        /// </summary>
        bool ShouldAutoEndAttack();
    }
}
