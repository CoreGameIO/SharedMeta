using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Simple state for testing packet delivery and ordering.
    /// Contains a list of operations with their order preserved.
    /// </summary>
    [MemoryPackable]
    [NamedRandom("Combat")]
    [NamedRandom("Loot")]
    public partial class CounterState : ISharedState
    {
        /// <summary>
        /// List of all operations applied to this state.
        /// Each entry: (CallerId, Value) - who added what.
        /// </summary>
        [MemoryPackOrder(0)] public List<CounterOperation> Operations { get; set; } = new();

        /// <summary>
        /// Sum of all values for quick verification.
        /// </summary>
        [MemoryPackOrder(1)] public long Sum { get; set; }

        /// <summary>
        /// Last ServerTimeTicks value seen during an operation.
        /// Used to verify time sync mechanism in tests.
        /// </summary>
        [MemoryPackOrder(2)] public long LastServerTimeTicks { get; set; }

        /// <summary>
        /// Version set by [MetaInit] during entity activation.
        /// </summary>
        [MemoryPackOrder(3)] public int InitializedVersion { get; set; }

        /// <summary>
        /// Tracked counter for testing push-based change tracking.
        /// </summary>
        [MemoryPackOrder(4), MemoryPackInclude, Tracked] private int _reactiveCounter;
    }

    /// <summary>
    /// A single counter operation for tracking who added what.
    /// </summary>
    [MemoryPackable]
    public partial class CounterOperation
    {
        /// <summary>
        /// Client who submitted this operation.
        /// </summary>
        [MemoryPackOrder(0)] public string CallerId { get; set; } = "";

        /// <summary>
        /// Random value added by this operation.
        /// </summary>
        [MemoryPackOrder(1)] public int Value { get; set; }

        /// <summary>
        /// Client-side sequence number for ordering verification.
        /// </summary>
        [MemoryPackOrder(2)] public int ClientSequence { get; set; }

        /// <summary>
        /// ServerTimeTicks at the time of this operation.
        /// </summary>
        [MemoryPackOrder(3)] public long ServerTimeTicks { get; set; }
    }
}
