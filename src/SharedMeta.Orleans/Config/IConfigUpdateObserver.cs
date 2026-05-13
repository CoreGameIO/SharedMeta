using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using SharedMeta.Core;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// Cross-silo notification target for config-version changes. One <see cref="IConfigDirectoryGrain"/>
    /// per config type holds a list of these observer references and fans out
    /// <see cref="OnConfigPublished"/> / <see cref="OnConfigUnpublished"/> on every
    /// <c>IConfigRegistry.PublishAsync</c> / <c>UnpublishAsync</c>.
    ///
    /// <para>Typical subscriber: <c>BroadcastingConfigProvider&lt;TConfig&gt;</c> — a DI singleton
    /// per silo that creates its observer reference via
    /// <c>IGrainFactory.CreateObjectReference&lt;IConfigUpdateObserver&gt;</c> at construction time.
    /// Multiple silos in the same cluster each register their own provider; the directory grain
    /// fans out to all of them. Observer references are in-memory on the directory grain (not
    /// persisted) — silo restart drops them, and the restarted silo's provider re-registers as
    /// part of its normal startup. Orleans does not guarantee at-least-once delivery for observer
    /// callbacks, so on cache miss the provider always falls back to <c>IConfigRegistry.GetAsync</c>;
    /// the observer path is a fast invalidation signal, not a durable event log.</para>
    /// </summary>
    public interface IConfigUpdateObserver : IGrainObserver
    {
        /// <summary>
        /// A version was just written to the registry (new publish or republish of an existing
        /// version's bytes). Subscribers should invalidate any cached entry for
        /// <paramref name="version"/> so the next read pulls the fresh bytes from
        /// <see cref="IConfigStoreGrain"/>.
        ///
        /// <para><c>[OneWay]</c>: the directory grain fires this as fire-and-forget — it does
        /// NOT await the observer's response. Critical for liveness: without [OneWay], a
        /// dead observer reference (silo gone away, GC'd object reference) would hang the
        /// grain's await indefinitely and block the entire fan-out for surviving observers.
        /// Mirrors the <c>ISessionObserver</c> pattern used by SessionManager.</para>
        /// </summary>
        [OneWay]
        Task OnConfigPublished(MetaConfigVersion version);

        /// <summary>
        /// A version was removed from the registry. Subscribers should drop the cached entry
        /// and the entry in their known-versions list. Re-publishing the same
        /// <paramref name="version"/> later will fire <see cref="OnConfigPublished"/>.
        /// <c>[OneWay]</c> — see <see cref="OnConfigPublished"/>.
        /// </summary>
        [OneWay]
        Task OnConfigUnpublished(MetaConfigVersion version);
    }
}
