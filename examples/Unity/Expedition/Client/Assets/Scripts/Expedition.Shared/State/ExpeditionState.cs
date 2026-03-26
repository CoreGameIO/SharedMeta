using System;
using System.Collections.Generic;
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

    [MemoryPackable(GenerateType.VersionTolerant)]
    [MessagePackObject]
    [SharedState]
    public partial class ExpeditionState : ISharedState
    {
        [Key(0), MemoryPackOrder(0)] public int Width { get; set; }
        [Key(1), MemoryPackOrder(1)] public int Height { get; set; }
#pragma warning disable MEMPACK019
        [Key(2), MemoryPackOrder(2)] public List<byte> Cells { get; set; } = new List<byte>();
        [Key(3), MemoryPackOrder(3)] public List<bool> Revealed { get; set; } = new List<bool>();
#pragma warning restore MEMPACK019
        [Key(4), MemoryPackOrder(4)] public int PlayerX { get; set; }
        [Key(5), MemoryPackOrder(5)] public int PlayerY { get; set; }
        [Key(6), MemoryPackOrder(6)] public bool IsGenerated { get; set; }
        [Key(7), MemoryPackOrder(7)] public string ProfileEntityId { get; set; }
        [Key(8), MemoryPackOrder(8), MemoryPackInclude, Tracked] private int _treasuresCollected;
        [Key(9), MemoryPackOrder(9)] public int TotalTreasures { get; set; }
        [Key(10), MemoryPackOrder(10), MemoryPackInclude, Tracked] private bool _isComplete;
        [Key(11), MemoryPackOrder(11)] public string OwnerPlayerId { get; set; }
    }

}
