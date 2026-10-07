using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Storage;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Storage provider for the session manager's resume record (<see cref="SessionManagerGrainState"/>).
    /// <para>
    /// The record is disposable: without it the client gets <c>SessionUnknown</c> and runs a full
    /// reconnect. Register a separate provider under <see cref="ProviderName"/> to keep it apart from
    /// game state (in-memory, or a store that can be wiped). Unregistered, it falls back to the
    /// <c>"Default"</c> provider.
    /// </para>
    /// </summary>
    public static class SessionStorage
    {
        /// <summary>Grain storage name the session manager's record is persisted under.</summary>
        public const string ProviderName = "SharedMetaSessions";

        /// <summary>
        /// Key of the record inside the provider. Change it whenever <see cref="SessionManagerGrainState"/>
        /// or anything it persists (e.g. <c>SessionResponse</c>) changes incompatibly — records under the
        /// old key are then ignored instead of failing grain activation.
        /// </summary>
        public const string StateName = "sessionMgr2";

        private const string DefaultProviderName = "Default";

        /// <summary>
        /// Registers <see cref="ProviderName"/> as an alias of the <c>"Default"</c> provider unless
        /// the host registered its own. Idempotent; order-independent with the host's registration
        /// (a later keyed registration wins resolution, an earlier one makes this a no-op).
        /// </summary>
        public static IServiceCollection AddSharedMetaSessionStorage(this IServiceCollection services)
        {
            services.TryAddKeyedSingleton<IGrainStorage>(ProviderName,
                (sp, _) => sp.GetRequiredKeyedService<IGrainStorage>(DefaultProviderName));
            return services;
        }
    }
}
