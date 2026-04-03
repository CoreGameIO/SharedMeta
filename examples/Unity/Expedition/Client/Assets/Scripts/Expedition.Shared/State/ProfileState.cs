using System;
using MemoryPack;
using MessagePack;
using SharedMeta.Core;

namespace Expedition.Shared
{
    /// <summary>
    /// Player profile state with energy and money.
    /// Energy and Money are [Tracked] for push-based UI change notifications.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant)]
    [MessagePackObject(true)]
    [SharedState]
    public partial class ProfileState : ISharedState
    {
        [Key(0), MemoryPackOrder(0)] public string PlayerId { get; set; } = "";
        [Key(1), MemoryPackOrder(1), MemoryPackInclude, Tracked] private int _energy = 50;
        [Key(3), MemoryPackOrder(3), MemoryPackInclude, Tracked] private int _money = 100;
        [Key(4), MemoryPackOrder(4)] public long LastEnergyUpdateTicks { get; set; }
        [Key(5), MemoryPackOrder(5)] public int EnergyRegenSeconds { get; set; } = 10;
        [Key(6), MemoryPackOrder(6)] public string CurrentExpeditionEntityId { get; set; } = "";
        [Key(7), MemoryPackOrder(7)] public int ExpeditionCounter { get; set; }
    }
}
