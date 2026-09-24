using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Permissions;

namespace SharedMeta.Server.Core.Permissions
{
    /// <summary>
    /// Default <see cref="IPlayerEntitlements"/>: one <see cref="IPlayerEntitlementsGrain"/> per
    /// player. Registered by the generated <c>ConfigureMeta</c> unless the host registered its own
    /// first, so permissions work with no wiring while a game with an existing account system can
    /// substitute it.
    /// </summary>
    public sealed class GrainPlayerEntitlements : IPlayerEntitlements
    {
        private readonly IGrainFactory _grainFactory;

        public GrainPlayerEntitlements(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory;
        }

        public async ValueTask<PlayerPermissions> GetAsync(string playerId)
        {
            // An empty caller id reaches here from an unauthenticated or server-originated call.
            // There is no account to read, and the gate must not admit one — an empty set denies
            // every requirement without a grain activation for a key that means "nobody".
            if (string.IsNullOrEmpty(playerId)) return new PlayerPermissions();

            return await Grain(playerId).GetAsync();
        }

        public async ValueTask GrantAsync(string playerId, IReadOnlyList<string> permissions)
        {
            RequirePlayer(playerId);
            await PushAsync(playerId, await Grain(playerId).GrantAsync(ToList(permissions)));
        }

        public async ValueTask RevokeAsync(string playerId, IReadOnlyList<string> permissions)
        {
            RequirePlayer(playerId);
            await PushAsync(playerId, await Grain(playerId).RevokeAsync(ToList(permissions)));
        }

        public async ValueTask SetAsync(string playerId, IReadOnlyList<string> permissions)
        {
            RequirePlayer(playerId);
            await PushAsync(playerId, await Grain(playerId).SetAsync(ToList(permissions)));
        }

        // Carries the change into the live session: it refreshes the connection's own stamped set
        // (so the next call is judged by the new rights) and the client's copy behind it. A session
        // that does not exist is the normal case and not an error — the next connect reads the store.
        // A push that fails, though, leaves that session running on its old rights until it
        // reconnects, which is why this is logged loudly rather than swallowed.
        private async Task PushAsync(string playerId, PlayerPermissions result)
        {
            try
            {
                await _grainFactory.GetGrain<Session.ISessionManager>(playerId).PushPermissionsAsync(result);
            }
            catch (Exception ex)
            {
                MetaLog.Error($"[Entitlements] Could not push a permission change to '{playerId}' — that session keeps its previous permissions until it reconnects: {ex.Message}");
            }
        }

        private IPlayerEntitlementsGrain Grain(string playerId)
            => _grainFactory.GetGrain<IPlayerEntitlementsGrain>(playerId);

        private static void RequirePlayer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("A permission write needs a player id.", nameof(playerId));
        }

        private static List<string> ToList(IReadOnlyList<string> permissions)
        {
            if (permissions == null) return new List<string>();
            var list = new List<string>(permissions.Count);
            for (int i = 0; i < permissions.Count; i++) list.Add(permissions[i]);
            return list;
        }
    }
}
