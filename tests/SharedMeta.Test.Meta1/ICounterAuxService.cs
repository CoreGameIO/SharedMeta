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

        // ============================================
        // 0.20.0 sibling-execution test methods
        // ============================================

        /// <summary>Mutates [Tracked] ReactiveCounter — invoked from sibling to test ChangeTracker.</summary>
        [MetaMethod(Alias = "MutateTracked", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void MutateTracked(int delta);

        /// <summary>Draws from Combat NamedRandom — invoked from sibling to test random scrolls.</summary>
        [MetaMethod(Alias = "DrawNamedCombat", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int DrawNamedCombat(int max);

        /// <summary>Calls back to ICounterService.AddClamped via sibling-bypass — recursive sibling.</summary>
        [MetaMethod(Alias = "CallBackToCounter", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        Task<int> CallBackToCounter(int value);

        /// <summary>Calls cross-entity to a different entity — verifies sibling+cross-entity composition.</summary>
        [MetaMethod(Alias = "AuxAddViaOther", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        Task<int> AuxAddViaOther(string otherEntityId, int value);

        /// <summary>Throws InvalidOperationException AFTER a partial state mutation. Sibling-call
        /// edge case: outer must observe the throw and decide rollback/cleanup; framework's
        /// PatchWrapper / ChangeTracker shouldn't leak corrupt state into a broadcast.</summary>
        [MetaMethod(Alias = "AuxThrowAfterMutate", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int AuxThrowAfterMutate(int value);

        /// <summary>Returns a copy of state.Operations (a list of complex MemoryPackable objects).
        /// SiblingCaller must pass-through the typed return value preserving the list contents.</summary>
        [MetaMethod(Alias = "AuxSnapshotOps", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        System.Collections.Generic.List<CounterOperation> AuxSnapshotOps();

        /// <summary>Draws from Context.ServerRandom — sibling's random advance must be recorded
        /// on server and replayed identically on client (otherwise random scroll delta diverges).</summary>
        [MetaMethod(Alias = "AuxDrawServerRandom", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int AuxDrawServerRandom(int max);

        /// <summary>Mutates the passed-in CounterOperation in place. Used to verify sibling-bypass
        /// passes objects BY REFERENCE — the caller observes the mutation. If any serialization
        /// boundary were involved, the sibling would receive a deserialized copy and the caller's
        /// instance would stay untouched. This is also the reason argument transformers don't
        /// apply to sibling-bypass: transformers are part of the serialization Box/Unbox cycle,
        /// which is skipped entirely on the typed in-process call path.</summary>
        [MetaMethod(Alias = "AuxMutateOpInPlace", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void AuxMutateOpInPlace(CounterOperation op);
    }
}
