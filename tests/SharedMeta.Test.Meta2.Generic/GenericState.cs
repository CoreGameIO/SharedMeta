using System.Collections.Generic;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta2.Generic
{
    /// <summary>Transformable argument. <see cref="Origin"/> distinguishes "arrived raw" from
    /// "produced by Unbox".</summary>
    [MemoryPackable]
    public partial class Point
    {
        [MemoryPackOrder(0)] public int X { get; set; }
        [MemoryPackOrder(1)] public int Y { get; set; }
        [MemoryPackOrder(2)] public string Origin { get; set; } = "";
    }

    [Transformer]
    public class PointTransformer : IArgumentTransformer<Point, int>
    {
        public int Box(Point value) => value.X * 1000 + value.Y;

        public Point Unbox(int value) => new Point
        {
            X = value / 1000,
            Y = value % 1000,
            Origin = "unboxed",
        };
    }

    /// <summary>Only the id travels; each side rebuilds the marker from its own state.</summary>
    [MemoryPackable]
    public partial class Marker
    {
        [MemoryPackOrder(0)] public int Id { get; set; }
        [MemoryPackOrder(1)] public string Label { get; set; } = "";
    }

    [Transformer]
    public class MarkerTransformer : IStateArgumentTransformer<Marker, int, GenericState>
    {
        public int Box(Marker value, GenericState state) => value.Id;

        public Marker Unbox(int value, GenericState state)
        {
            foreach (var marker in state.Markers)
                if (marker.Id == value) return marker;
            return new Marker { Id = value, Label = "missing" };
        }
    }

    [MemoryPackable]
    public partial class GenericState : ISharedState
    {
        [MemoryPackOrder(0)] public int LastX { get; set; }
        [MemoryPackOrder(1)] public int LastY { get; set; }
        [MemoryPackOrder(2)] public string LastOrigin { get; set; } = "";
        [MemoryPackOrder(3)] public int LastTag { get; set; }
        [MemoryPackOrder(4)] public int Calls { get; set; }
        [MemoryPackOrder(5)] public long Sum { get; set; }
        [MemoryPackOrder(6)] public List<Marker> Markers { get; set; } = new();
        [MemoryPackOrder(7)] public string LastLabel { get; set; } = "";
    }
}
