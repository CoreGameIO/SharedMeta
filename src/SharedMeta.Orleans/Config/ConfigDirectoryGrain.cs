using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// Persisted state: the set of currently published versions for one configType. Observer
    /// references are intentionally NOT in this state — they're cluster-runtime data and
    /// don't survive silo restarts.
    /// </summary>
    [GenerateSerializer]
    public class ConfigDirectoryGrainState
    {
        [Id(0)] public HashSet<MetaConfigVersion> Versions { get; set; } = new();
    }

    /// <summary>
    /// Grain implementation of <see cref="IConfigDirectoryGrain"/>. Holds the version set
    /// in persistent state; observer fan-out and dead-reference cleanup are in-memory.
    ///
    /// <para><b>Observer cleanup.</b> Orleans observer references can fail to invoke when the
    /// target silo went away. We catch per-observer exceptions during fan-out and remove the
    /// offending reference from the in-memory list. Surviving observers see the notification.
    /// This is the standard Orleans observer pattern (mirrors how SignalR / SessionManager
    /// handle subscribed clients).</para>
    /// </summary>
    public class ConfigDirectoryGrain : Grain, IConfigDirectoryGrain
    {
        private readonly IPersistentState<ConfigDirectoryGrainState> _state;
        private readonly ILogger<ConfigDirectoryGrain> _logger;
        // In-memory observer list. Order doesn't matter; we use HashSet so re-subscribe is a no-op.
        private readonly HashSet<IConfigUpdateObserver> _observers = new();

        public ConfigDirectoryGrain(
            [PersistentState("ConfigDirectory", "Default")] IPersistentState<ConfigDirectoryGrainState> state,
            ILogger<ConfigDirectoryGrain> logger)
        {
            _state = state;
            _logger = logger;
        }

        public Task<MetaConfigVersion[]> ListVersionsAsync()
            => Task.FromResult(SortedSnapshot());

        public async Task RecordPublishAsync(MetaConfigVersion version)
        {
            var added = _state.State.Versions.Add(version);
            if (added)
                await _state.WriteStateAsync();
            // Republish (added == false) still fans out — subscribers invalidate the cached
            // bytes and refetch on next read.
            await FanOutAsync(o => o.OnConfigPublished(version), "OnConfigPublished", version);
        }

        public async Task RecordUnpublishAsync(MetaConfigVersion version)
        {
            var removed = _state.State.Versions.Remove(version);
            if (!removed) return; // idempotent — nothing to broadcast
            await _state.WriteStateAsync();
            await FanOutAsync(o => o.OnConfigUnpublished(version), "OnConfigUnpublished", version);
        }

        public Task<MetaConfigVersion[]> SubscribeAsync(IConfigUpdateObserver observer)
        {
            _observers.Add(observer); // HashSet → idempotent re-register
            return Task.FromResult(SortedSnapshot());
        }

        public Task UnsubscribeAsync(IConfigUpdateObserver observer)
        {
            _observers.Remove(observer);
            return Task.CompletedTask;
        }

        private MetaConfigVersion[] SortedSnapshot()
            // MetaConfigVersion has comparison operators but does not implement IComparable;
            // OrderBy chain is the portable way to sort by (Major, Minor, Patch).
            => _state.State.Versions
                .OrderBy(v => v.Major).ThenBy(v => v.Minor).ThenBy(v => v.Patch)
                .ToArray();

        /// <summary>
        /// Fire <paramref name="notify"/> on every observer, collecting dead refs (those whose
        /// invocation threw) and removing them at the end. We don't fail the entire publish
        /// when one observer is dead — silently pruning is the documented behaviour.
        /// </summary>
        private async Task FanOutAsync(
            Func<IConfigUpdateObserver, Task> notify, string callbackName, MetaConfigVersion version)
        {
            if (_observers.Count == 0) return;
            List<IConfigUpdateObserver>? dead = null;
            // Snapshot the current observer list — notify callbacks can take time and we
            // don't want to hold the iterator while concurrent subscribe/unsubscribe runs
            // (single-threaded grain semantics mean we can't actually race here, but copying
            // is cheap and protects against future changes to grain reentrancy).
            var snapshot = _observers.ToArray();
            foreach (var observer in snapshot)
            {
                try { await notify(observer); }
                catch (Exception ex)
                {
                    (dead ??= new List<IConfigUpdateObserver>()).Add(observer);
                    _logger.LogDebug(ex,
                        "[ConfigDirectory:{Type}] {Callback}(v={Version}) failed for observer — pruning",
                        this.GetPrimaryKeyString(), callbackName, version);
                }
            }
            if (dead != null)
                foreach (var d in dead) _observers.Remove(d);
        }
    }
}
