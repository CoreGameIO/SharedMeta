using System;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Simple context for CounterService (for testing without generated code).
    /// </summary>
    public class CounterServiceContext
    {
        public CounterState State { get; set; } = null!;
        public string CallerId { get; set; } = "";
    }

    /// <summary>
    /// Implementation of the test counter service.
    /// Can be used with generated context or manual context.
    /// </summary>
    [MetaServiceImpl(typeof(ICounterService), typeof(CounterState), typeof(ICounterService), typeof(ICounterAuxService), typeof(IAltConfigService))]
    public partial class CounterService : ICounterService
    {
        // Manual context for testing without generated code
        private CounterServiceContext? _manualContext;

        /// <summary>
        /// Set context manually (for testing).
        /// </summary>
        public void SetContext(CounterServiceContext context)
        {
            _manualContext = context;
        }

        // Helper to get state (from generated Context or manual context)
        private CounterState GetState()
        {
            if (_manualContext != null)
                return _manualContext.State;
            // Fall back to generated Context if available
            return Context.State;
        }

        // Helper to get caller ID
        private string GetCallerId()
        {
            if (_manualContext != null)
                return _manualContext.CallerId;
            return Context.CallerId ?? "unknown";
        }

        [MetaInit]
        public Task<int> Init(int version)
        {
            if (version < 1)
            {
                // First-time initialization
                var state = GetState();
                state.InitializedVersion = 1;
                return Task.FromResult(1);
            }
            return Task.FromResult(version);
        }

        public void AddValue(int value, int clientSequence)
        {
            var state = GetState();
            var callerId = GetCallerId();

            var serverTimeTicks = _manualContext != null ? 0 : Context.ServerTimeTicks;

            state.Operations.Add(new CounterOperation
            {
                CallerId = callerId,
                Value = value,
                ClientSequence = clientSequence,
                ServerTimeTicks = serverTimeTicks
            });
            state.Sum += value;
            state.LastServerTimeTicks = serverTimeTicks;

            Console.WriteLine($"[Counter] {callerId} added {value} (seq={clientSequence}), sum={state.Sum}, ops={state.Operations.Count}, timeTicks={serverTimeTicks}");
        }

        public void AddReactive(int value)
        {
            var state = GetState();
            state.ReactiveCounter += value;
        }

        public void Reset()
        {
            var state = GetState();
            state.Operations.Clear();
            state.Sum = 0;

            Console.WriteLine("[Counter] Reset");
        }

        public async Task<int> AddCrossEntity(string targetEntityId, int value)
        {
            var targetService = GetICounterService(targetEntityId);
            int clamped = await targetService.AddClampedAsync(value);
            Console.WriteLine($"[Counter] AddCrossEntity: target={targetEntityId}, value={value}, clamped={clamped}");
            return clamped;
        }

        public int AddClamped(int value)
        {
            var config = Config;
            int clamped = Math.Min(value, config.MaxValue);
            var state = GetState();
            state.Sum += clamped;
            Console.WriteLine($"[Counter] AddClamped: value={value}, max={config.MaxValue}, clamped={clamped}, sum={state.Sum}");
            return clamped;
        }

        public int ApplyGrant(GrantRequest request)
        {
            var state = GetState();
            foreach (var amount in request.Currencies.Values)
                state.Sum += amount;
            return request.Currencies.Count;
        }

        public void ThrowIfNegative(int value)
        {
            if (value < 0)
                throw new InvalidOperationException($"Negative value not allowed: {value}");
            var state = GetState();
            state.Sum += value;
        }

        public int ReplaceReset(int newValue)
        {
            var state = GetState();
            state.Operations.Clear();
            state.Sum = newValue;
            Console.WriteLine($"[Counter] ReplaceReset to {newValue}");
            return newValue;
        }

        public async Task<long> ReadOtherEntityState(string targetEntityId)
        {
            var otherState = await Context.GetState<CounterState>(targetEntityId);
            if (otherState == null)
                return -1;
            return otherState.Sum;
        }

        public int DrawFromNamed(int which, int max)
        {
            // Generated Context props: CombatRandom → NamedRandoms[0], LootRandom → NamedRandoms[1]
            var rng = which == 0 ? CombatRandom : LootRandom;
            return rng.Next(max);
        }

        public void Ping()
        {
            // Empty Server-mode barrier — see interface doc for ordering rationale.
        }

        /// <summary>
        /// Server-side observer for signal method verification. Each received heartbeat is
        /// appended here so integration tests can assert "the signal arrived" without any
        /// state-mutation side-effect leaking into the shared state replay machinery.
        /// Static because the impl instance lives inside an Orleans grain we don't control.
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentBag<(string CallerId, long Ticks)> HeartbeatLog
            = new();

        public void NotifyHeartbeat(long clientTicks)
        {
            HeartbeatLog.Add((GetCallerId(), clientTicks));
            Console.WriteLine($"[Counter] NotifyHeartbeat from {GetCallerId()}: ticks={clientTicks}");
        }

        /// <summary>
        /// Server-side observer for SkipServerOnFalse — every TryAdd that lands on the server
        /// records its caller + amount. The integration test asserts the bag contains only
        /// the calls whose local return value was true; false-return calls must never reach the
        /// server (the generator's `if (!default-equals)` guard short-circuits them).
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentBag<(string CallerId, int Amount)> SkipServerOnFalseLog
            = new();

        public bool TryAdd(int amount)
        {
            // Optimistic methods run on BOTH sides — record only on the server, otherwise
            // local-exec on the client (which happens regardless of skipServerOnFalse) would
            // contaminate the bag and turn the integration test into a false positive
            // (single-AppDomain shared static collection).
            var ctx = MetaContextAccessor.Current;
            if (ctx?.IsServer == true)
                SkipServerOnFalseLog.Add((GetCallerId(), amount));
            // Validation contract: must NOT mutate state when returning false.
            if (amount <= 0) return false;
            var state = GetState();
            state.Sum += amount;
            return true;
        }

        // ============================================
        // 0.20.0 sibling-execution test methods
        // ============================================

        public async Task<int> SiblingAuxAdd(int value)
        {
            // Implicit cross-entity getter with self-id — server self-detect routes to the
            // cached sibling impl; client self-detect builds a transient impl with our Context.
            var aux = GetICounterAuxService(Context.EntityId ?? string.Empty);
            return await aux.AuxAddAsync(value);
        }

        public async Task<int> SiblingAuxAddExplicit(int value)
        {
            // Explicit sibling-async accessor: returns original ICounterAuxService (sync method
            // surface). Async-await resolves callee's typed Config through its provider.
            var aux = await GetICounterAuxServiceSiblingAsync();
            return aux.AuxAdd(value);
        }

        public async Task SiblingTrackedBump(int amount)
        {
            // Sibling mutates a [Tracked] field — outer ChangeTracker flushes notifications.
            var aux = await GetICounterAuxServiceSiblingAsync();
            aux.MutateTracked(amount);
        }

        public async Task<int> SiblingDrawCombat(int max)
        {
            // Sibling pulls from CombatRandom — outer call's random scroll must advance, and
            // the client's optimistic replay produces the same result.
            var aux = await GetICounterAuxServiceSiblingAsync();
            return aux.DrawNamedCombat(max);
        }

        public async Task<int> SiblingRecursive(int value)
        {
            // Outer (CounterService) → sibling AuxService.CallBackToCounter → sibling
            // CounterService.AddValue (back to us). Verifies the sibling-bypass survives
            // a recursive cycle and nested mutations all land in the same outer state.
            var aux = await GetICounterAuxServiceSiblingAsync();
            return await aux.CallBackToCounter(value);
        }

        public async Task<int> SiblingThenCrossEntity(string otherEntityId, int value)
        {
            // Outer → sibling AuxService → real cross-entity call to a DIFFERENT entity. The
            // inner cross-entity hop must use real grain RPC (not the self-bypass), confirming
            // sibling-bypass and cross-entity routing coexist correctly.
            var aux = await GetICounterAuxServiceSiblingAsync();
            return await aux.AuxAddViaOther(otherEntityId, value);
        }

        public async Task<int> SiblingMultiConfig()
        {
            // Multi-config sibling: AltConfigService is on the same TState (CounterState) but
            // declares ConfigType = typeof(CounterAltConfig) — different from CounterService's
            // primary CounterConfig. Get{Iface}SiblingAsync()'s async config-resolution path
            // looks up IMetaConfigProvider<CounterAltConfig> via DI and sets the sibling's
            // typed Config to a CounterAltConfig instance. The sibling then writes its
            // Config.MaxValue (7777) into State.Sum so the caller can verify.
            var alt = await GetIAltConfigServiceSiblingAsync();
            alt.WriteAltMaxToSum();
            return (int)Context.State.Sum;
        }

        public async Task<int> SiblingAuxAddTimes(int valuePerCall, int times)
        {
            // Multi-call sibling: same sibling instance is invoked N times in one outer call.
            // Each call mutates state cumulatively. Verifies that:
            //   - SiblingResolver returns the SAME cached impl across all N calls (no
            //     allocation churn);
            //   - the outer's PatchWrapper / ChangeTracker captures every mutation;
            //   - the final state reflects valuePerCall * times.
            var aux = GetICounterAuxService(Context.EntityId);
            for (int i = 0; i < times; i++)
                await aux.AuxAddAsync(valuePerCall);
            return (int)Context.State.Sum;
        }

        public async Task<int> SiblingThrowsCaught(int value)
        {
            // Sibling throws after partial mutation. Outer catches and returns a sentinel.
            // Test verifies the outer-level method returns the sentinel (proving the throw
            // crossed the sibling-call boundary) AND the framework didn't crash the grain.
            // Note: state.Sum WILL contain the partial mutation in current 0.20.0 — sibling-
            // bypass shares the outer's mutation pipeline by design (no implicit rollback).
            // Documented behaviour. The test asserts that fact too — outer code can use the
            // partial state if it wants, or compensate.
            var aux = GetICounterAuxService(Context.EntityId);
            try
            {
                await aux.AuxThrowAfterMutateAsync(value);
                return -1;  // unreachable
            }
            catch (System.InvalidOperationException)
            {
                return 999;
            }
        }

        public async Task<int> SiblingReturnsOps(int seed)
        {
            // Stage one operation, then ask sibling for a snapshot (returns List<CounterOperation>).
            // SiblingCaller's pass-through must hand back the typed list with the staged op
            // intact — verifies typed return through the sibling-bypass.
            var state = GetState();
            state.Operations.Add(new CounterOperation { CallerId = "seed", Value = seed, ClientSequence = 0, ServerTimeTicks = 0 });
            var aux = GetICounterAuxService(Context.EntityId);
            // Implicit getter routes to SiblingCaller (server) / transient impl (client). The
            // EntityCaller interface wraps the sync method as async — await unwraps.
            // Using the typed sibling-async getter keeps the original interface (sync return).
            var auxSibling = await GetICounterAuxServiceSiblingAsync();
            var ops = auxSibling.AuxSnapshotOps();
            return ops.Count;  // Should be 1 — the seeded op
        }

        public async Task<int> SiblingServerRandom(int max)
        {
            // Sibling pulls from Context.ServerRandom — server records, broadcast carries the
            // record so client replays the SAME value. If sibling-bypass leaks a different
            // recording context, client and server would observe different values → desync.
            var aux = await GetICounterAuxServiceSiblingAsync();
            return aux.AuxDrawServerRandom(max);
        }

        public async Task<long> SiblingByReference()
        {
            // Pass a CounterOperation object to a sibling. Sibling-bypass = typed C# call →
            // sibling receives the SAME REFERENCE. Sibling mutates `op.ServerTimeTicks` in
            // place. After return, the caller's `op` instance reflects the mutation.
            // If serialization were involved (cross-grain RPC), sibling would receive a
            // deserialized copy and the caller's instance would be untouched — return 0.
            var op = new CounterOperation { Value = 1, CallerId = "from-caller", ServerTimeTicks = 0 };
            var aux = await GetICounterAuxServiceSiblingAsync();
            aux.AuxMutateOpInPlace(op);
            return op.ServerTimeTicks;  // 999 if by-reference, 0 if by-value (serialized)
        }
    }
}
