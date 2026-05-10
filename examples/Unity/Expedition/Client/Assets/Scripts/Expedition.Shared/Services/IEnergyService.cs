
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned, DefaultConfig = true)]
    public interface IEnergyService : IMetaService
    {
        [MetaMethod(Alias = "SpendEnergy", Mode = ExecutionMode.Optimistic, GenerateClientApi = false)]
        bool SpendEnergy(int amount);

        [MetaMethod(Alias = "SpendEnergyUpTo", Mode = ExecutionMode.Optimistic, GenerateClientApi = false)]
        int SpendEnergyUpTo(int maxAmount);

        [MetaMethod(Alias = "OnEnergyPurchase", Mode = ExecutionMode.Optimistic, GenerateClientApi = false)]
        int AddPurchasedEnergy();

        [MetaMethod(Alias = "UpdateEnergy", Mode = ExecutionMode.Optimistic)]
        int UpdateEnergy();

    }
}