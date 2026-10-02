using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // A state with a ServerPatch method gets a {Impl}_PatchTracked copy of every service on it. The
    // copy is a separate class, so every name a body can mention — dependency accessors, PatchState —
    // has to be emitted there too. This fixture is a compile-time guard: if the copy misses one of
    // them, this assembly stops building.

    /// <summary>Server-only bridge dependency (implemented in the test server).</summary>
    [ServerMetaService]
    public interface IPatchCopyBridge
    {
        Task<int> Lookup(int key);
    }

    [MemoryPackable]
    public partial class PatchCopyState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
        [MemoryPackOrder(1)] public int Looked { get; set; }
        [MemoryPackOrder(2)] public int Price { get; set; }
    }

    [MetaService(StateType = typeof(PatchCopyState))]
    public interface IPatchCopyService : IMetaService
    {
        /// <summary>The reason the copy exists; writes through the PatchState accessor.</summary>
        [MetaMethod(Alias = "Bump", Mode = ExecutionMode.ServerPatch)]
        void Bump(int amount);

        [MetaMethod(Alias = "LookupAndStore", Mode = ExecutionMode.Server)]
        Task<int> LookupAndStore(int key);

        [MetaMethod(Alias = "StorePrice", Mode = ExecutionMode.Server)]
        Task<int> StorePrice(int quantity);
    }

    [MetaServiceImpl(typeof(IPatchCopyService), typeof(PatchCopyState), typeof(IPatchCopyBridge), typeof(IPricingService))]
    public partial class PatchCopyService : IPatchCopyService
    {
        public void Bump(int amount) => PatchState.Value += amount;

        public async Task<int> LookupAndStore(int key)
        {
            var looked = await PatchCopyBridge.Lookup(key);
            State.Looked = looked;
            return looked;
        }

        public async Task<int> StorePrice(int quantity)
        {
            var pricing = await GetIPricingServiceAsync();
            State.Price = pricing.ComputeCost(quantity);
            return State.Price;
        }
    }
}
