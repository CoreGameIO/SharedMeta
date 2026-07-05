using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // ════════════════════════════════════════════════════════════════════════════
    //  [StatelessMetaService] fixture — a service with no entity/state, resolution
    //  requires only materializing its linked [MetaConfig]. Two resolution paths:
    //  1) from any [MetaServiceImpl] as a declared dependency (sibling-like) — see
    //     StatelessConsumerService.GetPrice below.
    //  2) directly from MetaClient, no entity subscribe — client.GetIPricingServiceAsync().
    // ════════════════════════════════════════════════════════════════════════════

    [MetaConfig]
    public class PricingConfig
    {
        public int BaseCost { get; set; } = 42;
    }

    [StatelessMetaService(typeof(PricingConfig))]
    public interface IPricingService
    {
        int ComputeCost(int quantity);
    }

    /// <summary>
    /// The impl gets only a typed <see cref="Config"/> property — no Context, no Random, no
    /// dependencies. Pure function of (Config, method args) by design.
    /// </summary>
    [StatelessMetaServiceImpl(typeof(IPricingService))]
    public partial class PricingService : IPricingService
    {
        public int ComputeCost(int quantity) => Config.BaseCost * quantity;
    }

    [SharedState]
    [MemoryPackable]
    public partial class StatelessConsumerState : ISharedState
    {
        [MemoryPackOrder(0)] public int LastPrice { get; set; }
    }

    [MetaService(StateType = typeof(StatelessConsumerState))]
    public interface IStatelessConsumerService : IMetaService
    {
        /// <summary>
        /// Path 1: resolves IPricingService as a declared dependency, sibling-like — no entity,
        /// only Config is materialized. Server-only (same constraint as multi-config siblings):
        /// GetIPricingServiceAsync() only has a real body under Mode=Server/ServerReplace/ServerPatch.
        /// </summary>
        [MetaMethod(Alias = "GetPrice", Mode = ExecutionMode.Server)]
        Task<int> GetPrice(int quantity);
    }

    [MetaServiceImpl(typeof(IStatelessConsumerService), typeof(StatelessConsumerState), typeof(IPricingService))]
    public partial class StatelessConsumerService : IStatelessConsumerService
    {
        public async Task<int> GetPrice(int quantity)
        {
            var pricing = await GetIPricingServiceAsync();
            var cost = pricing.ComputeCost(quantity);
            State.LastPrice = cost;
            return cost;
        }
    }
}
