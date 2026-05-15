using Microsoft.Extensions.DependencyInjection;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// DI wiring for the 0.22.0 client-signature registry. Singleton per silo —
    /// stateless aside from a per-silo lookup cache that fronts the Orleans-backed
    /// signature directory grains.
    ///
    /// <para>Typical silo wiring:</para>
    /// <code>
    /// siloBuilder.ConfigureServices(services =>
    /// {
    ///     services.AddSharedMetaClientSignatureRegistry();
    /// });
    /// </code>
    ///
    /// <para>
    /// Idempotent. Safe to call from multiple wiring entry points: the
    /// <c>TryAddSingleton</c> below is a no-op when the registry is already
    /// registered (e.g. by a parent extension method bundling several services).
    /// </para>
    /// </summary>
    public static class ClientSignatureRegistryExtensions
    {
        /// <summary>
        /// Registers <see cref="IClientSignatureRegistry"/> as a per-silo singleton backed
        /// by <see cref="ClientSignatureRegistry"/>. The registry consumes <c>IGrainFactory</c>
        /// from the silo's container — make sure the Orleans silo is itself registered
        /// before resolving the registry.
        /// </summary>
        public static IServiceCollection AddSharedMetaClientSignatureRegistry(this IServiceCollection services)
        {
            // TryAdd so a host that already wired a custom impl (e.g. with a real Stage 6
            // compute pipeline overriding ComputeCapabilities) wins.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
                .TryAddSingleton<IClientSignatureRegistry, ClientSignatureRegistry>(services);
            return services;
        }
    }
}
