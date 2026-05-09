using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// Sibling service on CounterState with a non-primary config type. Verifies multi-config
    /// sibling support — <see cref="ICounterService"/> uses CounterConfig (DefaultConfig=true),
    /// this service uses <see cref="CounterAltConfig"/>. The async sibling getter resolves
    /// each service's typed Config independently through their own IMetaConfigProvider.
    /// </summary>
    [MetaService(StateType = typeof(CounterState), ConfigType = typeof(CounterAltConfig))]
    public interface IAltConfigService : IMetaService
    {
        /// <summary>
        /// Reads CounterAltConfig.MaxValue and writes it to State.Sum so callers can verify
        /// (via state.Sum) that the impl saw the alt config — NOT the caller's primary config.
        /// </summary>
        [MetaMethod(Alias = "WriteAltMaxToSum", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void WriteAltMaxToSum();
    }
}
