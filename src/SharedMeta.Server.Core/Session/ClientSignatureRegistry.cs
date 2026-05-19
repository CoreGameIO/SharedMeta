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
            var capabilities = ComputeCapabilities(signature);

            var sigGrain = _grainFactory.GetGrain<IClientSignatureGrain>((long)signature.SignatureHash);
            await sigGrain.SetAsync(signature, capabilities);

            var manager = _grainFactory.GetGrain<IClientSignatureManagerGrain>("global");
            await manager.RegisterAsync(signature.SignatureHash);

            _cache[signature.SignatureHash] = capabilities;
            return capabilities;
        }

        /// <summary>
        /// Capability compute. Walks the client's <see cref="MetaClientSignature.KnownMethods"/>
        /// and produces a verdict per entry by consulting the injected <see cref="MetaServerSignature"/>.
        /// Pure / side-effect free / deterministic — calling this twice with the same args
        /// returns equivalent results (different list instances, identical contents).
        /// <para>
        /// Subclasses may override to inject project-specific policy (e.g. additional
        /// services to force-patch based on per-method config-structure boundaries — Stage 8).
        /// The base impl handles the three core cases: missing-method / signature-drift / version-floor.
        /// </para>
        /// </summary>
        protected virtual ClientCapabilities ComputeCapabilities(MetaClientSignature signature)
        {
            var caps = new ClientCapabilities();
            if (_serverSignature == null)
            {
                // No server-side signature wired up — degrade to "no restrictions". A host can
                // opt back into compute by registering MetaServerSignature in DI.
                return caps;
            }

            // Index server methods by (ServiceName, Alias) — multiple Version entries land in
            // the same bucket, which the per-client-method matching below handles by Version
            // equality (Case 0 fallback for methodVersion=0 is a dispatcher concern, not a
            // capability concern: every coexisting Version is its own row here).
            var serverByAlias = new Dictionary<(string, string, int), ServerMethodEntry>();
            foreach (var s in _serverSignature.Methods)
            {
                serverByAlias[(s.ServiceName, s.Alias, s.Version)] = s;
            }

            foreach (var clientMethod in signature.KnownMethods)
            {
                // Lookup the server method for the exact (Service, Alias, Version) the client
                // declared. If the client's Version doesn't exist on the server, that's Case 4
                // (method was removed at that version) — reject.
                var key = (clientMethod.ServiceName, clientMethod.Alias, clientMethod.Version);
                if (!serverByAlias.TryGetValue(key, out var serverMethod))
                {
                    caps.RejectedMethods.Add(new MethodIdentity
                    {
                        ServiceName = clientMethod.ServiceName,
                        Alias = clientMethod.Alias,
                        Version = clientMethod.Version,
                    });
                    continue;
                }

                // Server-only method (GenerateClientApi = false) — client shouldn't know it.
                // Tag for rejection so the client's *ApiClient throws locally instead of
                // wasting a round trip that the server-side gate would block anyway.
                if (!serverMethod.GenerateClientApi)
                {
                    caps.RejectedMethods.Add(new MethodIdentity
                    {
                        ServiceName = clientMethod.ServiceName,
                        Alias = clientMethod.Alias,
                        Version = clientMethod.Version,
                    });
                    continue;
                }

                // Case 4: same (Alias, Version) but the argument shape doesn't match — the
                // client would serialize a parameter tuple the server can't deserialize.
                // Reject so the call never goes on the wire.
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

                // Case 3: method body changed enough on the server that an older client's
                // optimistic execution would diverge. The server declared a floor via
                // [MetaMethod(MinCompatibleVersion = N)]; if the client's Version is below
                // it, downgrade this method to ServerPatch on the client.
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

            return caps;
        }
    }
}
