using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Per-signature grain — keyed by the FNV-1a hash of a <see cref="MetaClientSignature"/>
    /// (cast to long for Orleans' integer-key requirement; collision-free since same bits).
    /// Stores the full client signature plus the server's computed
    /// <see cref="ClientCapabilities"/> verdict for it.
    /// <para>
    /// One activation per distinct client build that has ever connected to the cluster.
    /// Idle TTL applies — the grain rehydrates from persistent storage if a long-gone build
    /// reconnects after restart.
    /// </para>
    /// <para>
    /// Read-heavy: every <see cref="SharedMeta.Core.Transport.SessionConnectRequest"/> with
    /// a non-zero <see cref="SessionConnectRequest.ClientSignatureHash"/> hits a per-silo
    /// cache first; the grain is only consulted on a miss. Single-threading inherent to
    /// Orleans is fine — no <c>[AlwaysInterleave]</c> needed.
    /// </para>
    /// </summary>
    public interface IClientSignatureGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// Read the computed capabilities for this signature. Returns <c>null</c> when the
        /// grain has never been populated (the caller should fall back to phase-2 registration).
        /// </summary>
        Task<ClientCapabilities?> GetCapabilitiesAsync();

        /// <summary>
        /// True when the grain holds a populated entry (either from a previous registration
        /// in this session or rehydrated from storage). Lightweight existence check used by
        /// the silo-local <see cref="IClientSignatureRegistry"/> to confirm a hash hint
        /// before issuing a full <see cref="GetCapabilitiesAsync"/> fetch.
        /// </summary>
        Task<bool> ExistsAsync();

        /// <summary>
        /// Persist the full signature plus the server-computed capabilities for this hash.
        /// Idempotent: re-registering an identical signature replaces nothing meaningful;
        /// capabilities computed from the same server build will match anyway.
        /// </summary>
        Task SetAsync(MetaClientSignature signature, ClientCapabilities capabilities);

        /// <summary>
        /// Read the persisted client signature. Used by silos that didn't see the original
        /// phase-2 registration but need to rebuild the per-signature clientToServer method-id
        /// map locally. Returns <c>null</c> when the grain has never been populated.
        /// </summary>
        Task<MetaClientSignature?> GetSignatureAsync();
    }
}
