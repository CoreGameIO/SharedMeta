using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Persistent state for <see cref="PlayerEntitlementsGrain"/>.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class PlayerEntitlementsGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public List<string> Permissions { get; set; } = new();

        /// <summary>
        /// Bumped on every change that actually altered the set. Clients keep the highest they have
        /// seen so a push that arrives out of order cannot reinstate an older set.
        /// </summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public int Generation { get; set; }
    }

    /// <summary>
    /// Stores account-level permissions for one player.
    /// </summary>
    public class PlayerEntitlementsGrain : Grain, IPlayerEntitlementsGrain
    {
        private readonly IPersistentState<PlayerEntitlementsGrainState> _state;

        public PlayerEntitlementsGrain(
            [PersistentState("playerEntitlements", "Default")] IPersistentState<PlayerEntitlementsGrainState> state)
        {
            _state = state;
        }

        public Task<PlayerPermissions> GetAsync() => Task.FromResult(Snapshot());

        public async Task<PlayerPermissions> GrantAsync(List<string> permissions)
        {
            bool changed = false;
            if (permissions != null)
            {
                foreach (var p in permissions)
                {
                    if (string.IsNullOrEmpty(p) || _state.State.Permissions.Contains(p)) continue;
                    _state.State.Permissions.Add(p);
                    changed = true;
                }
            }
            return await CommitAsync(changed);
        }

        public async Task<PlayerPermissions> RevokeAsync(List<string> permissions)
        {
            bool changed = false;
            if (permissions != null)
            {
                foreach (var p in permissions)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    changed |= _state.State.Permissions.Remove(p);
                }
            }
            return await CommitAsync(changed);
        }

        public async Task<PlayerPermissions> SetAsync(List<string> permissions)
        {
            var next = new List<string>();
            if (permissions != null)
            {
                foreach (var p in permissions)
                {
                    if (!string.IsNullOrEmpty(p) && !next.Contains(p)) next.Add(p);
                }
            }

            bool changed = next.Count != _state.State.Permissions.Count;
            if (!changed)
            {
                foreach (var p in next)
                {
                    if (!_state.State.Permissions.Contains(p)) { changed = true; break; }
                }
            }

            if (changed) _state.State.Permissions = next;
            return await CommitAsync(changed);
        }

        // The generation only moves when the set actually moved, so a no-op grant does not make
        // every live session re-read permissions it already has.
        private async Task<PlayerPermissions> CommitAsync(bool changed)
        {
            if (!changed) return Snapshot();

            _state.State.Generation++;
            await _state.WriteStateAsync();
            return Snapshot();
        }

        private PlayerPermissions Snapshot() => new PlayerPermissions
        {
            Names = _state.State.Permissions.Count == 0
                ? Array.Empty<string>()
                : _state.State.Permissions.ToArray(),
            Generation = _state.State.Generation,
        };
    }
}
