using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // 0.20.0: declares ICounterService as dep so the generator emits a typed
    // GetICounterService(entityId) accessor on this impl partial. The helper classes
    // ({Iface}EntityCaller / Recorder / Replayer / LocalEntityCaller / SiblingCaller)
    // are deduped across consumers in the same namespace by ContextInjectionGenerator's
    // primary-emitter rule (alphabetically-first impl wins emission).
    [MetaServiceImpl(typeof(ICounterAuxService), typeof(CounterState), typeof(ICounterService))]
    public partial class CounterAuxService : ICounterAuxService
    {
        public int AuxAdd(int value)
        {
            Context.State.Sum += value;
            Context.State.Operations.Add(new CounterOperation
            {
                CallerId = Context.CallerId ?? "",
                Value = value,
                ClientSequence = -1,
                ServerTimeTicks = Context.ServerTimeTicks
            });
            return (int)Context.State.Sum;
        }

        public void AuxBumpReactive()
        {
            // Touch the [Tracked] field via its generated public setter so reactive subscribers fire.
            Context.State.ReactiveCounter = Context.State.ReactiveCounter + 1;
        }

        public void MutateTracked(int delta)
        {
            // Mutating ReactiveCounter through the generated [Tracked] setter — sibling-call
            // path must let the outer's ChangeTracker pick up the notification.
            Context.State.ReactiveCounter = Context.State.ReactiveCounter + delta;
        }

        public int DrawNamedCombat(int max)
        {
            // Pull from the Combat NamedRandom stream — caller verifies the scroll advanced.
            return CombatRandom.Next(max);
        }

        public Task<int> CallBackToCounter(int value)
        {
            // Recursive sibling: AuxService → CounterService via runtime sibling-resolver.
            // Works on server (resolver = provider.ResolveSiblingByType) and on client (resolver
            // = ICrossEntityResolver.ResolveSibling, which produces a transient impl bound to
            // our ClientMetaContext via the generator-emitted ClientSiblingFactory). Targets
            // AddValue (no Config read) so the test doesn't depend on a CounterConfig provider
            // being registered in the test fixture's DI.
            var counter = Context.SiblingServiceResolver?.Invoke(typeof(ICounterService)) as ICounterService;
            if (counter == null)
                throw new System.InvalidOperationException("ICounterService sibling not resolved");
            counter.AddValue(value, -2);
            return Task.FromResult((int)Context.State.Sum);
        }

        public async Task<int> AuxAddViaOther(string otherEntityId, int value)
        {
            // Real cross-entity from inside a sibling-call via typed Get-accessor — the
            // generator-emitted GetICounterService(entityId) on this impl handles both
            // server-side recorder (real grain-RPC) and client-side replayer (broadcast
            // result reads) paths transparently. Sibling-bypass MUST NOT short-circuit
            // (otherEntityId != self), so this hits real cross-grain RPC on server.
            var counter = GetICounterService(otherEntityId);
            await counter.AddValueAsync(value, -3);
            return value;
        }

        public int AuxThrowAfterMutate(int value)
        {
            // Mutate state, THEN throw. Test verifies outer's catch sees the throw and the
            // partial mutation isn't silently observed by external clients via broadcast.
            Context.State.Sum += value;
            throw new System.InvalidOperationException("AuxThrowAfterMutate: simulated failure after partial mutation");
        }

        public System.Collections.Generic.List<CounterOperation> AuxSnapshotOps()
        {
            // Returns a copy of operations — SiblingCaller's typed pass-through MUST hand back
            // the same list reference (no serialization in sibling path) so caller sees the
            // same instance the impl returned.
            return new System.Collections.Generic.List<CounterOperation>(Context.State.Operations);
        }

        public int AuxDrawServerRandom(int max)
        {
            // ServerRandom advances on server; client-side replay reads the recorded value
            // from the replay payload. Sibling-bypass shares the outer's MetaContext so the
            // outer's recording captures these advances correctly.
            return Context.ServerRandom!.Next(max);
        }

        public int AuxSumUnqualified(System.Collections.Generic.List<int> values)
        {
            int total = 0;
            foreach (int value in values)
            {
                total += value;
            }

            Context.State.Sum += total;
            return (int)Context.State.Sum;
        }

        public void AuxMutateOpInPlace(CounterOperation op)
        {
            // Mutate caller's instance directly. Sibling-bypass passes by reference (typed
            // in-process call), so the mutation is visible to the caller. A real cross-grain
            // RPC would serialize the arg → caller's instance would NOT see the mutation.
            op.ServerTimeTicks = 999;
            op.CallerId = "modified-by-sibling";
        }
    }
}
