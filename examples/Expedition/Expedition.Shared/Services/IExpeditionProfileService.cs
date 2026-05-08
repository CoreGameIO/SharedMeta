using SharedMeta.Core;

namespace Expedition.Shared
{
    /// <summary>
    /// Profile service — manages energy and money.
    /// Energy regenerates over time. Can be spent on exploration.
    /// Money earned from treasures, spent on buying energy.
    /// </summary>
    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned, DefaultConfig = true)]
    public interface IExpeditionProfileService : IMetaService
    {
        /// <summary>
        /// Recalculate energy based on elapsed time. Returns current energy.
        /// </summary>
        [MetaMethod(Alias = "UpdateEnergy")]
        int UpdateEnergy();

        /// <summary>
        /// Buy energy with money. Bypasses MaxEnergy cap.
        /// </summary>
        [MetaMethod(Alias = "BuyEnergy")]
        bool BuyEnergy(int energyAmount, int moneyCost);

        /// <summary>
        /// Spend energy. Called cross-entity by ExpeditionService.
        /// </summary>
        [MetaMethod(Alias = "SpendEnergy", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        bool SpendEnergy(int amount);

        /// <summary>
        /// Add money. Called cross-entity by ExpeditionService when collecting treasure.
        /// </summary>
        [MetaMethod(Alias = "AddMoney", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void AddMoney(int amount);

        /// <summary>
        /// Resume current expedition or start a new one.
        /// Makes cross-entity call to expedition to check if it's still active.
        /// </summary>
        [MetaMethod(Alias = "ResumeOrStartExpedition", Mode = ExecutionMode.Server)]
        Task<ResumeExpeditionResult> ResumeOrStartExpedition();
    }
}
