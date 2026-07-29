using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Framework;

namespace CardGame.Shared
{
    /// <summary>
    /// Player profile service implementation.
    /// Handles player profile state and lobby interactions.
    /// </summary>
    [MetaServiceImpl(typeof(IProfileService), typeof(ProfileState), typeof(ILobbyRequester), typeof(IProfileService))]
    public partial class ProfileService : IProfileService, ILobbyListener
    {
        // state shorthand - Context is injected by generator
        private ProfileState state => Context.State;

        public void SetName(string name)
        {
            state.DisplayName = name;
        }

        public async Task SendGiftTo(string friendId)
        {
            // GetIProfileService returns IProfileServiceEntityCaller (async interface)
            await GetIProfileService(friendId).ReceiveGiftAsync(Context.EntityId!, 1);
        }

        public void ReceiveGift(string fromPlayer, int giftId)
        {
            Console.WriteLine($"[ProfileService] Received gift from {fromPlayer} (id={giftId})");
            state.GiftsReceived++;
        }

        public async Task<bool> RequestMatch(int playerCount)
        {
            if (state.IsSearching || state.CurrentGameId != null) {
                return false;
            }

            var gameMode = $"cg1-{playerCount}p";

            // Request match through lobby
            // On server: LobbyRequester is Recorder that calls real service and records result
            // On client: LobbyRequester is Replayer that reads recorded result
            var requested = await LobbyRequester.RequestMatchAsync(new MatchRequest {
                GameMode = gameMode,
                PlayerCount = playerCount,
                MaxWaitSeconds = 60
            });

            if (requested) {
                state.SearchGameMode = gameMode;
                state.IsSearching = true;
            }

            return requested;
        }

        public async Task<bool> CancelMatch()
        {
            if (!state.IsSearching || state.SearchGameMode == null)
            {
                return false;
            }

            await LobbyRequester.CancelMatchAsync(state.SearchGameMode);

            state.IsSearching = false;
            state.SearchGameMode = null;

            return true;
        }

        public void OnGameResult(string gameEntityId, GameResult result)
        {
            if (state.CurrentGameId != gameEntityId)
            {
                return; // Not our game
            }

            state.GamesPlayed++;

            switch (result)
            {
                case GameResult.Win:
                    state.Wins++;
                    break;
                case GameResult.Loss:
                    state.Losses++;
                    break;
            }

            state.CurrentGameId = null;
        }

        public ProfileState GetProfile()
        {
            return state;
        }

        // ============================================
        // ILobbyListener implementation
        //
        // The contract lives in the framework, so [MetaMethod] sits here rather than on
        // IProfileService — that is what gives these methods a game method id, a dispatcher
        // entry and client-side replay. GenerateClientApi = false keeps them server-originated;
        // Server rather than Notification so the lobby can await delivery and see a failure —
        // Notification dispatches [OneWay] and has nothing to wait on.
        // ============================================

        [MetaMethod(Alias = "OnMatchFound", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        public void OnMatchFound(MatchFoundEvent @event)
        {
            state.IsSearching = false;
            state.SearchGameMode = null;
            state.CurrentGameId = @event.MatchId;
        }

        [MetaMethod(Alias = "OnMatchCancelled", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        public void OnMatchCancelled(MatchCancelledEvent @event)
        {
            state.IsSearching = false;
            state.SearchGameMode = null;
        }

        [MetaMethod(Alias = "OnMatchmakingUpdate", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        public void OnMatchmakingUpdate(MatchmakingUpdateEvent @event)
        {
            // Could update UI with queue position, etc.
        }
    }
}
