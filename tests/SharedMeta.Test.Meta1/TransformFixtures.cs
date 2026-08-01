using System.Collections.Generic;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Argument type for the transformer round-trip fixtures. <see cref="Origin"/> is the
    /// tell-tale: it stays "" when the value crossed the wire as a raw <c>Coord</c> and becomes
    /// "unboxed" only when <see cref="CoordTransformer.Unbox"/> produced it. A test can therefore
    /// distinguish "the value arrived" from "the transformer actually ran".
    /// </summary>
    [MemoryPackable]
    public partial class Coord
    {
        [MemoryPackOrder(0)] public int X { get; set; }
        [MemoryPackOrder(1)] public int Y { get; set; }
        [MemoryPackOrder(2)] public string Origin { get; set; } = "";

        public override string ToString() => $"{X}:{Y}:{Origin}";
    }

    /// <summary>
    /// Boxes a <see cref="Coord"/> down to a single packed int, mirroring the canonical
    /// transformer shape (send an identifier, rebuild the object on the far side).
    /// </summary>
    [Transformer]
    public class CoordTransformer : IArgumentTransformer<Coord, int>
    {
        public int Box(Coord value) => value.X * 1000 + value.Y;

        public Coord Unbox(int value) => new Coord
        {
            X = value / 1000,
            Y = value % 1000,
            Origin = "unboxed",
        };
    }

    /// <summary>
    /// Argument for the state-aware fixture. Only <see cref="Id"/> crosses the wire; the receiving
    /// side rebuilds the rest from its own replicated state — the canonical reason a transformer
    /// exists at all.
    /// </summary>
    [MemoryPackable]
    public partial class Token
    {
        [MemoryPackOrder(0)] public int Id { get; set; }
        [MemoryPackOrder(1)] public string Label { get; set; } = "";
    }

    [Transformer]
    public class TokenTransformer : IStateArgumentTransformer<Token, int, TransformState>
    {
        public int Box(Token value, TransformState state) => value.Id;

        public Token Unbox(int value, TransformState state)
        {
            foreach (var token in state.Tokens)
                if (token.Id == value) return token;
            return new Token { Id = value, Label = "missing" };
        }
    }

    /// <summary>State for <see cref="ITransformService"/>. Records the last argument as observed
    /// by the service body, so a test can inspect what the dispatcher actually handed over.</summary>
    [MemoryPackable]
    public partial class TransformState : ISharedState
    {
        [MemoryPackOrder(0)] public int LastX { get; set; }
        [MemoryPackOrder(1)] public int LastY { get; set; }
        [MemoryPackOrder(2)] public string LastOrigin { get; set; } = "";
        [MemoryPackOrder(3)] public int LastTag { get; set; }
        [MemoryPackOrder(4)] public int Calls { get; set; }
        [MemoryPackOrder(5)] public List<Token> Tokens { get; set; } = new();
        [MemoryPackOrder(6)] public string LastLabel { get; set; } = "";
    }
}
