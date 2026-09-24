using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Per-player store of account-level permissions, keyed by PlayerId. Backs the default
    /// <c>IPlayerEntitlements</c>.
    /// </summary>
    /// <remarks>
    /// Persisted rather than held on the connection, for the same reason the deep-desync flag is:
    /// it has to survive reconnects, and admin tooling and the running session must read and write
    /// the same place instead of two copies that drift.
    /// </remarks>
    public interface IPlayerEntitlementsGrain : IGrainWithStringKey
    {
        /// <summary>Permissions held, plus the generation they were last changed at. Never null.</summary>
        Task<PlayerPermissions> GetAsync();

        /// <summary>Adds permissions; already-held ones are ignored. Returns the resulting set.</summary>
        Task<PlayerPermissions> GrantAsync(List<string> permissions);

        /// <summary>Removes permissions; ones not held are ignored. Returns the resulting set.</summary>
        Task<PlayerPermissions> RevokeAsync(List<string> permissions);

        /// <summary>Replaces the whole set. Returns the resulting set.</summary>
        Task<PlayerPermissions> SetAsync(List<string> permissions);
    }
}
