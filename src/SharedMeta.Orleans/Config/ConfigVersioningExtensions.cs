using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Config;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// DI helpers for the 0.21.0 versioned config subsystem.
    ///
    /// <para>Wiring example on the silo host:</para>
    /// <code>
    /// siloBuilder.ConfigureServices(services =>
    /// {
    ///     services.AddSharedMetaConfigVersioning();
    ///     services.AddSharedMetaConfigProvider&lt;ExpeditionConfig&gt;();
    ///     services.AddSharedMetaConfigProvider&lt;ProfileConfig&gt;();
    /// });
    /// </code>
    ///
    /// <para>Initialization (subscribe to the directory grain, seed the cache) is opt-in via
    /// <see cref="WarmUpConfigProvidersAsync"/> at startup — until that runs, the provider
    /// is alive but has not yet established its observer subscription and known-versions
    /// list is empty. Either call WarmUp explicitly or rely on lazy initialization
    /// (BroadcastingConfigProvider.GetConfigAsync will hit the registry on first call;
    /// observer subscription happens at WarmUp time or when first manually triggered).</para>
    /// </summary>
    public static class ConfigVersioningExtensions
    {
        /// <summary>
        /// Register the registry façade — one DI singleton per silo, stateless, delegates
        /// to per-(type, version) and per-type grains. Idempotent; safe to call from
        /// multiple wiring entry points.
        /// </summary>
        public static IServiceCollection AddSharedMetaConfigVersioning(this IServiceCollection services)
        {
            services.AddSingleton<IConfigRegistry, GrainConfigRegistry>();
            return services;
        }

        /// <summary>
        /// Register a <see cref="BroadcastingConfigProvider{TConfig}"/> for the given config
        /// type and expose it both as the concrete type (for direct access) and as
        /// <see cref="IMetaConfigProvider{TConfig}"/> (for the framework's auto-discovery path).
        /// </summary>
        public static IServiceCollection AddSharedMetaConfigProvider<TConfig>(this IServiceCollection services)
            where TConfig : class
        {
            services.AddSingleton<BroadcastingConfigProvider<TConfig>>();
            services.AddSingleton<IMetaConfigProvider<TConfig>>(
                sp => sp.GetRequiredService<BroadcastingConfigProvider<TConfig>>());
            return services;
        }

        /// <summary>
        /// Subscribe every registered <see cref="BroadcastingConfigProvider{TConfig}"/> to its
        /// directory grain and pull the initial known-versions snapshot. Call once during
        /// silo startup — for example from <c>IHostedService.StartAsync</c> or an
        /// <c>ISiloLifecycleParticipant</c> hook — before the silo begins serving traffic.
        /// Without this call, providers initialize lazily on first <c>GetConfigAsync</c>.
        /// </summary>
        public static async Task WarmUpConfigProvidersAsync(this System.IServiceProvider serviceProvider, params System.Type[] configTypes)
        {
            foreach (var configType in configTypes)
            {
                // Resolve as BroadcastingConfigProvider<TConfig> via reflection — we have only
                // the open type at compile time. The DI container has the closed registration
                // from AddSharedMetaConfigProvider<TConfig>.
                var closed = typeof(BroadcastingConfigProvider<>).MakeGenericType(configType);
                var instance = serviceProvider.GetService(closed);
                if (instance == null) continue; // not registered — nothing to warm up
                var initMethod = closed.GetMethod(nameof(BroadcastingConfigProvider<object>.InitializeAsync))!;
                await (Task)initMethod.Invoke(instance, new object?[] { default(System.Threading.CancellationToken) })!;
            }
        }
    }
}
