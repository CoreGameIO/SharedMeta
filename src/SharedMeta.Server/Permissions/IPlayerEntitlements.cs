using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Server.Permissions
{
    /// <summary>
    /// Server-side source of truth for account-level permissions — what <c>[RequirePermission]</c>
    /// is checked against. The default implementation stores them per player in a grain; a host
    /// with its own account system registers its own.
    /// </summary>
    /// <remarks>
    /// Deliberately has no counterpart reachable from shared game logic. A permission that game
    /// code could grant would make <c>Cheat</c> reachable through any bug in any service, which is
    /// the whole reason permissions are separate from in-game roles. Writes belong to admin tooling
    /// and server-side code that is already inside the trust boundary.
    /// <para>
    /// Lives in the middleware layer, not the Orleans backend, because the permission gate on
    /// <c>ServerMetaContext</c> has to name it and that assembly cannot reference the backend.
    /// </para>
    /// </remarks>
    public interface IPlayerEntitlements
    {
        /// <summary>
        /// Permissions currently held by <paramref name="playerId"/>. Never null — a player with
        /// nothing granted reads as an empty set, which is distinct from the client-side "unknown".
        /// </summary>
        ValueTask<PlayerPermissions> GetAsync(string playerId);

        /// <summary>Adds permissions, leaving any already held untouched. Bumps the generation when something changed.</summary>
        ValueTask GrantAsync(string playerId, IReadOnlyList<string> permissions);

        /// <summary>Removes permissions the player holds, ignoring the rest. Bumps the generation when something changed.</summary>
        ValueTask RevokeAsync(string playerId, IReadOnlyList<string> permissions);

        /// <summary>Replaces the whole set. Bumps the generation when the result differs from what was held.</summary>
        ValueTask SetAsync(string playerId, IReadOnlyList<string> permissions);
    }
}
