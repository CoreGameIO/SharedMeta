using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClanWars.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;

namespace ClanWars.Server
{
    /// <summary>
    /// Per-silo DI singleton that fronts the cluster-wide <see cref="IClanContainerGrain"/>.
    /// Maintains an in-memory clan list for recommendations, accumulates power deltas in
    /// memory, and flushes them periodically to the grain. <c>ClanService</c> notifies this
    /// service via <c>Context.ResolveService&lt;IClanContainerService&gt;()</c> at the end
    /// of mutating server-mode operations.
    /// </summary>
    public class ClanContainerService : IClanContainerService, IHostedService, IDisposable
    {
        private readonly IGrainFactory _grainFactory;
        private readonly ILogger<ClanContainerService> _logger;
        private readonly ConcurrentDictionary<string, ClanSummary> _cache = new();
        private readonly ConcurrentDictionary<string, int> _pendingPowerDeltas = new();
        private Timer? _flushTimer;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
        private IClanContainerGrain _containerGrain;

        public ClanContainerService(IGrainFactory grainFactory, ILogger<ClanContainerService> logger)
        {
            _grainFactory = grainFactory;
            _logger = logger;
            _containerGrain = _grainFactory.GetGrain<IClanContainerGrain>("global");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Cold-start hydration: pull the full clan list from the grain.
            try
            {

                var all = await _containerGrain.GetAllAsync();
                foreach (var s in all) _cache[s.ClanId] = s;
                _logger.LogInformation("[ClanContainer] Hydrated {Count} clans from _containerGrain.", all.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ClanContainer] Hydration failed; starting with empty cache.");
            }

            // Periodic flush of accumulated power deltas.
            _flushTimer = new Timer(_ => _ = FlushPendingAsync(), null, FlushInterval, FlushInterval);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            return FlushPendingAsync();
        }

        public List<ClanSummary> GetRecommended(int limit)
        {
            // Simple "not full" recommendation. Real impl would consider mutual friends,
            // shared interests, geo-locality, etc. Returns a snapshot of the cache.
            // Synchronous: hits in-memory cache only — the grain backing this cache is
            // hydrated on silo start + kept warm by Register/Unregister/UpdateMemberCount.
            return _cache.Values
                .Where(c => c.MemberCount < 100)
                .OrderByDescending(c => c.Power)
                .Take(limit)
                .ToList();
        }

        public async Task RegisterClanAsync(ClanSummary summary)
        {
            _cache[summary.ClanId] = summary;
            // Register flushed eagerly — recommendations should see new clans without waiting
            // for the periodic flush window.

            await _containerGrain.RegisterClanAsync(summary);
        }

        public async Task UnregisterClanAsync(string clanId)
        {
            _cache.TryRemove(clanId, out _);
            _pendingPowerDeltas.TryRemove(clanId, out _);

            await _containerGrain.UnregisterClanAsync(clanId);
        }

        public ValueTask ApplyPowerDeltaAsync(string clanId, int delta)
        {
            // Apply to cache immediately (so GetRecommended sees current power ordering),
            // accumulate the delta for the periodic grain flush.
            if (_cache.TryGetValue(clanId, out var s))
                s.Power = Math.Max(0, s.Power + delta);
            _pendingPowerDeltas.AddOrUpdate(clanId, delta, (_, existing) => existing + delta);
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateMemberCountAsync(string clanId, int memberCount)
        {
            if (_cache.TryGetValue(clanId, out var s))
                s.MemberCount = memberCount;

            _containerGrain.UpdateMemberCountAsync(clanId, memberCount).Ignore();

            return ValueTask.CompletedTask;
        }

        private async Task FlushPendingAsync()
        {
            if (_pendingPowerDeltas.IsEmpty) return;
            // Snapshot + clear atomically per key so we don't lose deltas arriving during flush.
            var snapshot = new Dictionary<string, int>();
            foreach (var kv in _pendingPowerDeltas)
            {
                if (_pendingPowerDeltas.TryRemove(kv.Key, out var delta) && delta != 0)
                    snapshot[kv.Key] = delta;
            }
            if (snapshot.Count == 0) return;

            try
            {
                await _containerGrain.ApplyPowerDeltasAsync(snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ClanContainer] Flush failed; deltas dropped.");
            }
        }

        public void Dispose()
        {
            _flushTimer?.Dispose();
        }
    }
}
