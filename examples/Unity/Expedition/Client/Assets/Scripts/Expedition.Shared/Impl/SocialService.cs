using SharedMeta.Core;
using SharedMeta.Core.Logging;

namespace Expedition.Shared
{
    [MetaServiceImpl(typeof(ISocialService), typeof(ProfileState))]
    public partial class SocialService : ISocialService
    {
        public void ReceiveGift(string fromPlayerId, int amount)
        {
            // Mutates the [Tracked] _money backing field through its generated setter.
            // When this runs as a foreign-service replay on a receiver that doesn't hold
            // an ISocialServiceApiClient, the entity-level handler in MetaServiceResolver
            // dispatches here via MetaServiceConfig.EntityReplayDispatcher; the setter
            // still fires the Tracked subscription, so the profile UI updates immediately.
            MetaLog.Info($"{Context.EntityId} received gift with {amount} coins from the {fromPlayerId}");
            Context.State.Money += amount;
        }
    }
}
