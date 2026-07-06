
using SharedMeta.Core;

namespace Expedition.Shared
{
    // Deliberately kept on the legacy DefaultConfig — no xUnit coverage for this Unity mirror,
    // migrate together with the main Expedition.Shared copy once verified.
#pragma warning disable CS0618
    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned, DefaultConfig = true)]
#pragma warning restore CS0618
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