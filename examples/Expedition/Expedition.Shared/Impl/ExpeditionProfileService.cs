using SharedMeta.Core;
using SharedMeta.Core.Framework;

namespace Expedition.Shared
{
    /// <summary>
    /// Profile service implementation — energy regen, spending, money management.
    /// </summary>
    [MetaServiceImpl(typeof(IExpeditionProfileService), typeof(ProfileState), typeof(IExpeditionService))]
    public partial class ExpeditionProfileService : IExpeditionProfileService
    {
        private ProfileState state => Context.State;

        [MetaInit]
        public Task<int> InitState(int version)
        {
            if (version < 1)
            {
                state.PlayerId = Context.EntityId!; // UserOwned: entityId == playerId
                state.Energy = 50;
                state.MaxEnergy = 50;
                state.Money = 100;
                state.EnergyRegenSeconds = 10;
                state.LastEnergyUpdateTicks = Context.ServerTimeTicks;
                return Task.FromResult(1);
            }
            return Task.FromResult(version);
        }

        public int UpdateEnergy()
        {
            if (state.LastEnergyUpdateTicks == 0)
            {
                state.LastEnergyUpdateTicks = Context.ServerTimeTicks;
                return state.Energy;
            }

            if (state.Energy >= state.MaxEnergy)
            {
                state.LastEnergyUpdateTicks = Context.ServerTimeTicks;
                return state.Energy;
            }

            var now = Context.ServerTimeTicks;
            var elapsed = now - state.LastEnergyUpdateTicks;
            var secondsElapsed = elapsed / TimeSpan.TicksPerSecond;

            if (secondsElapsed <= 0)
                return state.Energy;

            var regenAmount = (int)(secondsElapsed / state.EnergyRegenSeconds);
            if (regenAmount > 0)
            {
                state.Energy = Math.Min(state.Energy + regenAmount, state.MaxEnergy);
                // Advance timestamp by consumed regen ticks only
                state.LastEnergyUpdateTicks += regenAmount * state.EnergyRegenSeconds * TimeSpan.TicksPerSecond;
            }

            return state.Energy;
        }

        public bool BuyEnergy(int energyAmount, int moneyCost)
        {
            if (state.Money < moneyCost)
                return false;

            UpdateEnergy();

            state.Money -= moneyCost;
            state.Energy += energyAmount; // Bypasses MaxEnergy cap
            return true;
        }

        public bool SpendEnergy(int amount)
        {
            UpdateEnergy();

            if (state.Energy < amount)
                return false;

            state.Energy -= amount;
            Context.LogInfo("Spend {Amount} energy, energy now {Energy}", amount, state.Energy);
            return true;
        }

        public void AddMoney(int amount)
        {
            state.Money += amount;
        }

        public async Task<ResumeExpeditionResult> ResumeOrStartExpedition()
        {
            if (!string.IsNullOrEmpty(state.CurrentExpeditionEntityId))
            {
                var expService = GetIExpeditionService(state.CurrentExpeditionEntityId);
                bool active = await expService.IsActiveAsync();
                if (active)
                    return new ResumeExpeditionResult
                    {
                        EntityId = state.CurrentExpeditionEntityId,
                        IsNew = false
                    };
            }

            // Start new expedition
            state.ExpeditionCounter++;
            var entityId = $"expedition-{state.PlayerId}-{state.ExpeditionCounter}";
            state.CurrentExpeditionEntityId = entityId;

            // Initialize expedition with owner (cross-entity call)
            var newExpService = GetIExpeditionService(entityId);
            await newExpService.InitAsync(state.PlayerId);

            return new ResumeExpeditionResult
            {
                EntityId = entityId,
                IsNew = true
            };
        }
    }
}
