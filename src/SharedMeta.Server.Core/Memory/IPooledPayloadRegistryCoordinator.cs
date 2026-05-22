using System.Threading.Tasks;
using Orleans;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// Cluster-singleton grain that hands out unique <see cref="PooledPayloadRegistry.SiloId"/>
    /// values keyed by silo identity. Each silo's <c>PooledPayloadRegistryStartupTask</c> calls
    /// <see cref="AcquireSiloIdAsync"/> once on startup and pins the returned id onto its local
    /// registry, so <see cref="SharedMeta.Core.Memory.PooledPayload.Ref"/> values from different
    /// silos cannot collide on slot-index interpretation.
    /// <para>
    /// Use <c>"default"</c> as the grain key — one coordinator per cluster.
    /// </para>
    /// </summary>
    public interface IPooledPayloadRegistryCoordinator : IGrainWithStringKey
    {
        /// <summary>
        /// Return a stable SiloId in <c>[0, PooledPayload.MaxSilos)</c> for the given silo
        /// identity. Idempotent: repeated calls with the same identity return the same id.
        /// First caller per identity claims the lowest currently-unused index.
        /// </summary>
        Task<byte> AcquireSiloIdAsync(string siloIdentity);
    }
}
