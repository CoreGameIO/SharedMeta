using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Test config for CounterState.
    /// Used to verify Config propagation in cross-entity calls.
    /// </summary>
    [MetaConfig(Default = true)]
    public class CounterConfig
    {
        public int MaxValue { get; set; } = 1000;
    }
}
