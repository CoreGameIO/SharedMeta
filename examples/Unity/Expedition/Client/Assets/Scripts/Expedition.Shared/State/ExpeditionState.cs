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
    [MessagePackObject(true)]
    [SharedState]
    [NamedRandom("MapGen")]
    // Schema 2 introduced together with the 2.x config branch — when a client connects with
    // config 2.0+ the state is upgraded (one-way). After upgrade the per-entity gate blocks
    // 1.x clients from re-subscribing to this profile.
    [MetaStateVersion(2, "2.0", typeof(ExpeditionConfig))]
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
        [Key(7), MemoryPackOrder(7)] public string ProfileEntityId { get; set; } = "";
        [Key(8), MemoryPackOrder(8), MemoryPackInclude, Tracked] private int _treasuresCollected;
        // TotalTreasures is Tracked so the "Broken" map demo (GenerateNewMapBroken) produces a
        // detectable divergence: client and server each corrupt cells with their own
        // System.Random, then recount treasures — the resulting per-side counts differ, and the
        // patch-based DeepDesync compare picks it up. Without [Tracked] here the divergence is
        // invisible to the CRC check until a Move happens to land on a treasure on one side but
        // not the other (fragile).
        [Key(9), MemoryPackOrder(9), MemoryPackInclude, Tracked] private int _totalTreasures;
        [Key(10), MemoryPackOrder(10), MemoryPackInclude, Tracked] private bool _isComplete;
        [Key(11), MemoryPackOrder(11)] public string OwnerPlayerId { get; set; } = "";
    }

}
