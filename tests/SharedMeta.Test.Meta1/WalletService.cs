using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Minimal second state type reusing <see cref="CounterConfig"/> as its config. Exists
    /// solely to test MetaServiceResolver.GetEntityConfig&lt;TConfig&gt;(entityId)'s ambiguous-match
    /// throw: subscribing both WalletState (via IWalletService) and CounterState (via
    /// ICounterService) under the same entityId gives two connections that both expose
    /// CounterConfig, which GetEntityConfig&lt;CounterConfig&gt; can no longer resolve unambiguously.
    /// </summary>
    [MemoryPackable]
    public partial class WalletState : ISharedState
    {
        [MemoryPackOrder(0)] public int Balance { get; set; }
    }

    [MetaService(StateType = typeof(WalletState))]
    [ServiceConfig(typeof(CounterConfig), "Config")]
    public interface IWalletService : IMetaService
    {
        [MetaMethod(Alias = "Deposit", Mode = ExecutionMode.Optimistic)]
        int Deposit(int amount);
    }

    [MetaServiceImpl(typeof(IWalletService), typeof(WalletState))]
    public partial class WalletService : IWalletService
    {
        private WalletState state => State;

        public int Deposit(int amount)
        {
            state.Balance += amount;
            return state.Balance;
        }
    }
}
