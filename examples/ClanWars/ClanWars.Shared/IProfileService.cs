using System.Collections.Generic;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace ClanWars.Shared
{
    [MetaService(StateType = typeof(ProfileState), ConfigType = typeof(ClanConfig))]
    public interface IProfileService : IMetaService
    {
        /// <summary>Award bonus power to the player. Forwarded to the clan (if any).</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task GainPoints(int amount);

        /// <summary>Spend money + create a new clan grain. Returns the new clan id on success.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<string?> CreateClan(string name);

        /// <summary>Submit an application to join a clan. Cross-entity call into ClanService.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> ApplyToClan(string clanId);

        /// <summary>Leave the current clan. Player's score is subtracted from clan power.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task<bool> LeaveClan();

        /// <summary>
        /// Server-driven recommendation list — pulls non-full clans (any pinned config version)
        /// from the silo's <c>ClanContainerService</c> cache. The cluster-wide list spans BOTH
        /// v1- and v2-pinned clans, so v1 callers will naturally see v2-pinned candidates and
        /// vice versa — the engine of the cross-version subscription mix that drives force-patch.
        /// <para>
        /// Mode = Query: read-only, no state sync, no replay validation. Callers invoke via the
        /// generated <c>ProfileServiceQueryApi.EntityApi(playerId).GetRecommendedClansAsync(n)</c>
        /// (RPC variant) — the regular ApiClient local wrapper would return the client-side
        /// empty list (server-only DI is unreachable from client-side replay).
        /// </para>
        /// </summary>
        [MetaMethod(Mode = ExecutionMode.Query)]
        List<ClanSummary> GetRecommendedClans(int limit);

        /// <summary>Read profile summary without modifying state.</summary>
        [MetaMethod(Mode = ExecutionMode.Query)]
        ProfileSummary GetSummary();
    }
}
