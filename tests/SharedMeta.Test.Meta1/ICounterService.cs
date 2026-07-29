using SharedMeta.Core;
using SharedMeta.Core.Framework;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Simple meta service for testing packet delivery and ordering.
    /// Each AddRandom call adds a value to the state and broadcasts it to all subscribers.
    /// </summary>
    [MetaService(StateType = typeof(CounterState))]
    [ServiceConfig(typeof(CounterConfig), "Config")]
    public interface ICounterService : IMetaService, ILobbyListener
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
        /// Accesses the generated Config property (backed by Context.Configs[0], via
        /// [ServiceConfig]) — will NRE if Configs is not propagated.
        /// </summary>
        [MetaMethod(Alias = "AddClamped", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int AddClamped(int value);

        /// <summary>
        /// Admin-shaped method: server-only, single complex argument carrying a collection.
        /// Returns the number of dictionary entries it actually received, so a test can tell an
        /// argument that arrived intact from one that arrived default-constructed.
        /// </summary>
        [MetaMethod(Alias = "ApplyGrant", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int ApplyGrant(GrantRequest request);

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
        /// Optimistic: the client advances its local stream and sends the call to server
        /// for verification; server broadcasts the advance (NamedRandomScrollDeltas) to
        /// subscribers. Used by broadcast-propagation tests.
        /// </summary>
        /// <param name="which">0 = Combat stream, 1 = Loot stream</param>
        /// <param name="max">upper bound (exclusive)</param>
        [MetaMethod(Alias = "DrawFromNamed", Mode = ExecutionMode.Optimistic)]
        int DrawFromNamed(int which, int max);

        /// <summary>
        /// Empty Server-mode method used as a barrier in broadcast-propagation tests.
        /// Two facts make it a reliable barrier:
        ///   1. Grain single-threading — Ping from client A is serialized after any prior
        ///      call on the same entity, so when A's Ping returns, A's preceding call has
        ///      finished server-side and its broadcast has been dispatched.
        ///   2. Per-connection FIFO — any broadcast enqueued to client B before B's Ping
        ///      response arrives in front of it on B's receive queue, so by the time B's
        ///      Ping await returns, B has processed all earlier broadcasts.
        /// </summary>
        [MetaMethod(Alias = "Ping", Mode = ExecutionMode.Server)]
        void Ping();

        /// <summary>
        /// Fire-and-forget heartbeat. Client does not wait, server does not broadcast.
        /// Impl writes to a static observer for test verification.
        /// </summary>
        [MetaMethod(Alias = "NotifyHeartbeat", Mode = ExecutionMode.Signal)]
        void NotifyHeartbeat(long clientTicks);

        /// <summary>
        /// Validation-style method covered by the SkipServerOnFalse contract.
        /// Returns true and increments Sum when amount > 0; returns false and mutates nothing
        /// otherwise. The generator must wrap the server RPC in
        /// <c>if (!EqualityComparer&lt;bool&gt;.Default.Equals(localResult, default))</c> so the
        /// false-return branch never sends bytes over the wire — verified via a server-side
        /// observer (<see cref="CounterService.SkipServerOnFalseLog"/>).
        /// </summary>
        [MetaMethod(Alias = "TryAdd", Mode = ExecutionMode.Optimistic, SkipServerOnFalse = true)]
        bool TryAdd(int amount);

        // ============================================
        // 0.20.0 sibling-execution test methods
        // ============================================

        [MetaMethod(Alias = "SiblingAuxAdd", Mode = ExecutionMode.Server)]
        Task<int> SiblingAuxAdd(int value);

        [MetaMethod(Alias = "SiblingAuxAddExplicit", Mode = ExecutionMode.Server)]
        Task<int> SiblingAuxAddExplicit(int value);

        [MetaMethod(Alias = "SiblingTrackedBump", Mode = ExecutionMode.Server)]
        Task SiblingTrackedBump(int amount);

        [MetaMethod(Alias = "SiblingDrawCombat", Mode = ExecutionMode.Optimistic)]
        Task<int> SiblingDrawCombat(int max);

        [MetaMethod(Alias = "SiblingRecursive", Mode = ExecutionMode.Server)]
        Task<int> SiblingRecursive(int value);

        [MetaMethod(Alias = "SiblingThenCrossEntity", Mode = ExecutionMode.Server)]
        Task<int> SiblingThenCrossEntity(string otherEntityId, int value);

        /// <summary>
        /// Multi-config sibling test: outer CounterService (uses CounterConfig) calls
        /// AltConfigService sibling (uses CounterAltConfig). The async sibling-getter must
        /// resolve CounterAltConfig for the sibling's typed Config — not CounterConfig.
        /// State.Sum is set to the value the sibling read from its Config — test asserts it
        /// equals CounterAltConfig.MaxValue (7777), confirming the right config was applied.
        /// ServerReplace mode so client-side replay path doesn't need access to config DI
        /// (which throws on ClientMetaContext.ResolveService).
        /// </summary>
        [MetaMethod(Alias = "SiblingMultiConfig", Mode = ExecutionMode.ServerReplace)]
        Task<int> SiblingMultiConfig();

        /// <summary>Calls sibling AuxAdd N times in the same outer call — verifies cumulative
        /// mutations and that state arrives at expected sum on both sides.</summary>
        [MetaMethod(Alias = "SiblingAuxAddTimes", Mode = ExecutionMode.Server)]
        Task<int> SiblingAuxAddTimes(int valuePerCall, int times);

        /// <summary>Sibling throws after a partial mutation — outer's catch swallows. State
        /// must NOT contain the partial mutation (or the framework must consistently report
        /// the failure to the client without leaking corrupt state).</summary>
        [MetaMethod(Alias = "SiblingThrowsCaught", Mode = ExecutionMode.Server)]
        Task<int> SiblingThrowsCaught(int value);

        /// <summary>Sibling returns a list — verifies SiblingCaller typed pass-through preserves
        /// reference identity / serialization-tolerant types end-to-end.</summary>
        [MetaMethod(Alias = "SiblingReturnsOps", Mode = ExecutionMode.Server)]
        Task<int> SiblingReturnsOps(int seed);

        /// <summary>Sibling consumes ServerRandom — outer's response carries the recorded random
        /// scroll delta (via outer's optimistic random); client replay reads same value.</summary>
        [MetaMethod(Alias = "SiblingServerRandom", Mode = ExecutionMode.Server)]
        Task<int> SiblingServerRandom(int max);

        /// <summary>Pass an object to sibling, sibling mutates in place. Verifies typed
        /// by-reference pass-through (no serialization on sibling-bypass), which is also
        /// why argument transformers don't apply on this path.</summary>
        [MetaMethod(Alias = "SiblingByRef", Mode = ExecutionMode.Server)]
        Task<long> SiblingByReference();
    }
}
