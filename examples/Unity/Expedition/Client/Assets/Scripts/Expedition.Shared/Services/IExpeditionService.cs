using System.Threading.Tasks;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaService(StateType = typeof(ExpeditionState), AccessPolicy = EntityAccessPolicy.Authorized, DefaultConfig = true)]
    public interface IExpeditionService : IMetaService
    {
        [MetaMethod(Alias = "Init", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void Init(string ownerPlayerId);

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
    }
}
