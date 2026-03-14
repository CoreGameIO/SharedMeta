using System.Threading.Tasks;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned, DefaultConfig = true)]
    public interface IExpeditionProfileService : IMetaService
    {
        [MetaMethod(Alias = "UpdateEnergy")]
        int UpdateEnergy();

        [MetaMethod(Alias = "BuyEnergy")]
        bool BuyEnergy();

        [MetaMethod(Alias = "SpendEnergy", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        bool SpendEnergy(int amount);

        [MetaMethod(Alias = "SpendEnergyUpTo", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int SpendEnergyUpTo(int maxAmount);

        [MetaMethod(Alias = "AddMoney", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void AddMoney(int amount);

        [MetaMethod(Alias = "ResumeOrStartExpedition", Mode = ExecutionMode.Server)]
        Task<ResumeExpeditionResult> ResumeOrStartExpedition();
    }
}
