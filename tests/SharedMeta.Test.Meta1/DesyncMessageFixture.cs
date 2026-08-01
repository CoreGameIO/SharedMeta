using System.Collections.Generic;
using MemoryPack;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Test.Meta1
{
    /// <summary>State for <see cref="IDesyncMessageService"/> — only satisfies the [SharedState] requirement.</summary>
    [MemoryPackable]
    public partial class DesyncMessageState : ISharedState
    {
        [MemoryPackOrder(0)] public int Calls { get; set; }
    }

    /// <summary>
    /// Result covering every member shape the generated formatter has to render: scalar,
    /// string, nested DTO, collection. None of these types overrides <c>ToString()</c>, so
    /// without a formatter the desync message would print the type name for all of them.
    /// </summary>
    [MemoryPackable]
    public partial class SellCargoResult
    {
        [MemoryPackOrder(0)] public int Gold { get; set; }
        [MemoryPackOrder(1)] public string Item { get; set; } = "";
        [MemoryPackOrder(2)] public CargoLine? Line { get; set; }
        [MemoryPackOrder(3)] public List<int> Ids { get; set; } = new();
    }

    [MemoryPackable]
    public partial class CargoLine
    {
        [MemoryPackOrder(0)] public int Quantity { get; set; }
        [MemoryPackOrder(1)] public string Sku { get; set; } = "";
    }

    [MetaService(StateType = typeof(DesyncMessageState))]
    public interface IDesyncMessageService : IMetaService
    {
        /// <summary>Server mode — the only path that throws <c>DesyncException</c> on the client.</summary>
        [MetaMethod(Alias = "Sell", Mode = ExecutionMode.Server)]
        SellCargoResult Sell(int amount);
    }

    [MetaServiceImpl(typeof(IDesyncMessageService), typeof(DesyncMessageState))]
    public partial class DesyncMessageService : IDesyncMessageService
    {
        public SellCargoResult Sell(int amount)
        {
            State.Calls++;
            return new SellCargoResult
            {
                Gold = amount * 2,
                Item = "ore",
                Line = new CargoLine { Quantity = amount, Sku = "SKU-7" },
                Ids = { 1, 2, 3 },
            };
        }
    }

    /// <summary>
    /// Always reports a mismatch, so the throw path runs on a deterministic method — the test
    /// is about the message text, not about producing a real divergence.
    /// </summary>
    public class SellCargoResultComparer : IMetaResultComparer<SellCargoResult>
    {
        public bool AreEqual(SellCargoResult server, SellCargoResult local) => false;
    }
}
