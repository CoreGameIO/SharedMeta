using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaServiceImpl(typeof(IAltConfigService), typeof(CounterState))]
    public partial class AltConfigService : IAltConfigService
    {
        public void WriteAltMaxToSum()
        {
            // Reads typed Config — generated as `_serviceConfig_Config ?? (CounterAltConfig)Context.Configs![0]!`.
            // When invoked through Get{Iface}SiblingAsync(), the provider sets
            // `_serviceConfig_Config` through the Config setter, so this method sees
            // CounterAltConfig.MaxValue regardless of what config the caller's outer service
            // uses (CounterConfig). The test asserts state.Sum == CounterAltConfig.MaxValue
            // (7777, distinct from CounterConfig.MaxValue=1000) — confirming per-service
            // typed Config.
            Context.State.Sum = Config.MaxValue;
        }
    }
}
