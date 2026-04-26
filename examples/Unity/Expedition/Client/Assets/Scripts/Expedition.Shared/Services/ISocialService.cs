using SharedMeta.Core;

namespace Expedition.Shared
{
    /// <summary>
    /// Social interactions targeting another player's <see cref="ProfileState"/>.
    /// Lives on the same state as <see cref="IExpeditionProfileService"/> — the framework
    /// allows multiple services per state since 0.14.0.
    ///
    /// The receiver's client typically does NOT subscribe to its own social service
    /// (no UI for that). When another player triggers <see cref="ReceiveGift"/> via
    /// cross-entity from <see cref="IExpeditionProfileService.SendGift"/>, the broadcast's
    /// <c>ServiceName="ISocialService"</c> doesn't match any of the receiver's locally
    /// held ApiClients. The entity-level handler in <c>MetaServiceResolver</c> picks
    /// up the slack via <c>MetaServiceConfig.EntityReplayDispatcher</c>: spins up a fresh
    /// <see cref="SocialService"/> instance on the fly and replays <see cref="ReceiveGift"/>
    /// against the shared state container. <c>State.Money</c> updates, the
    /// <c>[Tracked]</c> setter fires, the receiver's profile UI shows the new total
    /// without ever needing an <c>ISocialServiceApiClient</c>.
    /// Open access policy so any player can call into anyone else's social entity.
    /// </summary>
    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.Open)]
    public interface ISocialService : IMetaService
    {
        /// <summary>
        /// Adds <paramref name="amount"/> to the target's <c>State.Money</c>. Called
        /// cross-entity by another player's <c>ExpeditionProfileService.SendGift</c>.
        /// Server mode: broadcast carries replay-context (no state-data); foreign-service
        /// receivers reconstruct via <c>EntityReplayDispatcher</c>.
        /// </summary>
        [MetaMethod(Alias = "ReceiveGift", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void ReceiveGift(string fromPlayerId, int amount);
    }
}
