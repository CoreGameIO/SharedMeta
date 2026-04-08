using System.Collections.Generic;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Element type for Hero.Equipment — also has [MemoryPackOrder] members so the
    /// generator promotes List&lt;Item&gt; to the element-sub-wrappable path. Lets
    /// us exercise the two-level nested case <c>Heroes[i].Equipment[j].X = ...</c>.
    /// </summary>
    [MemoryPackable]
    public partial class Item
    {
        [MemoryPackOrder(0)] public int ItemId { get; set; }
        [MemoryPackOrder(1)] public string Name { get; set; } = "";
        [MemoryPackOrder(2)] public int Power { get; set; }
    }

    /// <summary>
    /// Element type for Party.Heroes — has [MemoryPackOrder] properties so the patch
    /// generator promotes List&lt;Hero&gt; to the element-sub-wrappable path. Each Hero
    /// gets its own HeroPatchWrapper, and mutations like <c>state.Heroes[0].Exp = X</c>
    /// flow into a per-element subtree of the patch instead of a full collection snapshot.
    /// </summary>
    [MemoryPackable]
    public partial class Hero
    {
        [MemoryPackOrder(0)] public int Id { get; set; }
        [MemoryPackOrder(1)] public string Name { get; set; } = "";
        [MemoryPackOrder(2)] public int Level { get; set; }
        [MemoryPackOrder(3)] public int Exp { get; set; }
        [MemoryPackOrder(4)] public int Hp { get; set; }
        [MemoryPackOrder(5)] public List<Item> Equipment { get; set; } = new List<Item>();
    }

    /// <summary>
    /// Test state for the element-sub-wrappable list path. <see cref="Heroes"/> is a
    /// <c>List&lt;Hero&gt;</c> where Hero itself has [MemoryPackOrder] members, so the
    /// generator emits a specialized PartyStatePatchWrapper.HeroPatchableList instead of
    /// the generic <c>PatchableList&lt;Hero&gt;</c>. Hero in turn has its own
    /// <c>List&lt;Item&gt; Equipment</c>, exercising two levels of nested element-list patching.
    /// </summary>
    [MemoryPackable]
    public partial class PartyState : ISharedState
    {
        [MemoryPackOrder(0)] public string PartyName { get; set; } = "";
        [MemoryPackOrder(1)] public List<Hero> Heroes { get; set; } = new List<Hero>();
        [MemoryPackOrder(2)] public int Gold { get; set; }
    }
}
