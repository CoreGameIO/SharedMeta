using MemoryPack;
using MessagePack;
using Orleans;
using SharedMeta.Core;

namespace Expedition.Shared
{
    [GenerateSerializer]
    public enum CellType : byte
    {
        Empty,
        Wall,
        Obstacle,
        Treasure
    }

    [GenerateSerializer]
    public enum MoveResult : byte
    {
        Ok,
        Treasure,
        NoEnergy,
        Blocked,
        OutOfBounds,
        Complete
    }

    /// <summary>
    /// Expedition map state — a maze with fog of war, walls, obstacles, treasures.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    [SharedState]
    // Schema 2 = config 2.0+ — when a client connects with config 2.0+ the state is upgraded
    // (one-way). After upgrade the per-entity gate blocks 1.x clients from re-subscribing.
    [MetaStateVersion(2, "2.0", typeof(ExpeditionConfig))]
    public partial class ExpeditionState : ISharedState
    {
        [Key(0), MemoryPackOrder(0), Id(0)] public int Width { get; set; }
        [Key(1), MemoryPackOrder(1), Id(1)] public int Height { get; set; }
        [Key(2), MemoryPackOrder(2), Id(2)] public List<byte> Cells { get; set; } = new();
        [Key(3), MemoryPackOrder(3), Id(3)] public List<bool> Revealed { get; set; } = new();
        [Key(4), MemoryPackOrder(4), Id(4)] public int PlayerX { get; set; }
        [Key(5), MemoryPackOrder(5), Id(5)] public int PlayerY { get; set; }
        [Key(6), MemoryPackOrder(6), Id(6)] public bool IsGenerated { get; set; }
        [Key(7), MemoryPackOrder(7), Id(7)] public string? ProfileEntityId { get; set; }
        [Key(8), MemoryPackOrder(8), Id(8)] public int TreasuresCollected { get; set; }
        [Key(9), MemoryPackOrder(9), Id(9)] public int TotalTreasures { get; set; }
        [Key(10), MemoryPackOrder(10), Id(10)] public bool IsComplete { get; set; }
        [Key(11), MemoryPackOrder(11), Id(11)] public string? OwnerPlayerId { get; set; }
    }

    /// <summary>
    /// Result of ResumeOrStartExpedition — tells the client which expedition to use.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ResumeExpeditionResult
    {
        [Key(0), MemoryPackOrder(0), Id(0)] public string EntityId { get; set; } = "";
        [Key(1), MemoryPackOrder(1), Id(1)] public bool IsNew { get; set; }
    }
}
