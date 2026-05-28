using System.Threading.Tasks;
using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Cluster-wide directory of known client-signature hashes. Single activation
    /// (key <c>"global"</c>) shared across all silos.
    /// <para>
    /// Purpose: cheap "have we seen this signature before?" check that
    /// <see cref="IClientSignatureRegistry"/> falls back to when its silo-local cache
    /// misses. Avoids spinning up a per-signature <see cref="IClientSignatureGrain"/>
    /// activation just to discover that the hash has never been registered.
    /// </para>
    /// <para>
    /// Read-heavy / write-rare — new signatures register at most once per distinct
    /// client build seen by the cluster, then all subsequent connections from that
    /// build are pure reads. Single-threaded grain semantics are fine; no
    /// <c>[AlwaysInterleave]</c>.
    /// </para>
    /// </summary>
    public interface IClientSignatureManagerGrain : IGrainWithStringKey
    {
        /// <summary>
        /// True if any silo has previously registered this signature hash. Cheap directory
        /// lookup — the actual <see cref="SharedMeta.Core.Transport.MetaClientSignature"/>
        /// payload lives in the per-hash <see cref="IClientSignatureGrain"/>.
        /// </summary>
        Task<bool> IsKnownAsync(ulong signatureHash);

        /// <summary>
        /// Mark a signature hash as known to the cluster. Called by
        /// <see cref="IClientSignatureRegistry"/> immediately after it sets the matching
        /// per-hash grain in phase-2 of the handshake. Idempotent.
        /// </summary>
        Task RegisterAsync(ulong signatureHash);
    }
}
