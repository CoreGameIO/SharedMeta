using System;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaServiceImpl(typeof(IExpeditionProfileService), typeof(ProfileState), typeof(IExpeditionService))]
    public partial class ExpeditionProfileService : IExpeditionProfileService
    {
        private ProfileState state => Context.State;

        [MetaInit]
        public Task<int> InitState(int version)
        {
            if (version < 1)
            {
                state.PlayerId = Context.EntityId!;
                state.Energy = Config.StartEnergy;
                state.Money = Config.StartMoney;
                state.EnergyRegenSeconds = Config.EnergyRegenSeconds;
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

            if (state.Energy >= Config.MaxEnergy)
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
                state.Energy = Math.Min(state.Energy + regenAmount, Config.MaxEnergy);
                state.LastEnergyUpdateTicks += regenAmount * state.EnergyRegenSeconds * TimeSpan.TicksPerSecond;
            }

            return state.Energy;
        }

        public bool BuyEnergy()
        {
            if (state.Money < Config.BuyEnergyCost)
                return false;

            UpdateEnergy();

            state.Money -= Config.BuyEnergyCost;
            state.Energy += Config.BuyEnergyAmount;
            return true;
        }

        public bool SpendEnergy(int amount)
        {
            UpdateEnergy();

            if (state.Energy < amount)
                return false;

            state.Energy -= amount;
            return true;
        }

        public int SpendEnergyUpTo(int maxAmount)
        {
            UpdateEnergy();

            int spent = Math.Min(state.Energy, maxAmount);
            if (spent > 0)
                state.Energy -= spent;
            return spent;
        }

        public void AddMoney(int amount)
        {
            state.Money += amount;
        }

        public async Task<string> StartNewExpedition()
        {
            state.ExpeditionCounter++;
            var entityId = $"expedition-{state.PlayerId}-{state.ExpeditionCounter}";
            state.CurrentExpeditionEntityId = entityId;

            var newExpService = GetIExpeditionService(entityId);
            await newExpService.InitAsync(state.PlayerId);

            return entityId;
        }

        public async Task<bool> AbandonExpedition()
        {
            var current = state.CurrentExpeditionEntityId;
            if (string.IsNullOrEmpty(current))
                return false;

            var expCaller = GetIExpeditionService(current);
            await expCaller.MarkAbandonedAsync();

            state.CurrentExpeditionEntityId = "";
            return true;
        }
    }
}
