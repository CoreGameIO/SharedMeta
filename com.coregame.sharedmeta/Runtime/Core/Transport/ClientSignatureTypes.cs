using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    // ════════════════════════════════════════════════════════════════════════════
    //  0.22.0 / 0.24.0 client-signature & annotation wire types.
    //
    //  Purpose: let the server compute — once per distinct client build × server
    //  build pair — what methods the client may legitimately use, and which must be
    //  force-downgraded to ServerPatch. The result is a ClientSignatureAnnotated
    //  object (verdict array + id translation table) cached by SignatureHash on
    //  the server AND on the client; steady-state connects ship < 100 B.
    //
    //  Two-phase handshake:
    //   1. Client sends SessionConnectRequest with ClientSignatureHash (8 B key).
    //   2. Server checks the registry:
    //      a. Known + server-hash matches client cache → reply with Annotated populated.
    //      b. Unknown OR hashes mismatched → reply with NeedsSignatureRegistration = true;
    //         client follows up with RegisterClientSignatureRequest carrying the full
    //         MetaClientSignature. Server computes annotation, stores the signature
    //         keyed by SignatureHash, returns the annotation.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full description of what a client build knows about the server protocol.
    /// Sent in <see cref="RegisterClientSignatureRequest"/> when the server doesn't
    /// recognize the client's <see cref="SignatureHash"/>. The generator emits a
    /// constant <c>MetaClientSignature</c> populated from compile-time discovery
    /// of every <c>[MetaMethod]</c> the client's <c>[MetaService]</c> interfaces
    /// declare.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class MetaClientSignature
    {
        /// <summary>
        /// FNV-1a hash over the canonical KnownMethods list (sorted, including
        /// ArgHash + Version). Stable across builds with the same protocol surface.
        /// Acts as the registry key on the server.
        /// </summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public ulong SignatureHash { get; set; }

        /// <summary>
        /// Client application version string ("Major.Minor.Patch"). Optional; used by
        /// the server to enrich diagnostic logs when a signature is unknown.
        /// </summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public string? ClientVersion { get; set; }

        /// <summary>
        /// Every <c>[MetaMethod]</c> the client knows. Sorted by
        /// <c>(ServiceName, Alias, Version)</c> so the canonical form is stable.
        /// </summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public List<KnownMethodEntry> KnownMethods { get; set; } = new();
    }

    /// <summary>
    /// One entry in <see cref="MetaClientSignature.KnownMethods"/>.
    /// Identifies a method the client believes it can call and the argument-shape
    /// hash it expects to serialize, so the server can detect signature drift
    /// (Case 4: parameter list changed) without ambiguity.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class KnownMethodEntry
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string ServiceName { get; set; } = "";
        [Id(1), Key(1), MemoryPackOrder(1)] public string Alias { get; set; } = "";

        /// <summary>
        /// <c>[MetaMethod(Version = N)]</c>. Zero for legacy/unversioned method
        /// declarations — they route through the lowest-versioned server impl
        /// under the alias.
        /// </summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public int Version { get; set; }

        /// <summary>
        /// FNV-1a hash over the canonical parameter-type sequence ("p1,p2,...->R").
        /// Distinct from <c>SignatureHashGenerator.ComputeMethodHash</c> (which also
        /// includes service/method names): this is JUST the shape of the arg tuple
        /// so the server can spot incompatible parameter changes even when the
        /// alias/version pair is unchanged.
        /// </summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public ulong ArgHash { get; set; }

        /// <summary>
        /// Client-side global method index. Stable per client build — assigned by the
        /// client signature codegen in canonical order over all <c>[MetaMethod]</c>
        /// declarations the client knows about. Used as the dispatch key client-side
        /// and as the wire identifier sent in <c>RpcCall.MethodId</c>. The server
        /// translates incoming client ids to its own indices via the per-signature
        /// <c>clientToServer</c> map built in <c>IClientSignatureRegistry.RegisterAsync</c>.
        /// Each <c>(Service, Alias, Version)</c> tuple gets its own index.
        /// </summary>
        [Id(4), Key(4), MemoryPackOrder(4)] public ushort GlobalIndex { get; set; }
    }

    /// <summary>
    /// 0.22.0+ Per-entity capability deltas on top of session-level <see cref="ClientSignatureAnnotated"/>.
    /// Returned by <c>EntityGrain.SubscribeAsync</c> via <c>SubscribeResponse.AugmentedCapabilities</c>.
    /// <para>
    /// Session-level caps describe what's stable per build (method versioning, arg-hash drift).
    /// Per-entity caps describe what's specific to ONE entity's resolved config version —
    /// notably <c>[MetaConfigStructureBoundary]</c> effects that depend on the entity's pinned
    /// config branch (Private/Shared) or the server's <c>CurrentClientVersion</c> resolution
    /// (Global). Two entities of the same state type can sit on different config branches and
    /// produce different per-entity verdicts for the same client.
    /// </para>
    /// <para>
    /// Combines with <see cref="ClientSignatureAnnotated"/> at dispatch / broadcast / gate time —
    /// EntityGrain refcounts these alongside session-level force-patch methods, the client's
    /// generated <c>*ApiClient</c> consults them at the gate for per-entity rejection /
    /// forced-ServerPatch decisions.
    /// </para>
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class EntityAugmentedCapabilities
    {
        /// <summary>Services on this entity the client cannot invoke at all. Per-entity equivalent
        /// of session-level <c>RejectedMethods</c> but at service granularity. The gate throws
        /// <see cref="IncompatibleFeatureException"/> locally when a call's ServiceName hits this
        /// list; server back-stop rejects forged calls that bypassed the gate.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public List<string> RejectedServices { get; set; } = new();

        /// <summary>Services on this entity that must execute as ServerPatch. Combined with
        /// session-level <c>ForceServerPatchServices</c>/<c>ForceServerPatchMethods</c> at
        /// dispatch (EntityGrain activates patch tracking) and at broadcast fan-out
        /// (SessionManagerGrain tailors per subscriber).</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public List<string> ForceServerPatchServices { get; set; } = new();
    }

    /// <summary>
    /// Phase-2 request: client sends its full <see cref="MetaClientSignature"/>
    /// when the server replies to phase-1 with
    /// <c>SessionConnectResponse.NeedsSignatureRegistration = true</c>. The
    /// response carries the freshly-computed capabilities for this signature.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RegisterClientSignatureRequest
    {
        /// <summary>Session this registration is being attached to.</summary>
        [Id(0), Key(0)] public Guid SessionId { get; set; }

        /// <summary>Full client signature, including every known method.</summary>
        [Id(1), Key(1)] public MetaClientSignature Signature { get; set; } = new();
    }

    /// <summary>
    /// Phase-2 response: the server's annotated verdict for the just-registered signature.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RegisterClientSignatureResponse
    {
        [Id(0), Key(0)] public bool Success { get; set; }
        [Id(1), Key(1)] public string? Error { get; set; }

        /// <summary>0.24.0+ Annotated form of the registered signature — verdict + id mapping.
        /// Null when the host hasn't wired an <c>IClientSignatureRegistry</c> (no negotiation).</summary>
        [Id(2), Key(2)] public ClientSignatureAnnotated? Annotated { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  0.24.0 annotated signature. Server computes
    //  once per (clientHash, serverHash) pair; ships verdict + id mapping as flat
    //  arrays. Client caches by clientHash; invalidates on serverHash mismatch.
    //  See docs/adr/0.24.0-server-signature-handshake.md.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-method verdict written into <see cref="ClientSignatureAnnotated.Statuses"/>.
    /// Consulted by <c>CapabilitiesGate</c> on every outgoing RPC.
    /// </summary>
    [GenerateSerializer]
    public enum MethodStatus : byte
    {
        /// <summary>Default — method exists on both sides with matching signature; call proceeds normally.</summary>
        Ok = 0,
        /// <summary>Method body diverged or service config-boundary changed; execute as ServerPatch.</summary>
        ForceServerPatch = 1,
        /// <summary>Method removed / arg-shape mismatch / below MinCompatibleVersion; client must not call.</summary>
        Rejected = 2,
    }

    /// <summary>
    /// Server's verdict + id-mapping shipped to the client. Deterministic output of
    /// <c>(MetaClientSignature.SignatureHash, MetaServerSignature.SignatureHash)</c>.
    /// <para>
    /// <b>Cache key</b> on the client is <see cref="ClientSignatureHash"/>; entry is invalidated
    /// when the server's reported <see cref="ServerSignatureHash"/> diverges from the cached one.
    /// On a steady-state connect (both hashes unchanged), the client supplies its hash, the
    /// server confirms its hash, the cached annotation is reused — phase-2 is skipped entirely.
    /// </para>
    /// <para>
    /// <b>Wire arithmetic</b> on a 1000-method surface: HIT ≈ &lt; 100 B (only hashes on the
    /// wire), MISS ≈ 3 KB (full annotation) cached forever for that hash pair.
    /// </para>
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ClientSignatureAnnotated
    {
        /// <summary>Identity of the client signature this annotation was computed against.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public ulong ClientSignatureHash { get; set; }

        /// <summary>Identity of the server signature this annotation was computed against.
        /// Carried by the entry so the client can detect cache staleness when the server
        /// returns a different hash on a later connect.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public ulong ServerSignatureHash { get; set; }

        /// <summary>
        /// server method id → client method id translation. Indexed by
        /// <c>serverMethodId</c> (= server's <c>GlobalIndex</c>); length =
        /// server's method count. Value <see cref="UnknownClientMethodId"/>
        /// (0xFFFF) means "client doesn't know this method" — used for
        /// server-only methods or methods the client retired. Client uses this on
        /// inbound broadcasts to translate the server's id into its own dispatch
        /// table id.
        /// </summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public ushort[] ServerToClient { get; set; } = System.Array.Empty<ushort>();

        /// <summary>
        /// Per-method verdict indexed by client method id (= client's
        /// <c>GlobalIndex</c>); length = client's method count. Consulted by
        /// <c>CapabilitiesGate</c> at every RPC: O(1) array index replaces the old
        /// <c>HashSet&lt;MethodIdentity&gt;</c> lookup over four parallel lists.
        /// <c>ForceServerPatchServices</c> (service-level) is folded in here per
        /// method at compute time — there is no separate service-level wire flag.
        /// </summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public MethodStatus[] Statuses { get; set; } = System.Array.Empty<MethodStatus>();

        /// <summary>Sentinel value in <see cref="ServerToClient"/> for "client does not know this server method".</summary>
        public const ushort UnknownClientMethodId = 0xFFFF;
    }
}
