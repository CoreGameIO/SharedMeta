using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Per-signature grain — keyed by the FNV-1a hash of a <see cref="MetaClientSignature"/>
    /// (cast to long for Orleans' integer-key requirement; collision-free since same bits).
    /// Stores only the full client signature; the
    /// <see cref="ClientSignatureAnnotated"/> verdict is recomputed deterministically by
    /// <see cref="SharedMeta.Server.Core.Session.IClientSignatureRegistry"/> on each silo
    /// against its local <see cref="MetaServerSignature"/>.
    /// <para>
    /// One activation per distinct client build that has ever connected to the cluster.
    /// Idle TTL applies — the grain rehydrates from persistent storage if a long-gone build
    /// reconnects after restart.
    /// </para>
    /// </summary>
    public interface IClientSignatureGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// True when the grain holds a populated entry (either from a previous registration
        /// in this session or rehydrated from storage). Lightweight existence check used by
        /// the silo-local <see cref="SharedMeta.Server.Core.Session.IClientSignatureRegistry"/>
        /// to confirm a hash hint before issuing a full <see cref="GetSignatureAsync"/> fetch.
        /// </summary>
        Task<bool> ExistsAsync();

        /// <summary>
        /// Persist the full signature for this hash. Idempotent: re-registering an identical
        /// signature is a no-op (writes the same bytes back).
        /// </summary>
        Task SetAsync(MetaClientSignature signature);

        /// <summary>
        /// Read the persisted client signature. Used by silos that didn't see the original
        /// phase-2 registration but need to rebuild the per-signature mapping locally.
        /// Returns <c>null</c> when the grain has never been populated.
        /// </summary>
        Task<MetaClientSignature?> GetSignatureAsync();
    }
}
