using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Simple meta service for testing packet delivery and ordering.
    /// Each AddRandom call adds a value to the state and broadcasts it to all subscribers.
    /// </summary>
    [MetaService(StateType = typeof(CounterState))]
    public interface ICounterService : IMetaService
    {
        /// <summary>
        /// Add a random value to the counter.
        /// Server mode ensures operations are serialized.
        /// </summary>
        /// <param name="value">Random value to add</param>
        /// <param name="clientSequence">Client-side sequence number for ordering verification</param>
        [MetaMethod(Alias = "Add", Mode = ExecutionMode.Server)]
        void AddValue(int value, int clientSequence);

        /// <summary>
        /// Reset the counter to initial state.
        /// </summary>
        [MetaMethod(Alias = "Reset", Mode = ExecutionMode.Server)]
        void Reset();
    }
}
