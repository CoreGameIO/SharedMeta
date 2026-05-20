using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core;
using SharedMeta.Core.Framework;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Orleans.Grains
{
    /// <summary>
    /// Lobby grain implementation for matchmaking.
    /// One grain instance per game mode (grain key = game mode name).
    /// </summary>
    public class LobbyGrain : Grain, ILobbyGrain
    {
        private readonly List<LobbyWaitingPlayer> _waitingPlayers = new();
        private readonly IMetaSerializer _serializer;
        private readonly IGrainFactory _grainFactory;
        private readonly IEntityGrainResolver _entityGrainResolver;
        private readonly ILogger<LobbyGrain> _logger;
        private IDisposable? _timeoutTimer;

        public LobbyGrain(IMetaSerializer serializer, IGrainFactory grainFactory, IEntityGrainResolver entityGrainResolver, ILogger<LobbyGrain> logger)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            var gameMode = this.GetPrimaryKeyString();
            _logger.LobbyActivated(gameMode);

            // Set up periodic timeout processing
            _timeoutTimer = this.RegisterGrainTimer(
                ProcessTimeouts,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _timeoutTimer?.Dispose();
            return base.OnDeactivateAsync(reason, cancellationToken);
        }

        public Task<LobbyMatchRequestResult> RequestMatchAsync(string profileEntityId, string playerId, int playerCount)
        {
            var gameMode = this.GetPrimaryKeyString();

            // Check if already in queue
            if (_waitingPlayers.Any(p => p.ProfileEntityId == profileEntityId))
            {
                return Task.FromResult(new LobbyMatchRequestResult
                {
                    Success = false,
                    Error = "Already in matchmaking queue"
                });
            }

            // Add to queue
            var entry = new LobbyWaitingPlayer
            {
                ProfileEntityId = profileEntityId,
                PlayerId = playerId,
                PlayerCount = playerCount,
                GameMode = gameMode
            };
            _waitingPlayers.Add(entry);

            _logger.PlayerJoinedQueue(playerId, profileEntityId, gameMode, _waitingPlayers.Count, playerCount);

            // Try to form a match
            _ = TryFormMatchAsync();

            var position = _waitingPlayers.FindIndex(p => p.ProfileEntityId == profileEntityId) + 1;
            var estimatedWait = EstimateWaitTime(playerCount);

            return Task.FromResult(new LobbyMatchRequestResult
            {
                Success = true,
                QueuePosition = position,
                EstimatedWaitSeconds = estimatedWait
            });
        }

        public async Task<bool> CancelMatchAsync(string profileEntityId)
        {
            var gameMode = this.GetPrimaryKeyString();
            var player = _waitingPlayers.FirstOrDefault(p => p.ProfileEntityId == profileEntityId);

            if (player == null)
                return false;

            _waitingPlayers.Remove(player);
            _logger.PlayerLeftQueue(player.PlayerId, gameMode);

            // Notify the player that matchmaking was cancelled
            await NotifyPlayerAsync(profileEntityId, player.StateTypeName, SharedMeta.Core.Framework.FrameworkMethodIds.ILobbySubscriber_OnMatchCancelled, new MatchCancelledEvent
            {
                Reason = MatchCancelReason.PlayerCancelled
            });

            return true;
        }

        public Task<int> GetQueueLengthAsync()
        {
            return Task.FromResult(_waitingPlayers.Count);
        }

        private async Task TryFormMatchAsync()
        {
            var gameMode = this.GetPrimaryKeyString();

            _logger.TryFormMatch(_waitingPlayers.Count, gameMode);

            // Group by desired player count
            var byPlayerCount = _waitingPlayers
                .GroupBy(p => p.PlayerCount)
                .ToList();

            foreach (var group in byPlayerCount)
            {
                var requiredPlayers = group.Key;
                var available = group.ToList();

                _logger.MatchGroup(requiredPlayers, available.Count);

                while (available.Count >= requiredPlayers)
                {
                    // Take players for this match
                    var matchPlayers = available.Take(requiredPlayers).ToList();
                    available = available.Skip(requiredPlayers).ToList();

                    // Remove from waiting list
                    foreach (var player in matchPlayers)
                    {
                        _waitingPlayers.Remove(player);
                    }

                    // Create match ID
                    var matchId = $"match-{Guid.NewGuid():N}";
                    var playerIds = matchPlayers.Select(p => p.PlayerId).ToList();
                    var entityIds = matchPlayers.Select(p => p.ProfileEntityId).ToList();

                    _logger.MatchFormed(matchId, playerIds.Count);

                    // Notify all players
                    for (int i = 0; i < matchPlayers.Count; i++)
                    {
                        var player = matchPlayers[i];
                        await NotifyPlayerAsync(
                            player.ProfileEntityId,
                            player.StateTypeName,
                            SharedMeta.Core.Framework.FrameworkMethodIds.ILobbySubscriber_OnMatchFound,
                            new MatchFoundEvent
                            {
                                MatchId = matchId,
                                PlayerIds = playerIds,
                                GameMode = gameMode,
                                PlayerSlot = i
                            });
                    }
                }
            }
        }

        private async Task NotifyPlayerAsync<TEvent>(string profileEntityId, string stateTypeName, ushort methodId, TEvent @event)
        {
            try
            {
                // Serialize the event
                var eventBytes = _serializer.Pack(@event);

                // Resolve entity grain via generated switch (no reflection)
                var grain = _entityGrainResolver.GetEntityGrain(_grainFactory, stateTypeName, profileEntityId);
                if (grain == null)
                {
                    _logger.StateTypeNotFound(stateTypeName);
                    return;
                }

                // Call HandleExternalEventAsync — 0.24.0+ identifies the subscriber method
                // by framework methodId (e.g. FrameworkMethodIds.ILobbySubscriber_OnMatchFound)
                // rather than the legacy (subscriberInterface, methodName) string pair.
                await grain.HandleExternalEventAsync(methodId, eventBytes, null);

                _logger.PlayerNotified(profileEntityId, "methodId=" + methodId);
            }
            catch (Exception ex)
            {
                _logger.ErrorNotifyingPlayer(ex, profileEntityId, "methodId=" + methodId);
            }
        }

        private int EstimateWaitTime(int playerCount)
        {
            var sameCount = _waitingPlayers.Count(p => p.PlayerCount == playerCount);
            var needed = playerCount - sameCount;

            if (needed <= 0) return 5; // Match should form soon
            return needed * 15; // Rough estimate: 15 seconds per needed player
        }

        private Task ProcessTimeouts()
        {
            // For now, no timeout processing - can be added later
            // This would remove players who have been waiting too long
            return Task.CompletedTask;
        }
    }
}
