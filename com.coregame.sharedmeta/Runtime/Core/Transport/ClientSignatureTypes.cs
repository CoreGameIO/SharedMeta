using System;
using System.Collections.Generic;
using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    // ════════════════════════════════════════════════════════════════════════════
    //  0.22.0 client-signature & capabilities wire types.
    //
    //  Purpose: let the server compute — once per distinct client build — what
    //  methods and services the client may legitimately use, and which must be
    //  force-downgraded to ServerPatch. The result is a ClientCapabilities object
    //  cached by SignatureHash on the server; subsequent connections from clients
    //  with the same signature skip recomputation.
    //
    //  Two-phase handshake (drives the wire flow, but the actual fill-in lives
    //  in Stages 4–6):
    //   1. Client sends SessionConnectRequest with SignatureHash (quick key).
    //   2. Server checks the registry:
    //      a. Known → reply with Capabilities populated.
    //      b. Unknown → reply with NeedsSignatureRegistration = true; client
    //         follows up with RegisterClientSignatureRequest carrying the full
    //         MetaClientSignature (KnownMethods + per-method ArgHashes). Server
    //         computes capabilities, stores by SignatureHash, returns them.
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
    /// Stable identity of a single dispatchable method — used as the granular
    /// addressing unit inside <see cref="ClientCapabilities"/>.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class MethodIdentity
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string ServiceName { get; set; } = "";
        [Id(1), Key(1), MemoryPackOrder(1)] public string Alias { get; set; } = "";
        [Id(2), Key(2), MemoryPackOrder(2)] public int Version { get; set; }
    }

    /// <summary>
    /// The server's verdict on what THIS client may do. Computed once per
    /// distinct <see cref="MetaClientSignature.SignatureHash"/> on the server and
    /// shipped back inside <see cref="SessionConnectResponse"/>. The client's
    /// generated <c>{Service}ApiClient</c> consults this object on every call:
    /// <list type="bullet">
    ///   <item>If the call's identity is in <see cref="RejectedMethods"/> →
    ///     throw <see cref="SharedMeta.Core.IncompatibleFeatureException"/>
    ///     locally without going to the wire (UX: no wasted round trip).</item>
    ///   <item>If the call's identity is in <see cref="ForceServerPatchMethods"/>
    ///     OR its service is in <see cref="ForceServerPatchServices"/> →
    ///     downgrade execution mode to ServerPatch regardless of the method's
    ///     declared <see cref="ExecutionMode"/>.</item>
    /// </list>
    /// Empty lists = no restrictions (the common case for an up-to-date client).
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer, Immutable]
    public partial class ClientCapabilities
    {
        /// <summary>
        /// Methods the server has on its surface that the client must not call.
        /// Populated when the server method has <c>MinCompatibleVersion</c> above
        /// the client's declared <c>Version</c>, or when the method has been
        /// removed entirely from the server's surface but is still in the
        /// client's <see cref="MetaClientSignature.KnownMethods"/>.
        /// </summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public List<MethodIdentity> RejectedMethods { get; set; } = new();

        /// <summary>
        /// Methods the client may still call but must execute as ServerPatch —
        /// the server-side body has diverged enough that optimistic local
        /// execution would desync. Used by Case 3 (method body changed).
        /// </summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public List<MethodIdentity> ForceServerPatchMethods { get; set; } = new();

        /// <summary>
        /// Entire services that must be force-downgraded to ServerPatch. Used by
        /// Case 2 (config structure changed for the service's bound config) where
        /// the granularity is one whole service, not per-method.
        /// </summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public List<string> ForceServerPatchServices { get; set; } = new();

        /// <summary>
        /// Per-signature method-id mapping from server-side global index → client-side
        /// global index. Length = server's <c>Methods.Count</c>; value <c>ushort.MaxValue</c>
        /// = sentinel "client does not know this method" (used for server-only methods or
        /// methods removed in the client's build — client ignores broadcasts/triggers
        /// referencing such ids). Built once on phase-2 <c>RegisterClientSignature</c>
        /// by the server, cached per signatureHash, shipped to client unchanged so the
        /// client can translate incoming wire ids into its local dispatch table.
        /// </summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public ushort[] ServerToClientMethodIds { get; set; } = System.Array.Empty<ushort>();
    }

    /// <summary>
    /// 0.22.0+ Per-entity capability deltas on top of session-level <see cref="ClientCapabilities"/>.
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
    /// Combines with <see cref="ClientCapabilities"/> at dispatch / broadcast / gate time —
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
    /// Phase-2 response: the server's verdict for the just-registered signature.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class RegisterClientSignatureResponse
    {
        [Id(0), Key(0)] public bool Success { get; set; }
        [Id(1), Key(1)] public string? Error { get; set; }

        /// <summary>Capabilities computed for the registered signature.</summary>
        [Id(2), Key(2)] public ClientCapabilities Capabilities { get; set; } = new();
    }
}
