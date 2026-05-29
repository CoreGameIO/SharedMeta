using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Default <see cref="IClientSignatureRegistry"/> implementation. Singleton per silo.
    /// <para>
    /// 0.24.0+ produces <see cref="ClientSignatureAnnotated"/> for each known client signature
    /// by walking the local <see cref="MetaServerSignature"/> against the client's
    /// <see cref="MetaClientSignature.KnownMethods"/>: methods the server doesn't have (or
    /// flags <c>GenerateClientApi=false</c>, or whose <c>ArgHash</c> diverges) become
    /// <see cref="MethodStatus.Rejected"/>; methods whose client version sits below the server's
    /// <c>MinCompatibleVersion</c> become <see cref="MethodStatus.ForceServerPatch"/>; everything
    /// else stays <see cref="MethodStatus.Ok"/>. The translation table
    /// <c>ServerToClient[serverMethodId] = clientMethodId</c> is built at the same time.
    /// </para>
    /// <para>
    /// When the host hasn't injected a <see cref="MetaServerSignature"/>, the compute degrades
    /// to "no restrictions" — empty status array, empty mapping table, no
    /// <see cref="ClientSignatureAnnotated.ServerSignatureHash"/>.
    /// </para>
    /// </summary>
    public class ClientSignatureRegistry : IClientSignatureRegistry
    {
        private readonly IGrainFactory _grainFactory;
        private readonly MetaServerSignature? _serverSignature;

        // Silo-local cache of the annotated form. Concurrent because the registry is a
        // singleton called from every connection handler concurrently.
        private readonly ConcurrentDictionary<ulong, ClientSignatureAnnotated> _annotatedCache = new();

        // Server-internal clientToServer method-id map per signature hash. Kept separate
        // from ClientSignatureAnnotated (which crosses the wire to the client) — the client
        // only needs ServerToClient to translate incoming wire ids; ClientToServer is for
        // the server's inbound RPC dispatch translation.
        private readonly ConcurrentDictionary<ulong, ushort[]> _clientToServerCache = new();

        // Lazily resolved on first use so unit tests can construct the registry with a
        // null IGrainFactory and exercise the pure compute path without touching Orleans.
        // Production code always supplies a real grain factory.
        private IClientSignatureManagerGrain? _manager;
        private IClientSignatureManagerGrain Manager
            => _manager ??= _grainFactory.GetGrain<IClientSignatureManagerGrain>("global");

        public ClientSignatureRegistry(IGrainFactory grainFactory, MetaServerSignature? serverSignature = null)
        {
            _grainFactory = grainFactory;
            _serverSignature = serverSignature;
        }

        public ulong ServerSignatureHash => _serverSignature?.SignatureHash ?? 0UL;

        public async Task<bool> IsKnownAsync(ulong signatureHash)
        {
            if (_annotatedCache.ContainsKey(signatureHash)) return true;
            return await Manager.IsKnownAsync(signatureHash);
        }

        public async Task<ClientSignatureAnnotated> RegisterAsync(MetaClientSignature signature)
        {
            // Fast path: another connect (or this same connect's earlier handshake) already
            // populated the cache. No grain hop needed.
            if (_annotatedCache.TryGetValue(signature.SignatureHash, out var cached))
                return cached;

            var (annotated, clientToServer) = ComputeAnnotatedAndMap(signature);

            if (!_annotatedCache.TryAdd(signature.SignatureHash, annotated))
                return annotated;

            _clientToServerCache[signature.SignatureHash] = clientToServer;

            if (!await Manager.IsKnownAsync(signature.SignatureHash)) {
                var sigGrain = _grainFactory.GetGrain<IClientSignatureGrain>((long)signature.SignatureHash);
                await sigGrain.SetAsync(signature);
                await Manager.RegisterAsync(signature.SignatureHash);
            }

            return annotated;
        }

        public async Task<ClientSignatureAnnotated?> TryGetAnnotatedAsync(ulong signatureHash)
        {
            if (_annotatedCache.TryGetValue(signatureHash, out var cached)) return cached;

            // Local cache miss — rehydrate from per-hash grain. Recompute annotated from the
            // stored signature on first lookup; then it lives in the local cache for the
            // silo's lifetime.
            if (!await Manager.IsKnownAsync(signatureHash)) return null;

            var sigGrain = _grainFactory.GetGrain<IClientSignatureGrain>((long)signatureHash);
            var sig = await sigGrain.GetSignatureAsync();
            if (sig == null) return null;

            var (annotated, clientToServer) = ComputeAnnotatedAndMap(sig);
            _annotatedCache.TryAdd(signatureHash, annotated);
            _clientToServerCache.TryAdd(signatureHash, clientToServer);
            return annotated;
        }

        public async Task<ushort[]?> TryGetClientToServerMapAsync(ulong signatureHash)
        {
            if (_clientToServerCache.TryGetValue(signatureHash, out var cached)) return cached;

            // Not cached locally — rehydrate from the per-hash grain (which holds the
            // original MetaClientSignature). If missing, signature is unknown to the
            // cluster: caller treats as "needs registration" / reject.
            var sigGrain = _grainFactory.GetGrain<IClientSignatureGrain>((long)signatureHash);
            var sig = await sigGrain.GetSignatureAsync();
            if (sig == null) return null;

            var (annotated, clientToServer) = ComputeAnnotatedAndMap(sig);
            _annotatedCache.TryAdd(signatureHash, annotated);
            _clientToServerCache.TryAdd(signatureHash, clientToServer);
            return clientToServer;
        }

        /// <summary>
        /// 0.24.0+ Single compute pass that walks the client's known methods against the local
        /// server signature and builds:
        /// <list type="bullet">
        ///   <item><b>Statuses</b> (length = client's method count) — per-method verdict (Ok /
        ///     ForceServerPatch / Rejected) consumed by <c>CapabilitiesGate</c> on the client.</item>
        ///   <item><b>ServerToClient</b> (length = server's method count) — at server's global
        ///     index, the client's local global index OR <see cref="ClientSignatureAnnotated.UnknownClientMethodId"/>
        ///     (client doesn't know this method). Shipped to the client for inbound broadcast
        ///     translation.</item>
        ///   <item><b>clientToServer</b> (length = client's method count) — at client's local
        ///     global index, the server's global index OR <c>ushort.MaxValue</c> (rejected method).
        ///     Kept server-internal; used by the connection handler to translate inbound
        ///     <c>RpcCall.MethodId</c> to the server-side dispatch key.</item>
        /// </list>
        /// Pure / side-effect free / deterministic — same inputs give same outputs.
        /// </summary>
        protected virtual (ClientSignatureAnnotated, ushort[]) ComputeAnnotatedAndMap(MetaClientSignature signature)
        {
            // Size statuses by max client GlobalIndex + 1 to defensively cover any sparse
            // index space (production generator emits dense 0..N-1 though).
            int statusLen = 0;
            foreach (var m in signature.KnownMethods)
                if (m.GlobalIndex + 1 > statusLen) statusLen = m.GlobalIndex + 1;

            var statuses = new MethodStatus[statusLen];
            var clientToServer = new ushort[signature.KnownMethods.Count];
            for (int i = 0; i < clientToServer.Length; i++) clientToServer[i] = ushort.MaxValue;

            if (_serverSignature == null)
            {
                // No server-side signature wired up — degrade to "no restrictions". Empty
                // ServerToClient (no broadcasts to translate); statuses all Ok by default.
                return (new ClientSignatureAnnotated
                {
                    ClientSignatureHash = signature.SignatureHash,
                    ServerSignatureHash = 0UL,
                    ServerToClient = System.Array.Empty<ushort>(),
                    Statuses = statuses,
                }, clientToServer);
            }

            var serverToClient = new ushort[_serverSignature.Methods.Count];
            for (int i = 0; i < serverToClient.Length; i++)
                serverToClient[i] = ClientSignatureAnnotated.UnknownClientMethodId;

            // Index server methods two ways:
            //  - serverByKey: exact (Service, Alias, Version) → entry. An exact match means the
            //    client's declared method version IS a surface the server still exposes, so the
            //    client may run it locally (its body matches a known server body).
            //  - serverByAlias: (Service, Alias) → entries ordered by Version DESC. Used for the
            //    version-fallback path: when the client's exact version isn't on the server, the
            //    client's local body is a different (older or newer) version than anything the
            //    server runs, so it must NOT run locally — the server runs the authoritative body
            //    and ships a ServerPatch diff. The fallback target is the highest-version entry
            //    whose ArgHash matches (same wire call-shape).
            var serverByKey = new Dictionary<(string, string, int), ServerMethodEntry>(_serverSignature.Methods.Count);
            var serverByAlias = new Dictionary<(string, string), List<ServerMethodEntry>>();
            foreach (var s in _serverSignature.Methods)
            {
                serverByKey[(s.ServiceName, s.Alias, s.Version)] = s;
                var aliasKey = (s.ServiceName, s.Alias);
                if (!serverByAlias.TryGetValue(aliasKey, out var list))
                    serverByAlias[aliasKey] = list = new List<ServerMethodEntry>();
                list.Add(s);
            }
            foreach (var list in serverByAlias.Values)
                list.Sort((a, b) => b.Version.CompareTo(a.Version)); // highest version first

            foreach (var clientMethod in signature.KnownMethods)
            {
                var clientIdx = clientMethod.GlobalIndex;
                var exactKey = (clientMethod.ServiceName, clientMethod.Alias, clientMethod.Version);

                // ── Exact match: client's declared version is a live server surface ──────────
                if (serverByKey.TryGetValue(exactKey, out var exact))
                {
                    if (!exact.GenerateClientApi)
                    {
                        // GenerateClientApi=false forbids the CLIENT-INITIATED direction only:
                        // leave clientToServer[clientIdx] at the sentinel so RpcCallAsync rejects
                        // forged invocations. BUT the server may still broadcast this method to
                        // subscribers (Notification mode is the canonical case — server-only
                        // mutation that fans out as a broadcast for clients to replay). The client
                        // has the local handler generated for it (GenerateClientApi=false suppresses
                        // only the callable, not the broadcast/replay handler) — without the
                        // server→client map entry, the broadcast would arrive with sentinel
                        // methodId and silently drop, causing a downstream desync on any
                        // subsequent Server-mode RPC that relies on the mutation being applied.
                        serverToClient[exact.GlobalIndex] = clientIdx;
                        statuses[clientIdx] = MethodStatus.Rejected;
                        continue;
                    }

                    if (clientMethod.ArgHash != exact.ArgHash)
                    {
                        // Same (Service, Alias, Version) but the wire arg-shape drifted (build
                        // skew / hash collision) — can't safely round-trip. Reject.
                        statuses[clientIdx] = MethodStatus.Rejected;
                        continue;
                    }

                    // Exact version + arg-compatible + client-callable → run locally as declared.
                    // No MinCompatibleVersion gate here: declaring this exact version IS the
                    // statement "clients at this version run it locally." The floor only governs
                    // the fallback path below.
                    clientToServer[clientIdx] = exact.GlobalIndex;
                    serverToClient[exact.GlobalIndex] = clientIdx;
                    statuses[clientIdx] = MethodStatus.Ok;
                    continue;
                }

                // ── Version fallback: no exact version, find the newest arg-compatible body ──
                ServerMethodEntry? fallback = null;
                if (serverByAlias.TryGetValue((clientMethod.ServiceName, clientMethod.Alias), out var candidates))
                {
                    foreach (var cand in candidates) // highest Version first
                    {
                        if (!cand.GenerateClientApi) continue;
                        if (cand.ArgHash != clientMethod.ArgHash) continue;
                        fallback = cand;
                        break;
                    }
                }

                if (fallback == null)
                {
                    // No (Service, Alias) at all, or none with a compatible arg-shape — the call
                    // can't be served against any server body. Reject.
                    statuses[clientIdx] = MethodStatus.Rejected;
                    continue;
                }

                if (clientMethod.Version < fallback.MinCompatibleVersion)
                {
                    // Explicitly too old to serve even via ServerPatch — blocked by policy.
                    // Client must update. Leave serverToClient at sentinel: a blocked method
                    // has no client-side handler we're willing to feed.
                    statuses[clientIdx] = MethodStatus.Rejected;
                    continue;
                }

                // Arg-compatible, at/above the floor, but a different version than any declared
                // server surface → the client's local body would diverge. The fallback wants
                // ServerPatch — but the server can only ship a diff if the service's
                // {Impl}_PatchTracked copy exists. When the service opted out of patch tracking
                // (PatchTrackingAvailable == false) there is no safe way to serve the diverged
                // body, so reject instead of silently shipping an empty patch (which would desync).
                if (!fallback.PatchTrackingAvailable)
                {
                    statuses[clientIdx] = MethodStatus.Rejected;
                    continue;
                }

                // Force ServerPatch: route the client's RPC to the fallback (authoritative) body
                // and translate its broadcasts back to the client's local handler, which applies
                // the patch.
                clientToServer[clientIdx] = fallback.GlobalIndex;
                serverToClient[fallback.GlobalIndex] = clientIdx;
                statuses[clientIdx] = MethodStatus.ForceServerPatch;
            }

            return (new ClientSignatureAnnotated
            {
                ClientSignatureHash = signature.SignatureHash,
                ServerSignatureHash = _serverSignature.SignatureHash,
                ServerToClient = serverToClient,
                Statuses = statuses,
            }, clientToServer);
        }
    }
}
