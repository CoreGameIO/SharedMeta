using System.Threading.Tasks;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Per-silo accessor for the cluster's client-signature directory. Sits in front of
    /// <see cref="SharedMeta.Server.Core.Grains.IClientSignatureManagerGrain"/> and
    /// <see cref="SharedMeta.Server.Core.Grains.IClientSignatureGrain"/> with a local
    /// concurrent cache so the hot path — every session-connect with a non-zero
    /// <see cref="SessionConnectRequest.ClientSignatureHash"/> — never touches Orleans
    /// after the first hit on a silo.
    ///
    /// <para>
    /// Lookup flow:
    /// <list type="number">
    ///   <item>Local cache hit → return capabilities.</item>
    ///   <item>Local cache miss → <c>IClientSignatureManagerGrain.IsKnownAsync</c>:
    ///     <list type="bullet">
    ///       <item>Known → fetch <c>IClientSignatureGrain.GetCapabilitiesAsync</c>,
    ///         populate cache, return.</item>
    ///       <item>Unknown → return <c>null</c>; the caller signals
    ///         <c>SessionConnectResponse.NeedsSignatureRegistration = true</c> so the
    ///         client follows up with phase-2.</item>
    ///     </list>
    ///   </item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Cache invalidation across silos is intentionally absent in this version: signatures
    /// only ever grow the set (never get retracted), and registrations are rare events
    /// (new client build deployments). Capabilities computed from the same server build
    /// are deterministic, so even if two silos race to populate the same hash, both arrive
    /// at the same answer.
    /// </para>
    /// </summary>
    public interface IClientSignatureRegistry
    {
        /// <summary>
        /// Cheap "is this signature even known?" probe. Returns <c>true</c> immediately
        /// when the local cache has the entry; otherwise consults the manager grain.
        /// Does not fetch the capabilities payload.
        /// </summary>
        Task<bool> IsKnownAsync(ulong signatureHash);

        /// <summary>
        /// Read-through lookup. Returns the cached/registered capabilities, or <c>null</c>
        /// when the signature has never been registered with the cluster.
        /// </summary>
        Task<ClientCapabilities?> TryGetCapabilitiesAsync(ulong signatureHash);

        /// <summary>
        /// Phase-2 registration. Computes capabilities for the full <see cref="MetaClientSignature"/>,
        /// persists them in the per-hash <see cref="SharedMeta.Server.Core.Grains.IClientSignatureGrain"/>,
        /// registers the hash with the manager directory, and populates the local cache.
        /// Returns the computed capabilities so the caller can put them directly on the
        /// <see cref="RegisterClientSignatureResponse"/>.
        /// </summary>
        Task<ClientCapabilities> RegisterAsync(MetaClientSignature signature);
    }
}
