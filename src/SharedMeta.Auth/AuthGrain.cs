using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Persistent state for auth grain.
    /// Stores the mapping from auth key (grain key) to PlayerId.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AuthGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string? PlayerId { get; set; }
        [Id(1), Key(1), MemoryPackOrder(1)] public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Authentication grain. Grain key = auth key (device ID or "platform:platformUserId").
    /// On first login, generates a new PlayerId and persists it.
    /// On subsequent logins, returns the existing PlayerId.
    /// Link/Unlink allow associating multiple auth keys with one player.
    /// </summary>
    public class AuthGrain : Grain, IAuthGrain
    {
        private readonly IPersistentState<AuthGrainState> _state;
        private readonly IGrainFactory _grainFactory;

        public AuthGrain(
            [PersistentState("auth", "Default")] IPersistentState<AuthGrainState> state,
            IGrainFactory grainFactory)
        {
            _state = state;
            _grainFactory = grainFactory;
        }

        public async Task<AuthLoginResult> LoginAsync()
        {
            if (_state.State.PlayerId != null)
            {
                return new AuthLoginResult
                {
                    PlayerId = _state.State.PlayerId,
                    IsNewPlayer = false
                };
            }

            var playerId = GeneratePlayerId();
            _state.State.PlayerId = playerId;
            _state.State.CreatedAt = DateTime.UtcNow;
            await _state.WriteStateAsync();

            // Register this auth key in the player's index
            var index = _grainFactory.GetGrain<IAuthIndexGrain>(playerId);
            await index.AddKeyAsync(this.GetPrimaryKeyString());

            return new AuthLoginResult
            {
                PlayerId = playerId,
                IsNewPlayer = true
            };
        }

        public async Task<AuthLinkResult> LinkAsync(string playerId)
        {
            if (_state.State.PlayerId != null)
            {
                if (_state.State.PlayerId == playerId)
                    return new AuthLinkResult { Success = true };

                return new AuthLinkResult
                {
                    Success = false,
                    Error = "Already linked to a different player",
                    ExistingPlayerId = _state.State.PlayerId
                };
            }

            _state.State.PlayerId = playerId;
            _state.State.CreatedAt = DateTime.UtcNow;
            await _state.WriteStateAsync();

            var index = _grainFactory.GetGrain<IAuthIndexGrain>(playerId);
            await index.AddKeyAsync(this.GetPrimaryKeyString());

            return new AuthLinkResult { Success = true };
        }

        public async Task<bool> UnlinkAsync(string playerId)
        {
            if (_state.State.PlayerId != playerId)
                return false;

            var authKey = this.GetPrimaryKeyString();

            // Remove from index
            var index = _grainFactory.GetGrain<IAuthIndexGrain>(playerId);
            await index.RemoveKeyAsync(authKey);

            // Clear this grain's state
            _state.State.PlayerId = null;
            await _state.WriteStateAsync();

            return true;
        }

        public Task<string?> GetPlayerIdAsync()
        {
            return Task.FromResult(_state.State.PlayerId);
        }

        public async Task<string?> ForceUnlinkAsync()
        {
            var playerId = _state.State.PlayerId;
            if (playerId == null)
                return null;

            var authKey = this.GetPrimaryKeyString();

            // Remove from index
            var index = _grainFactory.GetGrain<IAuthIndexGrain>(playerId);
            await index.RemoveKeyAsync(authKey);

            // Clear this grain's state
            _state.State.PlayerId = null;
            await _state.WriteStateAsync();

            return playerId;
        }

        private static string GeneratePlayerId()
        {
            return $"{Guid.NewGuid().ToString("N")[..8]}_{DateTime.UtcNow:yyyyMMdd}";
        }
    }

    /// <summary>
    /// Persistent state for auth index grain.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AuthIndexGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public List<string> AuthKeys { get; set; } = new();
    }

    /// <summary>
    /// Index grain: tracks all auth keys linked to a PlayerId.
    /// Grain key = PlayerId.
    /// </summary>
    public class AuthIndexGrain : Grain, IAuthIndexGrain
    {
        private readonly IPersistentState<AuthIndexGrainState> _state;

        public AuthIndexGrain(
            [PersistentState("authIndex", "Default")] IPersistentState<AuthIndexGrainState> state)
        {
            _state = state;
        }

        public async Task AddKeyAsync(string authKey)
        {
            if (!_state.State.AuthKeys.Contains(authKey))
            {
                _state.State.AuthKeys.Add(authKey);
                await _state.WriteStateAsync();
            }
        }

        public async Task RemoveKeyAsync(string authKey)
        {
            if (_state.State.AuthKeys.Remove(authKey))
            {
                await _state.WriteStateAsync();
            }
        }

        public Task<List<string>> GetKeysAsync()
        {
            return Task.FromResult(new List<string>(_state.State.AuthKeys));
        }
    }
}
