using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Framework;
using SharedMeta.Orleans.Grains;

namespace SharedMeta.Orleans.Framework
{
    /// <summary>
    /// Orleans-based implementation of ILobbyRequester.
    /// Routes requests from ProfileService to ILobbyGrain.
    /// Gets entityId/playerId from the current MetaContext.
    /// </summary>
    public class OrleansLobbyRequester : ILobbyRequester
    {
        private readonly IGrainFactory _grainFactory;

        public OrleansLobbyRequester(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        }

        public async Task<bool> RequestMatchAsync(MatchRequest request)
        {
            // Get current context for entityId and playerId
            var context = MetaContextAccessor.Current
                ?? throw new InvalidOperationException("MetaContext not available. RequestMatchAsync must be called within a service method.");

            var entityId = context.EntityId
                ?? throw new InvalidOperationException("EntityId not set in MetaContext.");
            var playerId = context.CallerId ?? entityId; // Use callerId if available, otherwise entityId

            // Get lobby grain (singleton per game mode)
            var gameMode = request.GameMode ?? "default";
            var lobbyGrain = _grainFactory.GetGrain<ILobbyGrain>(gameMode);

            var result = await lobbyGrain.RequestMatchAsync(entityId, playerId, request.PlayerCount);

            return result.Success;
        }

        public async Task CancelMatchAsync(string gameMode)
        {
            var context = MetaContextAccessor.Current
                ?? throw new InvalidOperationException("MetaContext not available. CancelMatchAsync must be called within a service method.");

            var entityId = context.EntityId
                ?? throw new InvalidOperationException("EntityId not set in MetaContext.");

            var lobbyGrain = _grainFactory.GetGrain<ILobbyGrain>(gameMode ?? "default");
            await lobbyGrain.CancelMatchAsync(entityId);
        }
    }
}
