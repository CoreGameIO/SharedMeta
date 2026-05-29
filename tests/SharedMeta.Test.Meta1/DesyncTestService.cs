using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaServiceImpl(typeof(IDesyncTestService), typeof(DesyncTestState), DeepDesync = true)]
    public partial class DesyncTestService : IDesyncTestService
    {
        private DesyncTestState state => State;

        public int Add(int amount)
        {
            state.Value += amount;
            state.Counter++;
            return state.Value;
        }

        public void SetLabel(string label)
        {
            state.Label = label;
        }

        /// <summary>
        /// Intentionally non-deterministic — uses System.Random.
        /// Server and client will compute different random values,
        /// causing state.Value to diverge. The return value is always 0
        /// so result-level desync detection won't catch it — only
        /// patch-level (deep desync) will.
        /// </summary>
        public int AddNonDeterministic()
        {
            // System.Random is non-deterministic across platforms!
            // Should use Context.Random instead.
            var seed = Context.IsServer ? 234352323 + DateTime.UtcNow.Second : 879842398 + DateTime.UtcNow.Second;
            var rng = new System.Random(seed);
            state.Value += rng.Next(1, 10000);
            state.Counter++;
            return 0; // always returns 0 — result comparison passes
        }
    }
}
