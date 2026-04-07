using MemoryPack;
using MessagePack;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [MetaConfig(Default = true)]
    [MemoryPackable, MessagePackObject]
    public partial class ExpeditionConfig
    {
        // Map generation
        [Key(0), MemoryPackOrder(0)] public int MapWidth { get; set; } = 15;
        [Key(1), MemoryPackOrder(1)] public int MapHeight { get; set; } = 10;
        [Key(2), MemoryPackOrder(2)] public int WallPercent { get; set; } = 20;
        [Key(3), MemoryPackOrder(3)] public int ObstaclePercent { get; set; } = 10;
        [Key(4), MemoryPackOrder(4)] public int TreasurePercent { get; set; } = 8;

        // Energy costs
        [Key(5), MemoryPackOrder(5)] public int MoveCost { get; set; } = 1;
        [Key(6), MemoryPackOrder(6)] public int ObstacleCost { get; set; } = 5;

        // Rewards
        [Key(7), MemoryPackOrder(7)] public int TreasureReward { get; set; } = 25;

        // Energy system
        [Key(8), MemoryPackOrder(8)] public int MaxEnergy { get; set; } = 50;
        [Key(9), MemoryPackOrder(9)] public int StartEnergy { get; set; } = 50;
        [Key(10), MemoryPackOrder(10)] public int StartMoney { get; set; } = 100;
        [Key(11), MemoryPackOrder(11)] public int EnergyRegenSeconds { get; set; } = 10;

        // Buy energy
        [Key(12), MemoryPackOrder(12)] public int BuyEnergyAmount { get; set; } = 10;
        [Key(13), MemoryPackOrder(13)] public int BuyEnergyCost { get; set; } = 20;
    }
}
