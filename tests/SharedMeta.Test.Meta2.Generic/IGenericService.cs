using SharedMeta.Core;

namespace SharedMeta.Test.Meta2.Generic
{
    /// <summary>
    /// Mirrors the transformer fixtures of <c>SharedMeta.Test.Meta1</c>, but compiled through the
    /// generic codegen branch. Every method puts a plain <c>tag</c> AFTER the interesting argument:
    /// a framing mistake misplaces the reader, and the trailing value is what catches it.
    /// </summary>
    [MetaService(StateType = typeof(GenericState))]
    public interface IGenericService : IMetaService
    {
        /// <summary>Plain arguments — baseline that the generic wire works at all.</summary>
        [MetaMethod(Alias = "Add", Mode = ExecutionMode.Server)]
        int Add(int value, int tag);

        /// <summary>Optimistic mode: client runs the body first, then ships the same arguments.</summary>
        [MetaMethod(Alias = "AddOptimistic", Mode = ExecutionMode.Optimistic)]
        int AddOptimistic(int value, int tag);

        [MetaMethod(Alias = "MoveExplicit", Mode = ExecutionMode.Server)]
        string MoveExplicit([Transform(typeof(PointTransformer))] Point position, int tag);

        [MetaMethod(Alias = "MoveAuto", Mode = ExecutionMode.Server)]
        string MoveAuto(Point position, int tag);

        [MetaMethod(Alias = "MoveSkip", Mode = ExecutionMode.Server)]
        string MoveSkip([SkipTransform] Point position, int tag);

        [MetaMethod(Alias = "MoveMixed", Mode = ExecutionMode.Server)]
        string MoveMixed(int lead, [Transform(typeof(PointTransformer))] Point position, int tag);

        [MetaMethod(Alias = "AddMarker", Mode = ExecutionMode.Server)]
        void AddMarker(int id, string label);

        [MetaMethod(Alias = "TouchMarker", Mode = ExecutionMode.Server)]
        string TouchMarker(Marker marker, int tag);

        [MetaMethod(Alias = "PeekPoint", Mode = ExecutionMode.Query)]
        string PeekPoint([Transform(typeof(PointTransformer))] Point position, int tag);

        [MetaMethod(Alias = "PeekPlain", Mode = ExecutionMode.Query)]
        string PeekPlain(int first, int second);

        /// <summary>Server-only, single complex argument — the shape <c>{Service}ServerApi</c> uses.</summary>
        [MetaMethod(Alias = "AdminMove", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        string AdminMove([Transform(typeof(PointTransformer))] Point position, int tag);
    }
}
