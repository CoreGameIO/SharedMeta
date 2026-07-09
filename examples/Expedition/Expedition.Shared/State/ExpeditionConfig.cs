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
    // Per-client config delivery: client app 1.x → 1.x.* config (lean economy);
    //                            client app 2.x → 2.x.* config (boosted economy).
    [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]
    public partial class ExpeditionConfig
    {
        [Key(0), MemoryPackOrder(0)] public int MapWidth { get; set; } = 15;
        [Key(1), MemoryPackOrder(1)] public int MapHeight { get; set; } = 10;
        [Key(2), MemoryPackOrder(2)] public int WallPercent { get; set; } = 20;
        [Key(3), MemoryPackOrder(3)] public int ObstaclePercent { get; set; } = 10;
        [Key(4), MemoryPackOrder(4)] public int TreasurePercent { get; set; } = 8;
        [Key(7), MemoryPackOrder(7)] public int TreasureReward { get; set; } = 25;
    }

    [MetaConfig]
    [MemoryPackable, MessagePackObject]
    // Per-client config delivery: client app 1.x → 1.x.* config (lean economy);
    //                            client app 2.x → 2.x.* config (boosted economy).
    [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]
    public partial class PlayerConfig
    {
        [Key(0), MemoryPackOrder(0)] public int MoveCost { get; set; } = 1;
        [Key(1), MemoryPackOrder(1)] public int ObstacleCost { get; set; } = 5;
    }


}
