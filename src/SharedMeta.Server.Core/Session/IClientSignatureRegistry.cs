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
        /// Phase-2 registration. Persists the full <see cref="MetaClientSignature"/> in the
        /// per-hash <see cref="SharedMeta.Server.Core.Grains.IClientSignatureGrain"/>, registers
        /// the hash with the manager directory, computes the
        /// <see cref="ClientSignatureAnnotated"/> verdict against the local server signature,
        /// caches it silo-locally, and returns it so the caller can ship it on
        /// <see cref="RegisterClientSignatureResponse.Annotated"/>.
        /// </summary>
        Task<ClientSignatureAnnotated> RegisterAsync(MetaClientSignature signature);

        /// <summary>
        /// Server-internal lookup of the per-signature <c>clientToServer</c> method-id map —
        /// used by the connection handler to translate the <c>RpcCall.MethodId</c> a client
        /// sends (its local global index) into the server-side global index for dispatch.
        /// Sentinel value <c>ushort.MaxValue</c> at index <c>i</c> means the client claims
        /// to know method-id <c>i</c> but the server doesn't accept that call from this
        /// client (rejected method / forbidden / unknown). Returns <c>null</c> when the
        /// signature has never been registered with the cluster.
        /// </summary>
        Task<ushort[]?> TryGetClientToServerMapAsync(ulong signatureHash);

        /// <summary>
        /// 0.24.0+ Read-through lookup of the <see cref="ClientSignatureAnnotated"/> form.
        /// Returns the cached entry (recomputing from the stored <see cref="MetaClientSignature"/>
        /// if only the legacy capabilities are cached locally) or <c>null</c> when the signature
        /// has never been registered. Used by <c>MetaConnectionHandler</c> to populate
        /// <see cref="SessionConnectResponse.Annotated"/> on a known signature.
        /// </summary>
        Task<ClientSignatureAnnotated?> TryGetAnnotatedAsync(ulong signatureHash);

        /// <summary>
        /// 0.24.0+ Hash of the server signature this registry was constructed against. Returned
        /// on every <see cref="SessionConnectResponse.ServerSignatureHash"/> so the client can
        /// detect when its locally cached annotation has been invalidated by a server redeploy.
        /// Returns 0 when no <see cref="MetaServerSignature"/> is wired (legacy / pure-test).
        /// </summary>
        ulong ServerSignatureHash { get; }
    }
}
