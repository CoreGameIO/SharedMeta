using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core.Memory;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// In-memory implementation of <see cref="IPooledPayloadRegistryCoordinator"/>. State is
    /// not persisted — if the coordinator's host silo restarts the assignments are lost. For
    /// the test cluster and short-lived game servers this is acceptable; production deployments
    /// that need to survive coordinator failover should switch to a persistent state grain.
    /// </summary>
    public sealed class PooledPayloadRegistryCoordinatorGrain : Grain, IPooledPayloadRegistryCoordinator
    {
        private readonly Dictionary<string, byte> _assignments = new(StringComparer.Ordinal);
        private readonly HashSet<byte> _used = new();

        public Task<byte> AcquireSiloIdAsync(string siloIdentity)
        {
            if (string.IsNullOrEmpty(siloIdentity))
                throw new ArgumentException("siloIdentity must be non-empty.", nameof(siloIdentity));

            if (_assignments.TryGetValue(siloIdentity, out var existing))
                return Task.FromResult(existing);

            for (int i = 0; i < PooledPayload.MaxSilos; i++)
            {
                byte candidate = (byte)i;
                if (_used.Add(candidate))
                {
                    _assignments[siloIdentity] = candidate;
                    return Task.FromResult(candidate);
                }
            }

            throw new InvalidOperationException(
                $"PooledPayloadRegistryCoordinator exhausted the SiloId pool ({PooledPayload.MaxSilos} silos). " +
                "Increase PooledPayload.SiloIdBits or evict stale assignments.");
        }
    }
}
