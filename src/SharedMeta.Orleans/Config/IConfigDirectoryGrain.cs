using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// One grain per <c>configType.FullName</c> (grain key = the type's full name). Holds
    /// the set of currently published versions plus the in-memory list of
    /// <see cref="IConfigUpdateObserver"/> references registered by per-silo
    /// <c>BroadcastingConfigProvider&lt;TConfig&gt;</c> singletons.
    ///
    /// <para>Per-type addressing means a single Orleans cluster can host configs for any
    /// number of distinct config types — each gets its own directory grain, its own version
    /// list, and its own observer fan-out. There's no global registry grain (intentional —
    /// avoids becoming a hot single-point-of-contention as the number of types grows).</para>
    ///
    /// <para>Observer references are NOT persisted. Orleans <see cref="IGrainObserver"/>
    /// references point at a specific silo and become invalid on silo restart. On
    /// directory-grain reactivation the observer list is empty, and the next
    /// <c>BroadcastingConfigProvider</c> startup on each silo re-registers via
    /// <see cref="SubscribeAsync"/>.</para>
    /// </summary>
    public interface IConfigDirectoryGrain : IGrainWithStringKey
    {
        /// <summary>Snapshot of all currently published versions, ascending by <see cref="MetaConfigVersion"/>.</summary>
        Task<MetaConfigVersion[]> ListVersionsAsync();

        /// <summary>
        /// Record that <paramref name="version"/> has been published (or republished). If the
        /// version is new it's appended to the version list; either way every registered
        /// observer is notified via <see cref="IConfigUpdateObserver.OnConfigPublished"/>.
        /// Dead observer references (silo restart, GC, network failure) are detected lazily
        /// during fan-out and pruned.
        /// </summary>
        Task RecordPublishAsync(MetaConfigVersion version);

        /// <summary>
        /// Remove <paramref name="version"/> from the version list and notify every observer
        /// via <see cref="IConfigUpdateObserver.OnConfigUnpublished"/>. Idempotent — calling
        /// twice on the same version is a no-op for the second call.
        /// </summary>
        Task RecordUnpublishAsync(MetaConfigVersion version);

        /// <summary>
        /// Register <paramref name="observer"/> for change notifications and return the
        /// current version snapshot in the same round-trip. Callers (typically
        /// <c>BroadcastingConfigProvider&lt;TConfig&gt;</c>) use the returned snapshot to
        /// seed their known-versions list. Idempotent — re-registering the same reference
        /// is a no-op.
        /// </summary>
        Task<MetaConfigVersion[]> SubscribeAsync(IConfigUpdateObserver observer);

        /// <summary>
        /// Remove a previously-registered observer. Safe to call even when the observer was
        /// never registered (no-op).
        /// </summary>
        Task UnsubscribeAsync(IConfigUpdateObserver observer);
    }
}
