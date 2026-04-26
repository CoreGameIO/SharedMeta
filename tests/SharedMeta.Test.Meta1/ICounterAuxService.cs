using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Second service targeting CounterState — exists to test the multi-service-on-entity
    /// scenario added in 0.14.0. The framework lets several services share one state, and
    /// every API client subscribed to the entity must see every state mutation regardless
    /// of which service triggered it. CounterServiceTests exercise this through the pair
    /// ICounterService + ICounterAuxService.
    /// </summary>
    [MetaService(StateType = typeof(CounterState), DefaultConfig = true)]
    public interface ICounterAuxService : IMetaService
    {
        /// <summary>
        /// Server-mode mutation. Broadcast carries only replay-context (no state-data) —
        /// the receiver's entity-level handler must use this service's
        /// <see cref="MetaServiceConfig.EntityReplayDispatcher"/> to spin up a
        /// <see cref="CounterAuxService"/> instance and re-run the method against the
        /// shared state. That's how a client holding only <see cref="ICounterService"/>'s
        /// ApiClient still sees mutations originating from this foreign service.
        /// </summary>
        [MetaMethod(Alias = "AuxAdd", Mode = ExecutionMode.Server)]
        int AuxAdd(int value);

        /// <summary>
        /// Optimistic-mode mutation — used to verify local-execution bumps go through
        /// the shared <c>EntityStateContainer</c> too (not just broadcasts).
        /// </summary>
        [MetaMethod(Alias = "AuxBumpReactive", Mode = ExecutionMode.Optimistic)]
        void AuxBumpReactive();
    }
}
