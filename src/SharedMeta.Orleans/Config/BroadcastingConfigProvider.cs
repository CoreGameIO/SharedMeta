using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Config;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// Server-side <see cref="IMetaConfigProvider{TConfig}"/> backed by
    /// <see cref="IConfigRegistry"/> with an in-memory cache and live invalidation. One
    /// instance per silo per config type — register as a DI singleton.
    ///
    /// <para><b>Lifecycle.</b> Construction is cheap (no I/O). Call <see cref="InitializeAsync"/>
    /// before serving traffic — it subscribes to the per-type
    /// <see cref="IConfigDirectoryGrain"/> as a <see cref="IConfigUpdateObserver"/>, seeds
    /// the known-versions list from the directory's snapshot, and unmarks the "not yet
    /// initialized" guard. <see cref="DisposeAsync"/> unsubscribes — call it on silo shutdown
    /// so the directory grain doesn't accumulate dead observer refs (it would prune them
    /// lazily on next fan-out anyway, but explicit unsubscribe is cheaper).</para>
    ///
    /// <para><b>Concurrency model.</b> Read-heavy workload — every entity activation calls
    /// <see cref="GetConfigAsync"/> / <see cref="ResolveForClient"/> once or more per call.
    /// Writes are rare (publish/unpublish events arrive from the directory grain at
    /// human-administered cadence). The implementation is lock-free for reads: the cache is
    /// a <see cref="ConcurrentDictionary{TKey,TValue}"/>; the known-versions snapshot is
    /// stored as an immutable <see cref="ImmutableSortedSet{T}"/> swapped via
    /// <c>Interlocked.CompareExchange</c>. Readers grab the latest snapshot with
    /// <c>Volatile.Read</c> and never block each other, nor block writers — and writers
    /// (observer callbacks, lazy fetches) use a CAS loop so concurrent mutations linearize
    /// without taking a mutex.</para>
    ///
    /// <para><b>Multi-silo.</b> Every silo in the cluster runs its own provider per
    /// <typeparamref name="TConfig"/> and registers as a separate observer. The directory
    /// grain fans out to all of them on publish/unpublish. Orleans observer references are
    /// in-memory only on the directory grain — silo restarts re-register on
    /// <see cref="InitializeAsync"/>. Observer callbacks are at-most-once (best effort);
    /// the cache-miss path always falls back to <see cref="IConfigRegistry.GetAsync"/>
    /// so a dropped notification cannot cause stale serving — at worst the cache is colder.</para>
    ///
    /// <para><b>0.21.0:</b> The pre-0.21.0 <c>CurrentVersion</c> property and
    /// <c>SetCurrentVersion</c> mutator have been removed. The framework no longer reads a
    /// "default" version from the provider — every call resolves the version explicitly from
    /// the caller's client app version (real or substituted via
    /// <see cref="IConfigVersionResolver.CurrentClientVersion"/> for server-internal callers).
    /// The provider's job is now strictly to materialize bytes for a specific requested
    /// version.</para>
    /// </summary>
    public class BroadcastingConfigProvider<TConfig> : IMetaConfigProvider<TConfig>, IConfigUpdateObserver, IAsyncDisposable
        where TConfig : class
    {
        private readonly IConfigRegistry _registry;
        private readonly IMetaSerializer _serializer;
        private readonly IGrainFactory _grains;
        private readonly ILogger _logger;
        private readonly MetaConfigVersionResolver? _clientResolver;

        // Cache of materialized configs. ConcurrentDictionary gives lock-free reads and
        // fine-grained per-bucket writes — concurrent GetConfigAsync calls don't serialize
        // on a single mutex, only on cache misses for the same key (and even then they
        // produce idempotent duplicate fetches, no corruption).
        private readonly ConcurrentDictionary<MetaConfigVersion, TConfig> _cache = new();

        // Known versions snapshot — immutable, swapped atomically via Interlocked.CompareExchange.
        // Readers do Volatile.Read and never take a lock; writers (observer callbacks, lazy
        // fetches) run a tiny CAS loop via Mutate.
        private ImmutableSortedSet<MetaConfigVersion> _knownVersions =
            ImmutableSortedSet<MetaConfigVersion>.Empty.WithComparer(MetaConfigVersionComparer.Instance);


        // 0 = not initialized, 1 = initialized. CAS via Interlocked.CompareExchange in
        // InitializeAsync — preserves idempotency without taking a lock.
        private int _initialized;

        private IConfigUpdateObserver? _observerReference;

        public BroadcastingConfigProvider(
            IConfigRegistry registry,
            IMetaSerializer serializer,
            IGrainFactory grains,
            ILogger<BroadcastingConfigProvider<TConfig>>? logger = null,
            MetaConfigVersionResolver? clientResolver = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _grains = grains ?? throw new ArgumentNullException(nameof(grains));
            _logger = (ILogger?)logger ?? NullLogger.Instance;
            // Pull [MetaConfigVersion] rules off the config class once at construction so
            // ResolveForClient / ResolveLatestMatching don't re-reflect on every call.
            _clientResolver = clientResolver ?? MetaConfigVersionResolver.ForType(typeof(TConfig));
        }

        /// <summary>
        /// Subscribe to the per-type directory grain and seed the known-versions list.
        /// Idempotent — calling twice is a no-op for the second call.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
            _observerReference = _grains.CreateObjectReference<IConfigUpdateObserver>(this);
            var directory = _grains.GetGrain<IConfigDirectoryGrain>(typeof(TConfig).FullName!);
            var snapshot = await directory.SubscribeAsync(_observerReference);

            // Seed known versions atomically. Snapshot is small (single-digit element count
            // in typical product use), so we build the new state inline.
            Mutate(known =>
            {
                foreach (var v in snapshot) known = known.Add(v);
                return known;
            });

            _logger.LogInformation(
                "[BroadcastingConfigProvider:{Type}] initialized with {Count} known version(s)",
                typeof(TConfig).Name, snapshot.Length);
        }

        public async ValueTask DisposeAsync()
        {
            if (_observerReference == null) return;
            try
            {
                var directory = _grains.GetGrain<IConfigDirectoryGrain>(typeof(TConfig).FullName!);
                await directory.UnsubscribeAsync(_observerReference);
            }
            catch (Exception ex)
            {
                // Cluster might already be tearing down — swallow, the grain will prune dead
                // refs lazily anyway.
                _logger.LogDebug(ex, "[BroadcastingConfigProvider:{Type}] unsubscribe failed on dispose", typeof(TConfig).Name);
            }
            _observerReference = null;
        }

        // ── IConfigUpdateObserver ──────────────────────────────────────────────────────

        public Task OnConfigPublished(MetaConfigVersion version)
        {
            // Drop the cached entry so the next GetConfigAsync refetches; add to known set
            // so ResolveLatestMatching reflects the new version immediately.
            _cache.TryRemove(version, out _);
            Mutate(known => known.Add(version));
            return Task.CompletedTask;
        }

        public Task OnConfigUnpublished(MetaConfigVersion version)
        {
            _cache.TryRemove(version, out _);
            Mutate(known => known.Remove(version));
            return Task.CompletedTask;
        }

        // ── IMetaConfigProvider<TConfig> ───────────────────────────────────────────────

        /// <summary>
        /// Synchronous read. Throws on cache miss — there's no way to fetch bytes
        /// synchronously across a grain call. Code paths where a cache miss is expected
        /// should use <see cref="GetConfigAsync"/> instead. The 0.21.0 framework calls
        /// the async path during entity activation (<c>InitializeConfigAsync</c>); sync
        /// <see cref="GetConfig"/> is reserved for already-warm reads after that point.
        /// </summary>
        public TConfig GetConfig(MetaConfigVersion version)
        {
            if (_cache.TryGetValue(version, out var cached)) return cached;
            throw new InvalidOperationException(
                $"[BroadcastingConfigProvider:{typeof(TConfig).Name}] no cached config for {version}. " +
                $"Call GetConfigAsync first or invoke via the framework's async activation path (InitializeConfigAsync).");
        }

        public async Task<TConfig> GetConfigAsync(MetaConfigVersion version)
        {
            if (_cache.TryGetValue(version, out var cached)) return cached;

            var bytes = await _registry.GetAsync(typeof(TConfig), version)
                ?? throw new InvalidOperationException(
                    $"[BroadcastingConfigProvider:{typeof(TConfig).Name}] version {version} is not published in the registry. " +
                    $"Available versions: {AvailableVersionsForDiagnostics()}");

            var config = _serializer.Unpack<TConfig>(bytes);

            // Race-tolerant: another caller may have populated the same key. ConcurrentDictionary
            // TryAdd / GetOrAdd both return the existing entry on collision, but we only need
            // the final cached value to be deterministic — _serializer.Unpack on the same
            // bytes produces an equivalent object, so either winner is fine.
            var stored = _cache.GetOrAdd(version, config);
            // Reflect the version in the known set even if the observer hasn't fired yet —
            // covers cold-start where the provider fetched bytes before its observer
            // subscription delivered the notification.
            Mutate(known => known.Contains(version) ? known : known.Add(version));
            return stored;
        }

        /// <summary>
        /// Return the highest known patch for the given <c>Major.Minor</c> branch. Throws
        /// when no patch in that branch has been published — there is no "default" fallback
        /// in 0.21.0+. Callers (typically <see cref="ResolveForClient"/>) are expected to
        /// route only versions they know are supported.
        /// </summary>
        public MetaConfigVersion ResolveLatestMatching(int major, int minor)
        {
            var known = Volatile.Read(ref _knownVersions);
            MetaConfigVersion best = default;
            bool found = false;
            foreach (var v in known)
            {
                if (v.Major != major || v.Minor != minor) continue;
                if (!found || v.Patch > best.Patch) { best = v; found = true; }
            }
            if (!found)
                throw new InvalidOperationException(
                    $"[BroadcastingConfigProvider:{typeof(TConfig).Name}] no published version in branch {major}.{minor}. " +
                    $"Available versions: {AvailableVersionsForDiagnostics()}");
            return best;
        }

        public MetaConfigVersion ResolveForClient(string? clientAppVersion, MetaConfigVersionResolver? resolver)
        {
            // 0.21.0+: strict — null/empty clientAppVersion throws. Server-internal callers
            // must substitute IConfigVersionResolver.CurrentClientVersion before calling;
            // EntityScope.Global entities do this inside the framework's per-call dispatch.
            if (string.IsNullOrEmpty(clientAppVersion))
                throw new InvalidOperationException(
                    $"[BroadcastingConfigProvider:{typeof(TConfig).Name}] ResolveForClient: clientAppVersion is required. " +
                    $"Server-internal callers must substitute IConfigVersionResolver.CurrentClientVersion before calling.");

            // Prefer the resolver passed in by the framework; fall back to the one captured at
            // construction (the [MetaConfigVersion] rules on TConfig).
            resolver ??= _clientResolver;
            if (resolver == null)
                return default;

            var request = resolver.Resolve(clientAppVersion!);
            if (request == null)
                return default;

            return request.PatchIsLatest
                ? ResolveLatestMatching(request.Major, request.Minor)
                : new MetaConfigVersion(request.Major, request.Minor, request.PatchMin);
        }

        // ── CAS plumbing ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply <paramref name="mutator"/> to the immutable known-versions set via a CAS
        /// loop until it commits without contention. Tail recursion not needed — the loop is
        /// bounded by concurrent writer count (in practice ≤ 1 in steady state; bursts on init).
        /// </summary>
        private void Mutate(Func<ImmutableSortedSet<MetaConfigVersion>, ImmutableSortedSet<MetaConfigVersion>> mutator)
        {
            ImmutableSortedSet<MetaConfigVersion> current, updated;
            do
            {
                current = Volatile.Read(ref _knownVersions);
                updated = mutator(current);
                if (ReferenceEquals(current, updated)) return; // no-op mutator (e.g. duplicate add)
            }
            while (Interlocked.CompareExchange(ref _knownVersions, updated, current) != current);
        }

        private string AvailableVersionsForDiagnostics()
        {
            var known = Volatile.Read(ref _knownVersions);
            if (known.Count == 0) return "(none)";
            return string.Join(", ", known.Select(v => $"{v.Major}.{v.Minor}.{v.Patch}"));
        }

        private sealed class MetaConfigVersionComparer : IComparer<MetaConfigVersion>
        {
            public static readonly MetaConfigVersionComparer Instance = new();
            public int Compare(MetaConfigVersion x, MetaConfigVersion y)
            {
                int c = x.Major.CompareTo(y.Major); if (c != 0) return c;
                c = x.Minor.CompareTo(y.Minor); if (c != 0) return c;
                return x.Patch.CompareTo(y.Patch);
            }
        }
    }
}
