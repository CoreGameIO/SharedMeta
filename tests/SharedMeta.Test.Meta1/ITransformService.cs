using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Argument-transformer fixtures. Every method takes the transformable argument FIRST and a
    /// plain <c>tag</c> SECOND — a boxing mismatch between client and server misframes the reader,
    /// so the trailing tag is what catches a silent framing bug that the first argument alone
    /// might survive.
    /// </summary>
    [MetaService(StateType = typeof(TransformState))]
    public interface ITransformService : IMetaService
    {
        /// <summary>Explicit transformer named on the parameter.</summary>
        [MetaMethod(Alias = "MoveExplicit", Mode = ExecutionMode.Server)]
        string MoveExplicit([Transform(typeof(CoordTransformer))] Coord position, int tag);

        /// <summary>No attribute — relies on discovery of <see cref="CoordTransformer"/>.</summary>
        [MetaMethod(Alias = "MoveAuto", Mode = ExecutionMode.Server)]
        string MoveAuto(Coord position, int tag);

        /// <summary>Transformation explicitly disabled — the raw <c>Coord</c> must cross the wire.</summary>
        [MetaMethod(Alias = "MoveSkip", Mode = ExecutionMode.Server)]
        string MoveSkip([SkipTransform] Coord position, int tag);

        /// <summary>Untransformable argument alongside a transformed one.</summary>
        [MetaMethod(Alias = "MoveMixed", Mode = ExecutionMode.Server)]
        string MoveMixed(int lead, [Transform(typeof(CoordTransformer))] Coord position, int tag);

        /// <summary>Seeds a token both sides can resolve, for the state-aware fixture.</summary>
        [MetaMethod(Alias = "AddToken", Mode = ExecutionMode.Server)]
        void AddToken(int id, string label);

        /// <summary>
        /// State-aware transformer: only the id travels, and each side rebuilds the token from its
        /// own replicated state. A label that comes back as "missing" means the lookup ran against
        /// the wrong state; a label that comes back as the caller's means it never ran at all.
        /// </summary>
        [MetaMethod(Alias = "TouchToken", Mode = ExecutionMode.Server)]
        string TouchToken(Token token, int tag);

        /// <summary>Query with arguments — separate wire path from the RPC dispatcher's callers.</summary>
        [MetaMethod(Alias = "PeekCoord", Mode = ExecutionMode.Query)]
        string PeekCoord([Transform(typeof(CoordTransformer))] Coord position, int tag);

        /// <summary>Query with plain arguments only.</summary>
        [MetaMethod(Alias = "PeekPlain", Mode = ExecutionMode.Query)]
        string PeekPlain(int first, int second);
    }
}
