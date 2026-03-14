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

        [MetaMethod(Alias = "IsActive", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        bool IsActive();
    }
}
