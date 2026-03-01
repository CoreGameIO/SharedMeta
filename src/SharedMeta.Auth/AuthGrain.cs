using System;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Persistent state for auth grain.
    /// Stores the mapping from DeviceId (grain key) to PlayerId.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AuthGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string? PlayerId { get; set; }
        [Id(1), Key(1), MemoryPackOrder(1)] public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Authentication grain. Grain key = DeviceId.
    /// On first login, generates a new PlayerId and persists it.
    /// On subsequent logins, returns the existing PlayerId.
    /// </summary>
    public class AuthGrain : Grain, IAuthGrain
    {
        private readonly IPersistentState<AuthGrainState> _state;

        public AuthGrain(
            [PersistentState("auth", "Default")] IPersistentState<AuthGrainState> state)
        {
            _state = state;
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

            _state.State.PlayerId = GeneratePlayerId();
            _state.State.CreatedAt = DateTime.UtcNow;
            await _state.WriteStateAsync();

            return new AuthLoginResult
            {
                PlayerId = _state.State.PlayerId,
                IsNewPlayer = true
            };
        }

        private static string GeneratePlayerId()
        {
            return $"{Guid.NewGuid().ToString("N")[..8]}_{DateTime.UtcNow:yyyyMMdd}";
        }
    }
}
