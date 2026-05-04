using MemoryPack;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Test.Meta1
{
    // ───── Accepting (returns true) — proves comparer-based path swallows desync ─────

    /// <summary>State for IAcceptingComparerService — used only to satisfy the [SharedState] requirement.</summary>
    [MemoryPackable]
    public partial class AcceptingComparerState : ISharedState
    {
        [MemoryPackOrder(0)] public int Calls { get; set; }
    }

    /// <summary>
    /// Return type whose <see cref="MemoryPack"/> serialization differs between client and server
    /// because the impl uses <c>System.Random</c>. With a comparer registered that returns true
    /// unconditionally, the byte difference must be ignored — i.e. <c>OnResultMismatch</c> must
    /// not fire.
    /// </summary>
    [MemoryPackable]
    public partial class AcceptingPayload
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
    }

    [MetaService(StateType = typeof(AcceptingComparerState))]
    public interface IAcceptingComparerService : IMetaService
    {
        [MetaMethod(Alias = "Roll", Mode = ExecutionMode.Optimistic)]
        AcceptingPayload Roll();
    }

    [MetaServiceImpl(typeof(IAcceptingComparerService), typeof(AcceptingComparerState))]
    public partial class AcceptingComparerService : IAcceptingComparerService
    {
        public AcceptingPayload Roll()
        {
            // Intentionally non-deterministic — produces different bytes on client vs server.
            // The comparer is what makes this scenario "no-desync"; without it the byte
            // comparison fails and OnResultMismatch fires.
            var rng = new System.Random();
            return new AcceptingPayload { Value = rng.Next(1, 1_000_000) };
        }
    }

    /// <summary>
    /// Discovered automatically by the generator (implements <see cref="IMetaResultComparer{T}"/>).
    /// Returns true unconditionally — the test asserts that <c>OnResultMismatch</c> never fires
    /// for <see cref="IAcceptingComparerService.Roll"/>.
    /// </summary>
    public class AcceptingPayloadComparer : IMetaResultComparer<AcceptingPayload>
    {
        public bool AreEqual(AcceptingPayload server, AcceptingPayload local) => true;
    }

    // ───── Rejecting (returns false) — proves comparer overrides byte-equal as mismatch ─────

    [MemoryPackable]
    public partial class RejectingComparerState : ISharedState
    {
        [MemoryPackOrder(0)] public int Calls { get; set; }
    }

    [MemoryPackable]
    public partial class RejectingPayload
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
    }

    [MetaService(StateType = typeof(RejectingComparerState))]
    public interface IRejectingComparerService : IMetaService
    {
        [MetaMethod(Alias = "Echo", Mode = ExecutionMode.Optimistic)]
        RejectingPayload Echo(int value);
    }

    [MetaServiceImpl(typeof(IRejectingComparerService), typeof(RejectingComparerState))]
    public partial class RejectingComparerService : IRejectingComparerService
    {
        public RejectingPayload Echo(int value)
        {
            // Deterministic — server and client return identical bytes.
            return new RejectingPayload { Value = value };
        }
    }

    /// <summary>
    /// Returns false unconditionally — the test asserts that <c>OnResultMismatch</c> fires even
    /// when server and client bytes are identical, because the comparer overrides byte equality.
    /// </summary>
    public class RejectingPayloadComparer : IMetaResultComparer<RejectingPayload>
    {
        public bool AreEqual(RejectingPayload server, RejectingPayload local) => false;
    }
}
