using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Catalog for the config-aware transformer fixtures. Names carry the resolved major, so a
    /// rebuilt definition shows which config produced it: "v1-item7" is the resolved 1.x branch,
    /// "v0-item7" a default instance nobody resolved.
    /// </summary>
    [MetaConfigVersion(Client = "1.x.*", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.0.0")]
    public class ItemCatalogConfig
    {
        public int Major { get; set; }

        public string NameOf(int id) => $"v{Major}-item{id}";
    }

    /// <summary>Definition argument: only <see cref="Id"/> crosses the wire.</summary>
    [MemoryPackable]
    public partial class ItemDef
    {
        [MemoryPackOrder(0)] public int Id { get; set; }
        [MemoryPackOrder(1)] public string Name { get; set; } = "";
    }

    [Transformer]
    public class ItemDefTransformer : IConfigArgumentTransformer<ItemDef, int, ItemCatalogConfig>
    {
        public int Box(ItemDef value, ItemCatalogConfig config) => value.Id;

        public ItemDef Unbox(int value, ItemCatalogConfig config)
            => new ItemDef { Id = value, Name = config.NameOf(value) };
    }

    /// <summary>
    /// Owned-instance argument: the count comes from the receiver's state, the name from its
    /// config — both must be rebuilt, neither is sent.
    /// </summary>
    [MemoryPackable]
    public partial class OwnedItem
    {
        [MemoryPackOrder(0)] public int Id { get; set; }
        [MemoryPackOrder(1)] public int Count { get; set; }
        [MemoryPackOrder(2)] public string Name { get; set; } = "";
    }

    [Transformer]
    public class OwnedItemTransformer
        : IStateConfigArgumentTransformer<OwnedItem, int, ConfigTransformState, ItemCatalogConfig>
    {
        public int Box(OwnedItem value, ConfigTransformState state, ItemCatalogConfig config) => value.Id;

        public OwnedItem Unbox(int value, ConfigTransformState state, ItemCatalogConfig config) => new OwnedItem
        {
            Id = value,
            Count = state.Stock.TryGetValue(value, out var count) ? count : -1,
            Name = config.NameOf(value),
        };
    }

    [MemoryPackable]
    public partial class ConfigTransformState : ISharedState
    {
        [MemoryPackOrder(0)] public string LastName { get; set; } = "";
        [MemoryPackOrder(1)] public int LastCount { get; set; }
        [MemoryPackOrder(2)] public int LastTag { get; set; }
        [MemoryPackOrder(3)] public int Calls { get; set; }
        [MemoryPackOrder(4)] public Dictionary<int, int> Stock { get; set; } = new();
    }

    /// <summary>
    /// Config-aware transformer fixtures. As in <see cref="ITransformService"/>, the transformed
    /// argument comes first and a plain <c>tag</c> second, so a framing mismatch shows up.
    /// </summary>
    [MetaService(StateType = typeof(ConfigTransformState))]
    [ServiceConfig(typeof(ItemCatalogConfig), "Catalog")]
    public interface IConfigTransformService : IMetaService
    {
        [MetaMethod(Alias = "Pick", Mode = ExecutionMode.Optimistic)]
        string Pick(ItemDef item, int tag);

        [MetaMethod(Alias = "PickServer", Mode = ExecutionMode.Server)]
        string PickServer(ItemDef item, int tag);

        [MetaMethod(Alias = "AddStock", Mode = ExecutionMode.Optimistic)]
        void AddStock(int id, int count);

        [MetaMethod(Alias = "UseOwned", Mode = ExecutionMode.Optimistic)]
        string UseOwned(OwnedItem item, int tag);
    }

    [MetaServiceImpl(typeof(IConfigTransformService), typeof(ConfigTransformState))]
    public partial class ConfigTransformService : IConfigTransformService
    {
        public string Pick(ItemDef item, int tag) => Record(item.Id, item.Name, 0, tag);

        public string PickServer(ItemDef item, int tag) => Record(item.Id, item.Name, 0, tag);

        public void AddStock(int id, int count) => State.Stock[id] = count;

        public string UseOwned(OwnedItem item, int tag) => Record(item.Id, item.Name, item.Count, tag);

        private string Record(int id, string name, int count, int tag)
        {
            State.LastName = name;
            State.LastCount = count;
            State.LastTag = tag;
            State.Calls++;
            return $"{id}:{name}:{count}:{tag}";
        }
    }

    [MemoryPackable]
    public partial class ConfigTransformRelayState : ISharedState
    {
        [MemoryPackOrder(0)] public string LastResult { get; set; } = "";
    }

    /// <summary>
    /// Cross-entity caller of <see cref="IConfigTransformService"/>. The hop boxes the argument
    /// with the config of the meta call it runs in, so this service declares the catalog too.
    /// </summary>
    [MetaService(StateType = typeof(ConfigTransformRelayState))]
    [ServiceConfig(typeof(ItemCatalogConfig), "Catalog")]
    public interface IConfigTransformRelayService : IMetaService
    {
        [MetaMethod(Alias = "Relay", Mode = ExecutionMode.Server)]
        Task<string> Relay(string targetEntityId, int id);
    }

    [MetaServiceImpl(typeof(IConfigTransformRelayService), typeof(ConfigTransformRelayState), typeof(IConfigTransformService))]
    public partial class ConfigTransformRelayService : IConfigTransformRelayService
    {
        public async Task<string> Relay(string targetEntityId, int id)
        {
            var result = await GetIConfigTransformService(targetEntityId).PickServerAsync(new ItemDef { Id = id, Name = "stale" }, 5);
            State.LastResult = result;
            return result;
        }
    }
}
