using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // Fixture for live config rollout against an entity that stays active.
    //
    // EntityScope.Global on purpose: Private/Shared entities freeze their config version behind a
    // pin for as long as they have subscribers, and that freeze is the documented contract for a
    // live session. Global never pins — it resolves freshly against
    // IConfigVersionResolver.CurrentClientVersion on every cache miss — so it is the scope where a
    // published patch is genuinely expected to land, and the scope where the grain-side
    // materialized-config cache was silently swallowing it.

    /// <summary>Config whose payload an admin can republish at runtime.</summary>
    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    public class RolloutConfig
    {
        public int Payout { get; set; }
    }

    [SharedState]
    [EntityScope(EntityScope.Global)]
    [MemoryPackable]
    public partial class RolloutState : ISharedState
    {
        [MemoryPackOrder(0)] public int Total { get; set; }
    }

    [MetaService(StateType = typeof(RolloutState))]
    [ServiceConfig(typeof(RolloutConfig), "Config")]
    public interface IRolloutService : IMetaService
    {
        /// <summary>
        /// Query mode: server-authoritative, no client replay, so the assertion is purely about
        /// which config instance the *server* materialized. Reads the same
        /// <c>GetCachedServiceConfigsForClient</c> cache the RPC path uses.
        /// </summary>
        [MetaMethod(Mode = ExecutionMode.Query)]
        int ReadPayout();
    }

    [MetaServiceImpl(typeof(IRolloutService), typeof(RolloutState))]
    public partial class RolloutService : IRolloutService
    {
        [MetaInit]
        public Task<int> Init(int version) => Task.FromResult(version);

        public int ReadPayout() => Config.Payout;
    }
}
