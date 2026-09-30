using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Storage provider for entity subscriber records (<see cref="EntitySubscribersState"/>).
    /// <para>
    /// Every subscribe, unsubscribe and reconnect writes this record, so it belongs in a fast store —
    /// register one under <see cref="ProviderName"/> (e.g. Redis). Unregistered, it falls back to the
    /// <c>"Default"</c> provider the entity state already uses. The record is recoverable: when it is
    /// lost, the next call from an affected player is detected and its subscription repaired.
    /// </para>
    /// </summary>
    public static class EntitySubscriptionStorage
    {
        /// <summary>Grain storage name the entity grain's subscriber record is persisted under.</summary>
        public const string ProviderName = "SharedMetaSubscriptions";

        private const string DefaultProviderName = "Default";

        /// <summary>
        /// Registers <see cref="ProviderName"/> as an alias of the <c>"Default"</c> provider unless
        /// the host registered its own. Idempotent; order-independent with the host's registration
        /// (a later keyed registration wins resolution, an earlier one makes this a no-op).
        /// </summary>
        public static IServiceCollection AddSharedMetaSubscriptionStorage(this IServiceCollection services)
        {
            services.TryAddKeyedSingleton<IGrainStorage>(ProviderName,
                (sp, _) => sp.GetRequiredKeyedService<IGrainStorage>(DefaultProviderName));
            return services;
        }
    }
}
