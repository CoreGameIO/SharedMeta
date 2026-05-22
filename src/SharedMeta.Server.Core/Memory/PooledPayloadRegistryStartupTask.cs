using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// Orleans startup task that binds the local <see cref="PooledPayloadRegistry"/> to a
    /// unique SiloId allocated by the cluster-wide <see cref="IPooledPayloadRegistryCoordinator"/>.
    /// <para>
    /// Registered on the silo via <c>siloBuilder.AddStartupTask&lt;PooledPayloadRegistryStartupTask&gt;()</c>.
    /// Runs once during silo startup, BEFORE any grain activations that consume the registry —
    /// so by the time an EntityGrain reaches <c>PackBroadcastVariant</c>, <see cref="PooledPayloadRegistry.SiloId"/>
    /// is bound and <c>PooledPayload.Ref</c> values correctly identify their source silo.
    /// </para>
    /// </summary>
    public sealed class PooledPayloadRegistryStartupTask : IStartupTask
    {
        private readonly PooledPayloadRegistry _registry;
        private readonly IGrainFactory _grainFactory;
        private readonly ILocalSiloDetails _siloDetails;

        public PooledPayloadRegistryStartupTask(
            PooledPayloadRegistry registry,
            IGrainFactory grainFactory,
            ILocalSiloDetails siloDetails)
        {
            _registry = registry;
            _grainFactory = grainFactory;
            _siloDetails = siloDetails;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            // CreatePinned path is already initialized — skip the coordinator call so non-Orleans
            // hosts that wired the registry without this task aren't disturbed.
            if (_registry.IsInitialized) return;

            var coordinator = _grainFactory.GetGrain<IPooledPayloadRegistryCoordinator>("default");
            var identity = _siloDetails.SiloAddress.ToString();
            var siloId = await coordinator.AcquireSiloIdAsync(identity);
            _registry.SetSiloId(siloId);
        }
    }
}
