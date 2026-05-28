using System;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaServiceImpl(typeof(IExpeditionProfileService), typeof(ProfileState)
        , typeof(IExpeditionService)
        , typeof(ISocialService)
        , typeof(IEnergyService)
    )]
    public partial class ExpeditionProfileService : IExpeditionProfileService
    {
        private ProfileState state => Context.State;

        [MetaInit]
        public Task<int> InitState(int version, int target)
        {
            if (version < 1) {
                state.PlayerId = Context.EntityId!;
                state.Energy = Config.StartEnergy;
                state.Money = Config.StartMoney;
                state.LastEnergyUpdateTicks = Context.ServerTimeTicks;
            }
            return Task.FromResult(target);
        }

        public Task<string> DummyChange()
        {
            return Task.FromResult("DummyChange");
        }

        public async Task<bool> BuyEnergy()
        {
            if (state.Money < Config.BuyEnergyCost)
                return false;

            var energyService = await GetIEnergyServiceSiblingAsync();
            energyService.AddPurchasedEnergy();
            state.Money -= Config.BuyEnergyCost;

            return true;
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

        public void Ping(string pingValue)
        {
            Context.LogInfo(pingValue);
        }

        public async Task<bool> SendGift(string targetPlayerId, int amount)
        {
            if (amount <= 0) return false;
            if (state.Money < amount) return false;

            // Charge sender first so a slow target entity can't double-debit on retry.
            state.Money -= amount;

            // Cross-entity to the target player's social service. The target may not have an
            // ISocialServiceApiClient subscribed locally — 0.14.0's foreign-service replay path
            // ensures the mutation still reaches them via the shared state container.
            var social = GetISocialService(targetPlayerId);
            await social.ReceiveGiftAsync(state.PlayerId, amount);
            return true;
        }

        public async Task UpdateEnergy()
        {
            var energyService = await GetIEnergyServiceSiblingAsync();
            energyService.UpdateEnergy();
        }
    }
}
