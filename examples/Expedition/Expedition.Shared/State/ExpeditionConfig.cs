using MemoryPack;
using MessagePack;
using SharedMeta.Core;

namespace Expedition.Shared
{
    /// <summary>
    /// Static configuration for expedition map generation and gameplay balance.
    /// Provided by IMetaConfigProvider on the server, sent to clients on subscribe.
    /// </summary>
    [MetaConfig(Default = true)]
    [MemoryPackable, MessagePackObject]
    public partial class ExpeditionConfig
    {
        [Key(0), MemoryPackOrder(0)] public int MapWidth { get; set; } = 15;
        [Key(1), MemoryPackOrder(1)] public int MapHeight { get; set; } = 10;
        [Key(2), MemoryPackOrder(2)] public int WallPercent { get; set; } = 20;
        [Key(3), MemoryPackOrder(3)] public int ObstaclePercent { get; set; } = 10;
        [Key(4), MemoryPackOrder(4)] public int TreasurePercent { get; set; } = 8;
        [Key(5), MemoryPackOrder(5)] public int MoveCost { get; set; } = 1;
        [Key(6), MemoryPackOrder(6)] public int ObstacleCost { get; set; } = 5;
        [Key(7), MemoryPackOrder(7)] public int TreasureReward { get; set; } = 25;
    }
}
