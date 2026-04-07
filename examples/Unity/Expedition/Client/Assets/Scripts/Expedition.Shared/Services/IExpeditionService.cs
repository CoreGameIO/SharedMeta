using System.Threading.Tasks;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaService(StateType = typeof(ExpeditionState), AccessPolicy = EntityAccessPolicy.Authorized, DefaultConfig = true)]
    public interface IExpeditionService : IMetaService
    {
        [MetaMethod(Alias = "Init", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void Init(string ownerPlayerId);

        /// <summary>Mark this expedition as abandoned. Internal — invoked by ProfileService.AbandonExpedition via cross-entity call.</summary>
        [MetaMethod(Alias = "MarkAbandoned", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void MarkAbandoned();

        [MetaMethod(Alias = "Move", Mode = ExecutionMode.CrossOptimistic)]
        Task<MoveResult> Move(int dx, int dy);

        [MetaMethod(Alias = "RemoveObstacle", Mode = ExecutionMode.CrossOptimistic)]
        Task<bool> RemoveObstacle(int dx, int dy);

        /// <summary>Query: check if expedition is active (no subscription needed).</summary>
        [MetaMethod(Alias = "IsActive", Query = true)]
        bool IsActive();

        /// <summary>Regenerate map — server generates, client receives full state replacement.</summary>
        [MetaMethod(Alias = "GenerateNewMap", Mode = ExecutionMode.ServerReplace)]
        void GenerateNewMap();

        /// <summary>Regenerate map — client predicts with same deterministic random seed.</summary>
        [MetaMethod(Alias = "GenerateNewMapOptimistic", Mode = ExecutionMode.Optimistic)]
        void GenerateNewMapOptimistic();

        /// <summary>
        /// Regenerate map using System.Random (intentionally non-deterministic).
        /// Demonstrates deep desync detection — server and client will produce different maps
        /// but return value is the same. Only patch CRC comparison catches this.
        /// </summary>
        [MetaMethod(Alias = "GenerateNewMapBroken", Mode = ExecutionMode.Optimistic)]
        void GenerateNewMapBroken();
    }
}
