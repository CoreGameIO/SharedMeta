using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Simple meta service for testing packet delivery and ordering.
    /// Each AddRandom call adds a value to the state and broadcasts it to all subscribers.
    /// </summary>
    [MetaService(StateType = typeof(CounterState), DefaultConfig = true)]
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
        /// Add a value to the tracked counter field.
        /// Tests push-based change tracking.
        /// </summary>
        [MetaMethod(Alias = "AddReactive", Mode = ExecutionMode.Server)]
        void AddReactive(int value);

        /// <summary>
        /// Reset the counter to initial state.
        /// </summary>
        [MetaMethod(Alias = "Reset", Mode = ExecutionMode.Server)]
        void Reset();

        /// <summary>
        /// CrossOptimistic method that calls another counter entity cross-entity.
        /// The target entity's method accesses Config — tests that Config is propagated
        /// to CrossOptimisticMetaContext during LocalEntityCaller execution.
        /// </summary>
        [MetaMethod(Alias = "AddCrossEntity", Mode = ExecutionMode.CrossOptimistic)]
        Task<int> AddCrossEntity(string targetEntityId, int value);

        /// <summary>
        /// Adds value clamped by Config.MaxValue. Called cross-entity from AddCrossEntity.
        /// Accesses Context.Config — will NRE if Config is not propagated.
        /// </summary>
        [MetaMethod(Alias = "AddClamped", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int AddClamped(int value);

        /// <summary>
        /// Throws if value is negative. Used to test framework-level error handling.
        /// Uses Optimistic mode so the exception fires during local execution (where SetError is).
        /// </summary>
        [MetaMethod(Alias = "ThrowIfNegative", Mode = ExecutionMode.Optimistic)]
        void ThrowIfNegative(int value);

        /// <summary>
        /// ServerReplace mode: server executes and sends full state to client.
        /// Client replaces state wholesale. Used when full state is smaller than patch.
        /// </summary>
        [MetaMethod(Alias = "ReplaceReset", Mode = ExecutionMode.ServerReplace)]
        int ReplaceReset(int newValue);

        /// <summary>
        /// Server mode method that reads another entity's state via Context.GetState.
        /// Returns the Sum from the target entity, or -1 if not found.
        /// </summary>
        [MetaMethod(Alias = "ReadOtherState", Mode = ExecutionMode.Server)]
        Task<long> ReadOtherEntityState(string targetEntityId);

        /// <summary>
        /// Draw a random value from one of the named random streams.
        /// Uses Optimistic mode so client and server both advance their copy of the stream.
        /// Tests deterministic named-random behavior and stream isolation.
        /// </summary>
        /// <param name="which">0 = Combat stream, 1 = Loot stream</param>
        /// <param name="max">upper bound (exclusive)</param>
        [MetaMethod(Alias = "DrawFromNamed", Mode = ExecutionMode.Optimistic)]
        int DrawFromNamed(int which, int max);

        /// <summary>
        /// Fire-and-forget heartbeat. Client does not wait, server does not broadcast.
        /// Impl writes to a static observer for test verification.
        /// </summary>
        [MetaMethod(Alias = "NotifyHeartbeat", Mode = ExecutionMode.Signal)]
        void NotifyHeartbeat(long clientTicks);
    }
}
