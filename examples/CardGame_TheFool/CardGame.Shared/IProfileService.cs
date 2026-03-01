using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Framework;

namespace CardGame.Shared
{
    /// <summary>
    /// Player profile service interface.
    /// Each player has their own profile entity.
    /// </summary>
    [MetaService(StateType = typeof(ProfileState), SubscriberInterfaces = new[] { typeof(ILobbySubscriber) })]
    public interface IProfileService : IMetaService
    {
        /// <summary>
        /// Set player display name.
        /// </summary>
        [MetaMethod(Alias = "SetName", Mode = ExecutionMode.Server)]
        void SetName(string name);

        /// <summary>
        /// Send a gift to another player
        /// </summary>
        [MetaMethod(Alias = "SendGift", Mode = ExecutionMode.Server)]
        Task SendGiftTo(string fiendId);

        /// <summary>
        /// Receive a gift from another player.
        /// Called cross-entity by SendGiftTo.
        /// Synchronous in interface - async EntityCaller wrapper is generated.
        /// </summary>
        [MetaMethod(Alias = "ReceiveGift", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void ReceiveGift(string fromPlayer, int giftId);

        /// <summary>
        /// Request to join matchmaking queue.
        /// Async because it calls LobbyService (cross-entity call).
        /// </summary>
        [MetaMethod(Alias = "RequestMatch", Mode = ExecutionMode.Server)]
        Task<bool> RequestMatch(int playerCount);

        /// <summary>
        /// Cancel matchmaking request.
        /// Async because it calls LobbyService (cross-entity call).
        /// </summary>
        [MetaMethod(Alias = "CancelMatch", Mode = ExecutionMode.Server)]
        Task<bool> CancelMatch();

        /// <summary>
        /// Called by game service when match ends.
        /// </summary>
        [MetaMethod(Alias = "OnGameResult", Mode = ExecutionMode.Server)]
        void OnGameResult(string gameEntityId, GameResult result);

        /// <summary>
        /// Get current profile state (for debugging).
        /// </summary>
        [MetaMethod(Alias = "GetProfile", Mode = ExecutionMode.Server)]
        ProfileState GetProfile();
    }

    /// <summary>
    /// Result of a game for a player.
    /// </summary>
    [GenerateSerializer]
    public enum GameResult
    {
        Win,
        Loss,
        Draw
    }
}
