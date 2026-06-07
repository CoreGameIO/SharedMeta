using System.Collections.Generic;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Server-side mirror of <see cref="MetaClientSignature"/>: the full set of methods the
    /// server exposes plus the per-method compatibility metadata needed to compute
    /// <see cref="ClientSignatureAnnotated"/> for an incoming client signature.
    /// <para>
    /// Emitted as a generated <c>public static readonly</c> singleton on
    /// <c>GameServiceDiscoveryBase</c> by the source generator. Plain C# class — never goes
    /// on the wire — but lives in the shared transport namespace so the same codegen output
    /// compiles on client, server, and any host that pulls in <c>SharedMeta.Core</c>.
    /// </para>
    /// <para>
    /// Lookup tables (<c>id → entry</c>, <c>(svc, alias, ver) → id</c>) are built eagerly
    /// inside the <c>Methods</c> init setter so consumers see an O(1) ready object and never
    /// race on first-use construction.
    /// </para>
    /// </summary>
    public sealed class MetaServerSignature
    {
        private readonly IReadOnlyList<ServerMethodEntry> _methods = System.Array.Empty<ServerMethodEntry>();
        private readonly Dictionary<(string, string, int), ushort> _idLookup = new();
        private readonly ServerMethodEntry?[] _idToEntry = System.Array.Empty<ServerMethodEntry?>();

        /// <summary>Sorted (by <c>ServiceName</c>, <c>Alias</c>, <c>Version</c>) so the
        /// compute pipeline can binary-merge against the client's sorted KnownMethods.</summary>
        public IReadOnlyList<ServerMethodEntry> Methods
        {
            get => _methods;
            init
            {
                _methods = value ?? System.Array.Empty<ServerMethodEntry>();
                // Eager build of both lookup tables — every RPC pays one read on hot paths
                // (dispatch + telemetry), so the per-build cost is acceptable here. Setter
                // is invoked once at startup inside the generated GameServiceDiscoveryBase
                // static initializer; the object never sees Methods reassignment.
                _idLookup = new Dictionary<(string, string, int), ushort>(_methods.Count);
                int max = -1;
                foreach (var m in _methods)
                {
                    _idLookup[(m.ServiceName, m.Alias, m.Version)] = m.GlobalIndex;
                    if (m.GlobalIndex > max) max = m.GlobalIndex;
                }
                _idToEntry = new ServerMethodEntry?[max + 1];
                foreach (var m in _methods)
                    _idToEntry[m.GlobalIndex] = m;
            }
        }

        /// <summary>
        /// Resolves <c>(ServiceName, Alias, Version)</c> → server's <see cref="ServerMethodEntry.GlobalIndex"/>.
        /// Returns <c>null</c> when the method isn't registered (rejected / typo / version drift).
        /// </summary>
        public ushort? ResolveMethodId(string serviceName, string alias, int version)
            => _idLookup.TryGetValue((serviceName, alias, version), out var id) ? id : null;

        /// <summary>
        /// Reverse lookup: server's <c>MethodId</c> → <see cref="ServerMethodEntry"/>. Used by
        /// server-side logging / telemetry to resolve a friendly name from the wire identifier
        /// when string fields are no longer present on the call packet. Returns <c>null</c>
        /// when the id is outside the registered range (legacy / id=0 / drift).
        /// </summary>
        public ServerMethodEntry? GetMethodEntry(ushort methodId)
            => methodId < _idToEntry.Length ? _idToEntry[methodId] : null;

        /// <summary>
        /// <c>[MetaConfigStructureBoundary]</c> declarations harvested from every
        /// <c>[MetaConfig]</c> class. Drives per-subscriber boundary compute on the server.
        /// Empty when no config declares breakpoints.
        /// </summary>
        public IReadOnlyList<ConfigBoundaryEntry> ConfigBoundaries { get; init; } = new List<ConfigBoundaryEntry>();

        /// <summary>
        /// 0.24.0+ FNV-1a over the canonical server-surface string. Distinct from the client
        /// signature hash because it folds in server-only fields that influence the verdict
        /// (<see cref="ServerMethodEntry.MinCompatibleVersion"/>,
        /// <see cref="ServerMethodEntry.GenerateClientApi"/>,
        /// <see cref="ServerMethodEntry.ConfigTypeFullName"/>,
        /// <see cref="ServerMethodEntry.PatchTrackingAvailable"/>, plus
        /// <see cref="ConfigBoundaries"/>). Any server-side change that should invalidate
        /// previously-shipped <c>ClientSignatureAnnotated</c> entries MUST contribute to this
        /// hash, otherwise client caches stay stale. Emitted as a compile-time constant by
        /// <c>GameServiceDiscoveryGenerator</c>; defaults to 0 for synthetic test instances
        /// (the hash field is only load-bearing in the wire/cache flow).
        /// </summary>
        public ulong SignatureHash { get; init; }
    }

    /// <summary>
    /// One structural boundary declared on a config class via
    /// <c>[MetaConfigStructureBoundary("X.Y", Reason)]</c>. The compute pipeline reads
    /// these when deciding whether a client's resolved config version sits below a
    /// structural break — if so, every service bound to <see cref="ConfigTypeFullName"/>
    /// is added to <c>ForceServerPatchServices</c> for that session.
    /// </summary>
    public sealed class ConfigBoundaryEntry
    {
        /// <summary>Fully-qualified name of the annotated config class.</summary>
        public string ConfigTypeFullName { get; init; } = "";

        /// <summary>Major.Minor breakpoint. Clients below this version need force-patch.</summary>
        public string MinConfigVersion { get; init; } = "";

        /// <summary>Developer-facing rationale (diagnostic only).</summary>
        public string Reason { get; init; } = "";
    }

    /// <summary>
    /// One server-side method declaration. Carries the version-compatibility floor needed
    /// to decide whether an older client may still call the method optimistically.
    /// </summary>
    public sealed class ServerMethodEntry
    {
        public string ServiceName { get; init; } = "";
        public string Alias { get; init; } = "";

        /// <summary><c>[MetaMethod(Version = N)]</c>. Zero for legacy/unversioned.</summary>
        public int Version { get; init; }

        /// <summary>Server-side global method index. Stable per server build — assigned in
        /// canonical sort order. Used as the dispatch key on the server (jump-table on ushort)
        /// and as the wire identifier after capability negotiation translates the client's
        /// local id into the server's id. Each <c>(Service, Alias, Version)</c> tuple gets
        /// its own index — version is encoded into the index, not a separate field.</summary>
        public ushort GlobalIndex { get; init; }

        /// <summary>
        /// <c>[MetaMethod(MinCompatibleVersion = N)]</c>. Lowest client method-version the
        /// server will still serve for this method via the version-fallback path. A client
        /// whose version isn't declared on the server falls back to the highest arg-compatible
        /// entry: <c>Version &gt;= MinCompatibleVersion</c> → <c>ForceServerPatch</c>;
        /// <c>Version &lt; MinCompatibleVersion</c> → <c>Rejected</c>. Does NOT gate exact-version
        /// matches (those run locally regardless). Default <c>0</c> = never blocks.
        /// </summary>
        public int MinCompatibleVersion { get; init; }

        /// <summary>FNV-1a hash of canonical parameter-type sequence — used to detect
        /// signature drift (Case 4) even when alias/version match.</summary>
        public ulong ArgHash { get; init; }

        /// <summary>Mirror of <c>[MetaMethod(GenerateClientApi = false)]</c>. Methods with this
        /// flag are server-only; if a client claims to know one, it is always rejected.</summary>
        public bool GenerateClientApi { get; init; } = true;

        /// <summary>
        /// FQN of the config class this service is bound to. Empty when no config bound.
        /// </summary>
        public string ConfigTypeFullName { get; init; } = "";

        /// <summary>
        /// True when the server can produce a state diff for this method under force-patch — i.e.
        /// the service's <c>{Impl}_PatchTracked</c> copy is generated (service is force-patch-able
        /// and did not opt out via <c>[MetaService(PatchTracking = false)]</c>). When false, a
        /// client that would otherwise resolve to <see cref="MethodStatus.ForceServerPatch"/> via
        /// the version-fallback path is instead <see cref="MethodStatus.Rejected"/> — the server
        /// has no safe way to serve the diverged body. Folds into the server signature hash so an
        /// opt-out flip invalidates cached annotations.
        /// </summary>
        public bool PatchTrackingAvailable { get; init; }

        /// <summary>
        /// 0.26.6+ Mirror of <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c>. Drives the
        /// per-method opt-in deep state comparison: server reads this entry by methodId and
        /// runs pre / post / both snapshot CRC checks only when this is non-<c>None</c>.
        /// Unannotated methods stay at <see cref="SharedMeta.Core.SnapshotTiming.None"/> and
        /// pay zero overhead on the dispatch path.
        /// </summary>
        public SharedMeta.Core.SnapshotTiming DeepStateCheck { get; init; }
    }
}
