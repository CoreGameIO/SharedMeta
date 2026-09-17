using System.Collections.Generic;
using SharedMeta.Core.Transport;

namespace SharedMeta.Client
{
    /// <summary>
    /// 0.24.0+ Client-side cache of <see cref="ClientSignatureAnnotated"/> entries, keyed by
    /// the client's own <see cref="ClientSignatureAnnotated.ClientSignatureHash"/>.
    /// <para>
    /// <b>Usage flow:</b> on every <c>SessionConnect</c> the server returns its current
    /// <c>ServerSignatureHash</c>. The client looks up its cached entry for the current
    /// <c>ClientSignatureHash</c> and compares the cached entry's
    /// <see cref="ClientSignatureAnnotated.ServerSignatureHash"/> to the one just returned —
    /// match means the cached annotation is still authoritative (HIT, no phase-2 needed),
    /// mismatch means the server redeployed and the entry must be re-fetched via phase-2.
    /// </para>
    /// <para>
    /// Implementations: <see cref="InMemoryServerAnnotationCache"/> for non-Unity / tests;
    /// Unity hosts use <c>PlayerPrefsServerAnnotationCache</c> (added separately under the
    /// Unity asmdef) so the cache survives app launches and a freshly-installed app pays the
    /// phase-2 cost only once per <c>(clientHash × serverHash)</c> pair.
    /// </para>
    /// </summary>
    public interface IServerAnnotationCache
    {
        /// <summary>
        /// Return the cached entry for <paramref name="clientSignatureHash"/>, or <c>null</c>
        /// when none exists. Callers compare <see cref="ClientSignatureAnnotated.ServerSignatureHash"/>
        /// against the freshly-returned server hash to decide HIT vs MISS.
        /// </summary>
        ClientSignatureAnnotated? TryGet(ulong clientSignatureHash);

        /// <summary>
        /// Insert or replace the cached entry under
        /// <paramref name="annotated"/>'s <see cref="ClientSignatureAnnotated.ClientSignatureHash"/>.
        /// Implementations may persist asynchronously, but the in-memory side of the cache
        /// must be observable to subsequent <see cref="TryGet"/> calls before this method
        /// returns.
        /// </summary>
        void Set(ClientSignatureAnnotated annotated);

        /// <summary>
        /// Drop the entry for <paramref name="clientSignatureHash"/>. No-op when no entry
        /// exists. Used when the cached entry's <c>ServerSignatureHash</c> diverged from the
        /// server's current hash — the stale entry is invalidated before phase-2 fetches the
        /// fresh one.
        /// </summary>
        void Invalidate(ulong clientSignatureHash);
    }

    /// <summary>
    /// In-process cache backed by a plain dictionary. Default for non-Unity hosts and tests.
    /// Thread-safe via lock — annotation lookups happen at most once per connect, so the lock
    /// is not on a hot path.
    /// </summary>
    public sealed class InMemoryServerAnnotationCache : IServerAnnotationCache
    {
        private readonly Dictionary<ulong, ClientSignatureAnnotated> _entries = new();
        private readonly object _gate = new();

        public ClientSignatureAnnotated? TryGet(ulong clientSignatureHash)
        {
            lock (_gate)
                return _entries.TryGetValue(clientSignatureHash, out var v) ? v : null;
        }

        public void Set(ClientSignatureAnnotated annotated)
        {
            if (annotated == null) return;
            lock (_gate)
                _entries[annotated.ClientSignatureHash] = annotated;
        }

        public void Invalidate(ulong clientSignatureHash)
        {
            lock (_gate)
                _entries.Remove(clientSignatureHash);
        }
    }
}
