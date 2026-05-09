using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Second config type on CounterState — used by <see cref="IAltConfigService"/> to verify
    /// 0.20.0's per-service typed Config: a sibling on the same TState can declare a config
    /// type that differs from the primary <see cref="CounterConfig"/>, and the explicit
    /// <c>Get{Iface}SiblingAsync()</c> path resolves the correct typed config independently.
    /// MaxValue is intentionally distinct from CounterConfig.MaxValue (1000) so tests can
    /// assert which config was applied.
    /// </summary>
    [MetaConfig]
    public class CounterAltConfig
    {
        public int MaxValue { get; set; } = 7777;
    }
}
