using System.Threading.Tasks;
using SharedMeta.Core;

namespace Expedition.Shared
{
    // Deliberately kept on the legacy DefaultConfig — no xUnit coverage for this Unity mirror,
    // migrate together with the main Expedition.Shared copy once verified.
#pragma warning disable CS0618
    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned, DefaultConfig = true)]
#pragma warning restore CS0618
    public interface IExpeditionProfileService : IMetaService
    {
        [MetaMethod(Alias = "DummyChange", Mode = ExecutionMode.Optimistic)]
        Task<string> DummyChange();


        [MetaMethod(Alias = "BuyEnergy", Mode = ExecutionMode.Optimistic, SkipServerOnFalse = true)]
        Task<bool> BuyEnergy();

        [MetaMethod(Alias = "AddMoney", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        void AddMoney(int amount);

        /// <summary>Create a new expedition entity and return its ID.</summary>
        [MetaMethod(Alias = "StartNewExpedition", Mode = ExecutionMode.Server)]
        Task<string> StartNewExpedition();

        /// <summary>
        /// Abandon the current expedition (e.g. when the player is stuck in a dead end).
        /// Marks the expedition as complete via cross-entity call and clears the profile's
        /// CurrentExpeditionEntityId. No rewards are granted.
        /// Returns true if there was an active expedition to abandon, false otherwise.
        /// </summary>
        [MetaMethod(Alias = "AbandonExpedition", Mode = ExecutionMode.Server)]
        Task<bool> AbandonExpedition();

        [MetaMethod(Alias = "Ping", Mode = ExecutionMode.Signal)]
        void Ping(string pingValue);

        /// <summary>
        /// Send <paramref name="amount"/> coins to another player's profile entity.
        /// Spends sender's money, then cross-entity-calls <see cref="ISocialService.ReceiveGift"/>
        /// on the target. The target's client doesn't need an <c>ISocialServiceApiClient</c> —
        /// 0.14.0's foreign-service replay path applies the mutation through the shared
        /// state container, and the receiver's profile UI updates via the [Tracked] Money setter.
        /// </summary>
        [MetaMethod(Alias = "SendGift", Mode = ExecutionMode.Server)]
        Task<bool> SendGift(string targetPlayerId, int amount);

        [MetaMethod(Alias = "UpdateEnergy", Mode = ExecutionMode.Optimistic)]
        Task UpdateEnergy();
    }
}
