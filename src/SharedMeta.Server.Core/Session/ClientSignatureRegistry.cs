using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Default <see cref="IClientSignatureRegistry"/> implementation. Singleton per silo.
    /// <para>
    /// Capability compute: <see cref="RegisterAsync"/> consults the injected
    /// <see cref="MetaServerSignature"/> (generated as <c>GameServiceDiscovery.ServerSignature</c>
    /// in the consumer project, supplied via DI) and produces <see cref="ClientCapabilities"/>
    /// that flag:
    /// <list type="bullet">
    ///   <item><b>RejectedMethods</b> — methods the client claims to know but the server
    ///     either doesn't expose at all, marks <c>GenerateClientApi = false</c>, or whose
    ///     argument shape (<c>ArgHash</c>) disagrees with the server's declaration (Case 4).</item>
    ///   <item><b>ForceServerPatchMethods</b> — methods where the client's <c>Version</c>
    ///     is below the server's declared <c>MinCompatibleVersion</c>, meaning the body
    ///     diverged and the client's optimistic execution would desync (Case 3).</item>
    /// </list>
    /// When the host hasn't injected a <see cref="MetaServerSignature"/>, compute degrades
    /// to "no restrictions" — the same legacy behaviour the Stage 4 placeholder produced.
    /// </para>
    /// </summary>
    public class ClientSignatureRegistry : IClientSignatureRegistry
    {
        private readonly IGrainFactory _grainFactory;
        private readonly MetaServerSignature? _serverSignature;

        // Concurrent because the registry is a singleton called from every connection
        // handler concurrently. Tombstone for "looked up, server doesn't know it" is
        // intentionally not stored — a "needs registration" reply prompts the client to
        // follow up, after which we cache the registered capabilities like any other hit.
        private readonly ConcurrentDictionary<ulong, ClientCapabilities> _cache = new();

        // Server-internal clientToServer method-id map per signature hash. Kept separate
        // from ClientCapabilities (which crosses the wire to the client) — the client only
        // needs ServerToClient to translate incoming wire ids; ClientToServer is for the
        // server's inbound RPC translation.
        private readonly ConcurrentDictionary<ulong, ushort[]> _clientToServerCache = new();

        public ClientSignatureRegistry(IGrainFactory grainFactory, MetaServerSignature? serverSignature = null)
        {
            _grainFactory = grainFactory;
            _serverSignature = serverSignature;
        }

        public async Task<bool> IsKnownAsync(ulong signatureHash)
        {
            if (_cache.ContainsKey(signatureHash)) return true;
            var manager = _grainFactory.GetGrain<IClientSignatureManagerGrain>("global");
            return await manager.IsKnownAsync(signatureHash);
        }

        public async Task<ClientCapabilities?> TryGetCapabilitiesAsync(ulong signatureHash)
        {
            if (_cache.TryGetValue(signatureHash, out var cached)) return cached;

            // Check directory first — cheap, avoids spinning up a per-hash grain activation
            // for hashes that have never been registered.
            var manager = _grainFactory.GetGrain<IClientSignatureManagerGrain>("global");
            if (!await manager.IsKnownAsync(signatureHash)) return null;

            // Known to the directory but not in our cache — fetch from the per-hash grain.
            var sigGrain = _grainFactory.GetGrain<IClientSignatureGrain>((long)signatureHash);
            var caps = await sigGrain.GetCapabilitiesAsync();
            if (caps != null) _cache[signatureHash] = caps;
            return caps;
        }

        public async Task<ClientCapabilities> RegisterAsync(MetaClientSignature signature)
        {
            var (capabilities, clientToServer) = ComputeCapabilitiesAndMaps(signature);

            var sigGrain = _grainFactory.GetGrain<IClientSignatureGrain>((long)signature.SignatureHash);
            await sigGrain.SetAsync(signature, capabilities);

            var manager = _grainFactory.GetGrain<IClientSignatureManagerGrain>("global");
            await manager.RegisterAsync(signature.SignatureHash);

            _cache[signature.SignatureHash] = capabilities;
            _clientToServerCache[signature.SignatureHash] = clientToServer;
            return capabilities;
        }

        public async Task<ushort[]?> TryGetClientToServerMapAsync(ulong signatureHash)
        {
            if (_clientToServerCache.TryGetValue(signatureHash, out var cached)) return cached;

            // Not cached locally — try to rehydrate from the per-hash grain (which holds
            // the original MetaClientSignature). If missing, signature is unknown to the
            // cluster: caller treats as "needs registration" / reject.
            var sigGrain = _grainFactory.GetGrain<IClientSignatureGrain>((long)signatureHash);
            var sig = await sigGrain.GetSignatureAsync();
            if (sig == null) return null;

            var (caps, c2s) = ComputeCapabilitiesAndMaps(sig);
            _cache.TryAdd(signatureHash, caps);
            _clientToServerCache.TryAdd(signatureHash, c2s);
            return c2s;
        }

        /// <summary>
        /// Capability compute + method-id mapping. Walks the client's known methods, produces
        /// the rejection / force-patch verdict (consumed by the client), AND builds both
        /// directions of the per-signature method-id map:
        /// <list type="bullet">
        ///   <item><b>serverToClient</b> (length = server's method count) — at server's global
        ///     index, the client's local global index OR <c>ushort.MaxValue</c> (client doesn't
        ///     know this method, e.g. server-only). Shipped to client via
        ///     <see cref="ClientCapabilities.ServerToClientMethodIds"/>.</item>
        ///   <item><b>clientToServer</b> (length = client's method count) — at client's local
        ///     global index, the server's global index OR <c>ushort.MaxValue</c> (rejected
        ///     method — same set as <see cref="ClientCapabilities.RejectedMethods"/>). Kept
        ///     server-internal in the registry's silo-local cache; used by the connection
        ///     handler to translate inbound <c>RpcCall.MethodId</c>.</item>
        /// </list>
        /// Pure / side-effect free / deterministic — same inputs give same outputs.
        /// </summary>
        protected virtual (ClientCapabilities, ushort[]) ComputeCapabilitiesAndMaps(MetaClientSignature signature)
        {
            var caps = new ClientCapabilities();
            var clientToServer = new ushort[signature.KnownMethods.Count];
            for (int i = 0; i < clientToServer.Length; i++) clientToServer[i] = ushort.MaxValue;

            if (_serverSignature == null)
            {
                // No server-side signature wired up — degrade to "no restrictions". Identity-ish
                // map: clientId == clientId, but server has no methods to dispatch via id either.
                caps.ServerToClientMethodIds = System.Array.Empty<ushort>();
                return (caps, clientToServer);
            }

            // Build serverToClient: filled in below as we walk client methods and match server
            // entries by (ServiceName, Alias, Version). Unmatched server slots stay at sentinel.
            var serverToClient = new ushort[_serverSignature.Methods.Count];
            for (int i = 0; i < serverToClient.Length; i++) serverToClient[i] = ushort.MaxValue;

            // Index server methods by (Service, Alias, Version) for O(1) lookup.
            var serverByKey = new Dictionary<(string, string, int), ServerMethodEntry>(_serverSignature.Methods.Count);
            foreach (var s in _serverSignature.Methods)
                serverByKey[(s.ServiceName, s.Alias, s.Version)] = s;

            foreach (var clientMethod in signature.KnownMethods)
            {
                var clientIdx = clientMethod.GlobalIndex;
                var key = (clientMethod.ServiceName, clientMethod.Alias, clientMethod.Version);
                if (!serverByKey.TryGetValue(key, out var serverMethod))
                {
                    caps.RejectedMethods.Add(new MethodIdentity
                    {
                        ServiceName = clientMethod.ServiceName,
                        Alias = clientMethod.Alias,
                        Version = clientMethod.Version,
                    });
                    continue;
                }

                if (!serverMethod.GenerateClientApi)
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
                    serverToClient[serverMethod.GlobalIndex] = clientIdx;
                    caps.RejectedMethods.Add(new MethodIdentity
                    {
                        ServiceName = clientMethod.ServiceName,
                        Alias = clientMethod.Alias,
                        Version = clientMethod.Version,
                    });
                    continue;
                }

                if (clientMethod.ArgHash != serverMethod.ArgHash)
                {
                    caps.RejectedMethods.Add(new MethodIdentity
                    {
                        ServiceName = clientMethod.ServiceName,
                        Alias = clientMethod.Alias,
                        Version = clientMethod.Version,
                    });
                    continue;
                }

                // Method is callable — map both directions.
                clientToServer[clientIdx] = serverMethod.GlobalIndex;
                serverToClient[serverMethod.GlobalIndex] = clientIdx;

                if (serverMethod.MinCompatibleVersion > 0
                    && clientMethod.Version < serverMethod.MinCompatibleVersion)
                {
                    caps.ForceServerPatchMethods.Add(new MethodIdentity
                    {
                        ServiceName = clientMethod.ServiceName,
                        Alias = clientMethod.Alias,
                        Version = clientMethod.Version,
                    });
                }
            }

            caps.ServerToClientMethodIds = serverToClient;
            return (caps, clientToServer);
        }
    }
}
