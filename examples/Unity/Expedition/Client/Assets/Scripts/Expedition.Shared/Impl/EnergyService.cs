using System;
using SharedMeta.Core;

namespace Expedition.Shared
{

    [MetaServiceImpl(typeof(IEnergyService), typeof(ProfileState))]
    public partial class EnergyService : IEnergyService
    {
        public bool SpendEnergy(int amount)
        {
            UpdateEnergy();

            if (State.Energy < amount)
                return false;

            State.Energy -= amount;
            return true;
        }

        public int SpendEnergyUpTo(int maxAmount)
        {
            UpdateEnergy();

            int spent = Math.Min(State.Energy, maxAmount);
            if (spent > 0)
                State.Energy -= spent;
            return spent;
        }

        public int AddPurchasedEnergy()
        {
            State.Energy += Config.BuyEnergyAmount;
            return State.Energy;
        }

        public int UpdateEnergy()
        {
            if (State.LastEnergyUpdateTicks == 0)
            {
                State.LastEnergyUpdateTicks = Context.ServerTimeTicks;
                return State.Energy;
            }

            if (State.Energy >= Config.MaxEnergy)
            {
                State.LastEnergyUpdateTicks = Context.ServerTimeTicks;
                return State.Energy;
            }

            var now = Context.ServerTimeTicks;
            var elapsed = now - State.LastEnergyUpdateTicks;
            var secondsElapsed = elapsed / TimeSpan.TicksPerSecond;

            if (secondsElapsed <= 0)
                return State.Energy;

            var regenAmount = (int)(secondsElapsed / Config.EnergyRegenSeconds);
            if (regenAmount > 0)
            {
                State.Energy = Math.Min(State.Energy + regenAmount, Config.MaxEnergy);
                State.LastEnergyUpdateTicks += regenAmount * Config.EnergyRegenSeconds * TimeSpan.TicksPerSecond;
            }

            return State.Energy;
        }
    }

}