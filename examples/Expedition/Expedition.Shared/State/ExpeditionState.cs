using MemoryPack;
using MessagePack;
using SharedMeta.Core;

namespace Expedition.Shared
{
    public enum CellType : byte
    {
        Empty,
        Wall,
        Obstacle,
        Treasure
    }

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
    [MemoryPackable, MessagePackObject]
    [SharedState]
    public partial class ExpeditionState : ISharedState
    {
        [Key(0), MemoryPackOrder(0)] public int Width { get; set; }
        [Key(1), MemoryPackOrder(1)] public int Height { get; set; }
        [Key(2), MemoryPackOrder(2)] public List<byte> Cells { get; set; } = new();
        [Key(3), MemoryPackOrder(3)] public List<bool> Revealed { get; set; } = new();
        [Key(4), MemoryPackOrder(4)] public int PlayerX { get; set; }
        [Key(5), MemoryPackOrder(5)] public int PlayerY { get; set; }
        [Key(6), MemoryPackOrder(6)] public bool IsGenerated { get; set; }
        [Key(7), MemoryPackOrder(7)] public string? ProfileEntityId { get; set; }
        [Key(8), MemoryPackOrder(8)] public int TreasuresCollected { get; set; }
        [Key(9), MemoryPackOrder(9)] public int TotalTreasures { get; set; }
        [Key(10), MemoryPackOrder(10)] public bool IsComplete { get; set; }
        [Key(11), MemoryPackOrder(11)] public string? OwnerPlayerId { get; set; }
    }

    /// <summary>
    /// Result of ResumeOrStartExpedition — tells the client which expedition to use.
    /// </summary>
    [MemoryPackable, MessagePackObject]
    public partial class ResumeExpeditionResult
    {
        [Key(0), MemoryPackOrder(0)] public string EntityId { get; set; } = "";
        [Key(1), MemoryPackOrder(1)] public bool IsNew { get; set; }
    }
}
