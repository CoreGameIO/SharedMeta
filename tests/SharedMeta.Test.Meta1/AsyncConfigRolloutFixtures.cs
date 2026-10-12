using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // Fixture for a config publish reaching an active entity whose providers are async-backed:
    // their sync GetConfig only reads what GetConfigAsync already materialized (the shape of
    // BroadcastingConfigProvider). Private scope, so the entity runs pinned behind a live
    // subscriber — the case where subscribe-time warming was the only warming there was.

    [MetaConfig]
    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    public class AsyncRolloutPrimaryConfig
    {
        public int Payout { get; set; }
    }

    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    public class AsyncRolloutExtraConfig
    {
        public int Bonus { get; set; }
    }

    [SharedState]
    [MemoryPackable]
    public partial class AsyncRolloutState : ISharedState
    {
        [MemoryPackOrder(0)] public int Last { get; set; }
    }

#pragma warning disable CS0618 // ConfigType is Obsolete — the legacy primary has its own cache path and must be covered too.
    [MetaService(StateType = typeof(AsyncRolloutState), ConfigType = typeof(AsyncRolloutPrimaryConfig))]
#pragma warning restore CS0618
    [ServiceConfig(typeof(AsyncRolloutExtraConfig), "Extra")]
    public interface IAsyncRolloutService : IMetaService
    {
        /// <summary>
        /// RPC: Payout * 100 + Bonus as the server materialized them. ServerPatch so the client
        /// applies the result instead of re-running it against its own (unpublished) config.
        /// </summary>
        [MetaMethod(Mode = ExecutionMode.ServerPatch)]
        int Record();

        /// <summary>Same read through the query path.</summary>
        [MetaMethod(Mode = ExecutionMode.Query)]
        int Peek();
    }

    [MetaServiceImpl(typeof(IAsyncRolloutService), typeof(AsyncRolloutState))]
    public partial class AsyncRolloutService : IAsyncRolloutService
    {
        public int Record()
        {
            State.Last = Config.Payout * 100 + Extra.Bonus;
            return State.Last;
        }

        public int Peek() => Config.Payout * 100 + Extra.Bonus;
    }
}
