using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;
using SharedMeta.Server;
using SharedMeta.Server.Core.Session;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Context implementation for IMetaProvider.
    /// </summary>
    internal class MetaProviderContext : IMetaProviderContext
    {
        public string EntityId { get; }
        public IMetaSerializer Serializer { get; }
        public IGrainFactory GrainFactory { get; }
        public ILogger? Logger { get; }
        public byte[]? NamedRandomsBytes { get; }
        public SharedMeta.Server.Core.Memory.PooledPayloadRegistry? Registry { get; }

        public MetaProviderContext(string entityId, IMetaSerializer serializer, IGrainFactory grainFactory,
            ILogger? logger = null, byte[]? namedRandomsBytes = null,
            SharedMeta.Server.Core.Memory.PooledPayloadRegistry? registry = null)
        {
            EntityId = entityId;
            Serializer = serializer;
            GrainFactory = grainFactory;
            Logger = logger;
            NamedRandomsBytes = namedRandomsBytes;
            Registry = registry;
        }
    }

    /// <summary>
    /// Thin entity grain wrapper with persistent state.
    /// Delegates all business logic to IMetaProvider.
    /// State, subscribers, and sequence numbers survive deactivation/reactivation.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    public class EntityGrain<TState> : Grain, IEntityGrain<TState>
        where TState : class, ISharedState, new()
    {
        private readonly IPersistentState<EntityGrainState<TState>> _persistentState;
        private readonly IMetaProviderFactory<TState> _providerFactory;
        private readonly IMetaSerializer _serializer;
        private readonly ILogger _logger;
        private readonly EntityGrainOptions _options;
        private readonly IEntityGrainResolver _entityGrainResolver;
        private readonly IExecutionModeProvider? _executionModeProvider;
        private readonly IConfigVersionResolver? _configVersionResolver;
        // Resolves capabilities from the caller's signature hash at Subscribe time.
        // Null = host hasn't wired negotiation; grain stays in pass-through mode.
        private readonly IClientSignatureRegistry? _signatureRegistry;

        private IMetaProvider<TState>? _provider;

        // Per-activation scratch pool + grain-scoped serializer. All intermediate Pack(value)
        // calls inside HandleCallAsync / HandleExternalEventAsync / SubscribeAsync write into
        // this pool's byte[] and return ROM slices — no per-call GC alloc. Reset before each
        // grain method entry; the rom slices from the previous call become invalid then.
        private readonly Memory.ScratchBufferPool _scratchPool = new(initialCapacity: 8192);
        private SharedMeta.Server.Core.Memory.GrainScopedSerializer? _scopedSerializer;

        // Cached once in OnActivateAsync — GetPrimaryKeyString allocates a fresh
        // string on every call and hot paths read this 5-10× per RPC.
        private string _entityId = string.Empty;

        // Persistence policy tracking
        private int _requestsSinceLastSave;
        private DateTime _lastSaveTime = DateTime.UtcNow;
        private bool _isDirty;

        /// <summary>
        /// Runtime grain references for subscribers.
        /// Reconstructed from persisted PlayerId on activation.
        /// </summary>
        private readonly Dictionary<string, ISessionManagerReference> _subscriberRefs = new();

        // Force-ServerPatch refcounts aggregated across active subscribers. HandleCallAsync
        // consults these to activate patch tracking on dispatches whose declared mode isn't
        // ServerPatch, so the broadcast carries both replay and patch and per-subscriber
        // tailoring can ship only what each player needs.
        //
        // Two granularities, OR'd at dispatch time:
        //   Method-level — driven by per-subscriber capabilities (MinCompatibleVersion).
        //   Service-level — driven by per-entity ConfigStructureBoundary effects.
        //
        // Per-subscriber contribution snapshots so Unsubscribe decrements deterministically
        // without re-asking the session.
        //
        // 0.24.0+ Method-level refcounts are indexed by server-side global MethodId rather
        // than a (Service, Alias, Version) string tuple — ~200 bytes for 100 methods vs
        // ~6 KB of dictionary overhead, and ContainsKey on the RPC hot path becomes a single
        // bounds check + int read. Lazily allocated on first subscribe (depends on signature).
        private int[]? _forcePatchMethodRefs;
        private readonly Dictionary<string, List<ushort>> _subscriberForcePatchContributions = new();
        private readonly Dictionary<string, int> _forcePatchServiceRefs = new();
        private readonly Dictionary<string, List<string>> _subscriberForcePatchServiceContributions = new();

        public EntityGrain(
            [PersistentState("entity", "Default")] IPersistentState<EntityGrainState<TState>> persistentState,
            IMetaProviderFactory<TState> providerFactory,
            IMetaSerializer serializer,
            ILogger<EntityGrain<TState>> logger,
            IOptions<EntityGrainOptions> options,
            IEntityGrainResolver entityGrainResolver,
            MetaServerSignature serverSignature,
            IExecutionModeProvider? executionModeProvider = null,
            IConfigVersionResolver? configVersionResolver = null,
            IClientSignatureRegistry? signatureRegistry = null,
            SharedMeta.Server.Core.Memory.PooledPayloadRegistry? pooledPayloadRegistry = null)
        {
            _persistentState = persistentState ?? throw new ArgumentNullException(nameof(persistentState));
            _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _executionModeProvider = executionModeProvider;
            _configVersionResolver = configVersionResolver;
            _serverSignature = serverSignature;
            _signatureRegistry = signatureRegistry;
            // Optional — host wires the silo-scoped registry when pooled broadcast allocation
            // is enabled. When null, providers fall back to the byte[]-allocating serializer path.
            _pooledPayloadRegistry = pooledPayloadRegistry;
        }

        private readonly SharedMeta.Server.Core.Memory.PooledPayloadRegistry? _pooledPayloadRegistry;

        // ConfigBoundaries + service-to-config bindings. Null = host hasn't wired
        // MetaServerSignature in DI; Subscribe emits no per-entity capability overlay.
        private readonly MetaServerSignature _serverSignature;

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _entityId = this.GetPrimaryKeyString();   // cache once for the grain lifetime
            var entityId = _entityId;
            var state = _persistentState.State;

            _logger.EntityGrainActivated(typeof(TState).Name, entityId);
            _logger.EntityStateLoaded(typeof(TState).Name, entityId, state.Subscribers.Count, state.EntitySequenceNumber);

            SharedMeta.Server.Core.Telemetry.MetricEvents.Grain.Activated(typeof(TState).Name);

            // Prune expired subscribers
            var cutoff = DateTime.UtcNow - _options.SubscriberTtl;
            var expired = state.Subscribers.Where(kv => kv.Value.LastActiveUtc < cutoff).Select(kv => kv.Key).ToList();
            if (expired.Count > 0)
            {
                foreach (var playerId in expired)
                    state.Subscribers.Remove(playerId);

                _logger.ExpiredSubscribersPruned(entityId, expired.Count);
                await _persistentState.WriteStateAsync();
                ResetPersistenceTracking();
            }

            // Reconstruct grain references for surviving subscribers
            foreach (var (playerId, sub) in state.Subscribers)
            {
                _subscriberRefs[playerId] = GrainFactory.GetGrain<ISessionManagerReference>(playerId);
                _logger.SubscriberRestored(entityId, playerId);
            }

            // Create and initialize provider with persisted state
            _provider = _providerFactory.Create();

            // Wire execution mode provider and cross-entity call handler
            if (_provider is MetaProviderBase<TState> providerBase)
            {
                providerBase.ExecutionModeProvider = _executionModeProvider;
                providerBase.EntityCallHandler = async (targetEntityId, methodId, argsBytes, serverTimeTicks) =>
                {
                    var serviceName = _serverSignature.GetMethodEntry(methodId)?.ServiceName;
                    if (serviceName == null) {
                        throw new InvalidOperationException($"Cannot resolve entity grain service name for method {methodId}, entity {targetEntityId}");
                    }
                    // Self-targeting cross-entity call (gift-to-self, sibling service on the
                    // same state): dispatch locally as a nested op instead of routing through
                    // Orleans. EntityGrain is non-reentrant — a self-call through the grain
                    // reference would deadlock against the outer call's held scheduler turn.
                    if (targetEntityId == _entityId)
                    {
                        var resolved = _entityGrainResolver.GetEntityGrainByService(
                            GrainFactory, serviceName, targetEntityId);
                        if (resolved is IEntityGrain<TState>)
                        {
                            return await providerBase.HandleNestedCallAsync(targetEntityId, methodId, argsBytes);
                        }
                    }

                    var targetGrain = _entityGrainResolver.GetEntityGrainByService(
                        GrainFactory, serviceName, targetEntityId);
                    if (targetGrain == null)
                        throw new InvalidOperationException(
                            $"Cannot resolve entity grain for service {serviceName}, entity {targetEntityId}");

                    // Propagate the originating client's app version so the target resolves
                    // schema cap against the same version as the session-level call. Fall back
                    // to the resolver when no MetaContext is present (server-internal call from
                    // a timer / background job / server-only service).
                    var callerClientVersion = SharedMeta.Core.MetaContextAccessor.Current?.CallerClientVersion
                        ?? _configVersionResolver?.CurrentClientVersion;
                    // Propagate CrossOptimistic flag so the target excludes the originating
                    // caller from its broadcast — the effect is already inlined in the outer's
                    // replay payload; a duplicate would double-apply.
                    var callerCtx = SharedMeta.Core.MetaContextAccessor.Current;
                    var callerId = callerCtx?.CallerId;
                    var isCrossOptimistic = callerCtx?.IsCrossOptimistic ?? false;
                    var result = await targetGrain.HandleCallFromEntityAsync(new RpcCall
                    {
                        MethodId = methodId,
                        Payload = argsBytes,
                        ServerTimeTicks = serverTimeTicks,
                        CallerClientVersion = callerClientVersion,
                        CallerId = callerId,
                        IsCrossOptimistic = isCrossOptimistic
                    });

                    if (result.HasError)
                        throw new InvalidOperationException(
                            $"Cross-entity call failed: {result.Error}");

                    // Cross-entity is the one outgoing wire shape where the SENDER IS the
                    // consumer — the target grain produced ResultBytes as a pool-rented
                    // buffer and we (the caller) own the ref-count. Copy the bytes into a
                    // managed byte[] for embedding into our outgoing response/broadcast,
                    // then release the source's pool slot immediately so the slot returns
                    // to the registry within the same call frame (no lifetime races, no
                    // intermediate-tracking machinery).
                    ReadOnlyMemory<byte> resultBytes;
                    if (result.ResultBytes.Ref != 0 && _pooledPayloadRegistry != null
                        && result.ResultBytes.SiloId == _pooledPayloadRegistry.SiloId)
                    {
                        resultBytes = result.ResultBytes.Memory.ToArray();
                        _pooledPayloadRegistry.Release(result.ResultBytes);
                    }
                    else
                    {
                        // Ref==0 (byte[]-backed) or foreign-silo (cross-silo deferred):
                        // just take the Memory as-is.
                        resultBytes = result.ResultBytes.Memory;
                    }

                    return new CrossEntityOperationInfo
                    {
                        EntityId = targetEntityId,
                        EntitySequenceNumber = result.EntitySequenceNumber,
                        MethodId = methodId,
                        ResultBytes = resultBytes
                    };
                };

                // Fire-and-forget cross-entity dispatch for [MetaMethod(OneWay = true)] —
                // routes through the [OneWay]-marked grain entry so the source doesn't wait
                // for the reply envelope.
                providerBase.EntityCallOneWayHandler = (targetEntityId, methodId, argsBytes, serverTimeTicks) =>
                {
                    var serviceName = _serverSignature.GetMethodEntry(methodId)?.ServiceName;
                    if (serviceName == null) {
                        _logger.LogWarning("[EntityGrain] OneWay for {MethodId} dropped: cannot resolve service Name for entity {EntityId}",
                            methodId, targetEntityId);
                        return;
                    }
                    var targetGrain = _entityGrainResolver.GetEntityGrainByService(GrainFactory, serviceName, targetEntityId);
                    if (targetGrain == null)
                    {
                        _logger.LogWarning("[EntityGrain] OneWay {Service}.{MethodId} dropped: cannot resolve grain for entity {EntityId}",
                            serviceName, methodId, targetEntityId);
                        return;
                    }

                    var callerClientVersion = SharedMeta.Core.MetaContextAccessor.Current?.CallerClientVersion
                        ?? _configVersionResolver?.CurrentClientVersion;
                    // Propagate the outer's CrossOptimistic flag so the target excludes the
                    // originating caller from its broadcast (effect is already inlined in the
                    // outer call's replay payload, would double-apply on duplicate).
                    var callerCtx = MetaContextAccessor.Current;
                    var callerId = callerCtx?.CallerId;
                    var isCrossOptimistic = callerCtx?.IsCrossOptimistic ?? false;
                    var rpcCall = new RpcCall
                    {
                        MethodId = methodId,
                        Payload = argsBytes,
                        ServerTimeTicks = serverTimeTicks,
                        CallerClientVersion = callerClientVersion,
                        CallerId = callerId,
                        IsCrossOptimistic = isCrossOptimistic
                    };

                    // Discard is intentional — [OneWay] on the target's entry suppresses the
                    // reply envelope; the Task returned here is for the local dispatch only.
                    _ = targetGrain.HandleCallFromEntityOneWayAsync(rpcCall);
                };

                providerBase.EntityStateHandler = async (targetEntityId, stateTypeName) =>
                {
                    var targetGrain = _entityGrainResolver.GetEntityGrain(
                        GrainFactory, stateTypeName, targetEntityId);
                    if (targetGrain == null)
                        return null;

                    return await targetGrain.GetEntityStateAsync();
                };

                providerBase.SaveStateHandler = async () =>
                {
                    PersistRandomBytes();
                    await _persistentState.WriteStateAsync();
                    ResetPersistenceTracking();
                    _logger.PersistenceForced(entityId, _requestsSinceLastSave);
                };

                // 0.24.0+ Forward the server signature so the provider can do reverse
                // methodId → (Service, Alias, Version) resolution for diagnostics. Trigger
                // method ids no longer need a runtime lookup — the dispatcher emits
                // GameMethodIds constants directly into DispatchResult.TriggersToExecute.
                if (_serverSignature != null)
                {
                    providerBase.ServerSignature = _serverSignature;
                }

                // Forward optional global seed factory so MetaProviderBase.CreateFreshRandomSeed
                // can mix in non-deterministic entropy when host opts in. Must be set BEFORE
                // Initialize() — Initialize seeds fresh randoms during its body.
                providerBase.FreshRandomSeedFactory = _options.FreshRandomSeedFactory;
            }

            // Wrap the silo-singleton IMetaSerializer with a per-grain GrainScopedSerializer
            // that routes Pack<T>(T) through this grain's scratch pool — every intermediate
            // serialization (dispatcher result, patch tree, state pack, triggers) goes into
            // pool-rented memory instead of allocating a fresh byte[] per call.
            _scopedSerializer = new Memory.GrainScopedSerializer(_serializer, _scratchPool);
            var context = new MetaProviderContext(entityId, _scopedSerializer, GrainFactory, _logger, state.NamedRandomsBytes, _pooledPayloadRegistry);
            _provider.Initialize(context, state.UserState, state.ServerRandomBytes, state.OptimisticRandomBytes);

            // Apply global deep desync override from EntityGrainOptions
            if (_options.DeepDesyncEnabled.HasValue && _provider is MetaProviderBase<TState> ddProvider)
                ddProvider.DeepDesyncEnabled = _options.DeepDesyncEnabled.Value;

            // Seed the provider's schema version from persisted state.Version so the
            // fresh-entity-floor rule doesn't re-run [MetaInit] on every activation.
            // Migration itself is deferred to Subscribe/HandleCall (client version unknown here).
            if (_provider is MetaProviderBase<TState> seedProvider)
                seedProvider.SeedSchemaVersion(state.Version);

            // [MetaInit] migration is intentionally NOT run here. The client triggering
            // activation isn't known yet, so we can't pick a config branch to migrate to.
            // Migration is deferred to SubscribeAsync (capped to subscriber's branch) and
            // HandleCallAsync (lazy, capped to call.CallerClientVersion). Fresh entities
            // run base init through the same path via the "fresh entity floor" rule.

            ResetPersistenceTracking();

            await base.OnActivateAsync(cancellationToken);
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            var entityId = _entityId;
            _logger.EntityGrainDeactivating(typeof(TState).Name, entityId);

            _provider?.OnDeactivating();

            // Only persist if there were actual player interactions
            if (_isDirty)
            {
                await _persistentState.WriteStateAsync();
                _logger.EntityStatePersisted(entityId);
            }

            SharedMeta.Server.Core.Telemetry.MetricEvents.Grain.Deactivated(
                typeof(TState).Name, reason.ReasonCode.ToString());

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        public async Task<EntitySnapshot> SubscribeAsync(string playerId, ISessionManagerReference sessionManager, string? clientVersion = null, ulong clientSignatureHash = 0)
        {
            using var __m = SharedMeta.Server.Core.Telemetry.SubscribeMeasurement.Start(typeof(TState).Name, _entityId, playerId);
            try
            {
            // Access policy check
            if (_provider != null)
            {
                var policy = _provider.AccessPolicy;
                if (policy != EntityAccessPolicy.Open)
                {
                    bool allowed;
                    if (policy is EntityAccessPolicy.OwnerOnly or EntityAccessPolicy.UserOwned)
                        allowed = _entityId == playerId;
                    else // Authorized
                        allowed = await _provider.CheckAccessAsync(playerId);

                    if (!allowed)
                    {
                        _logger.EntityAccessDenied(_entityId, playerId, policy.ToString());
                        throw new EntityAccessDeniedException(
                            $"Player '{playerId}' is not authorized to access entity '{_entityId}'");
                    }
                }
            }

            var state = _persistentState.State;

            // Drive init/migration capped to the subscriber's branch BEFORE the compatibility
            // gate, so a 1.x client joining a fresh entity never causes a 2.0 schema jump.
            // For Global scope migration tracks the server's CurrentClientVersion instead of
            // the joiner's version — Global entities progress with server policy, not joiners.
            if (_provider is MetaProviderBase<TState> mpbInit)
            {
                // Non-Global scope normally drives migration with the subscriber's clientVersion;
                // when it's missing (resubscribe path where the original version wasn't captured,
                // legacy callers) fall back to the server's CurrentClientVersion so
                // ResolveClientConfigVersion doesn't throw "clientAppVersion is required".
                var migrationDriverVersion = mpbInit.Scope == EntityScope.Global
                    ? (_configVersionResolver?.CurrentClientVersion ?? clientVersion)
                    : (!string.IsNullOrEmpty(clientVersion) ? clientVersion : _configVersionResolver?.CurrentClientVersion);
                var clientCfg = mpbInit.ResolveClientConfigVersion(migrationDriverVersion);
                // Async variant: providers may need to fetch bytes across a grain boundary
                // (e.g. BroadcastingConfigProvider on cold-cache version).
                await mpbInit.InitializeConfigAsync(clientCfg);
                var cap = mpbInit.ComputeSchemaCapForClient(migrationDriverVersion);
                if (await mpbInit.RunInitOrMigrateAsync(migrationDriverVersion, cap))
                {
                    state.Version = mpbInit.LazyMigrationNewVersion;
                    mpbInit.LazyMigrationCompleted = false;
                    PersistRandomBytes();
                    await _persistentState.WriteStateAsync();
                    ResetPersistenceTracking();
                    _logger.EntityStateInitialized(typeof(TState).Name, _entityId, state.Version);
                }
            }

            // Reject clients whose resolved config version is below the minimum required for
            // the entity's current state schema — but only when the schema bump was marked
            // Breaking=true on [MetaStateVersion]. Non-breaking bumps let old clients subscribe
            // and rely on VersionTolerant deserialization to skip new fields.
            if (_provider != null)
            {
                var resolvedConfigVersion = _provider.ResolveClientConfigVersion(clientVersion);
                if (!_provider.IsClientConfigCompatible(resolvedConfigVersion))
                {
                    _logger.LogWarning(
                        "[EntityGrain] Subscribe rejected (breaking schema): entity={EntityId} player={PlayerId} " +
                        "clientConfig={ConfigVersion} is below the minimum required for the current state schema.",
                        _entityId, playerId, resolvedConfigVersion);
                    // Throw structured exception so SessionManagerGrain / MetaConnectionHandler
                    // can propagate FeatureRequirement to the client via SubscribeResponse.
                    throw new IncompatibleFeatureException(new FeatureRequirement
                    {
                        FeatureKind = "State",
                        Identifier = typeof(TState).FullName ?? typeof(TState).Name,
                        MinRequiredVersion = resolvedConfigVersion.ToString(),
                        Reason = "State schema introduced a structural change ([MetaStateVersion(..., Breaking = true)]). Update the client to use this entity.",
                    });
                }
            }

            // Establish runtime config-version pin (first subscriber) or validate against
            // an existing pin (subsequent Shared joiners). Global scope never pins — it
            // resolves freshly from IConfigVersionResolver.CurrentClientVersion every call.
            // Patch differences tolerated on Shared (joiner downgrades to pinned patch);
            // Major.Minor mismatch rejects so mixed-branch clients can't share a session.
            if (_provider is MetaProviderBase<TState> mpbPin && mpbPin.Scope != EntityScope.Global)
            {
                if (mpbPin.ActiveConfigPins.Count == 0)
                {
                    mpbPin.EstablishConfigPinsFromClientVersion(clientVersion);
                }
                else if (mpbPin.Scope == EntityScope.Shared
                    && !mpbPin.ValidateClientCompatibleWithPins(clientVersion, out var pinReason))
                {
                    _logger.LogWarning(
                        "[EntityGrain] Shared-session pin mismatch: entity={EntityId} player={PlayerId} reason={Reason}",
                        _entityId, playerId, pinReason);
                    throw new EntityAccessDeniedException(
                        $"Cannot join this shared session — your app version is on a different config branch. {pinReason}");
                }
            }

            state.Subscribers[playerId] = new PersistedSubscriberInfo
            {
                PlayerId = playerId,
                LastActiveUtc = DateTime.UtcNow
            };
            _subscriberRefs[playerId] = sessionManager;

            // 0.24.0+ Resolve this subscriber's force-patch declarations from the silo-local
            // annotation cache. Walk Statuses[] for ForceServerPatch entries and translate
            // each client method id to its server-side counterpart via the clientToServer map.
            // Foreign entries can't false-trigger because the translation drops anything the
            // server doesn't know (clientToServer[i] == ushort.MaxValue).
            ClientSignatureAnnotated? annotated = null;
            ushort[]? clientToServer = null;
            if (clientSignatureHash != 0 && _signatureRegistry != null)
            {
                annotated = await _signatureRegistry.TryGetAnnotatedAsync(clientSignatureHash);
                clientToServer = await _signatureRegistry.TryGetClientToServerMapAsync(clientSignatureHash);
            }

            // Always drop prior contributions unconditionally — even when the new list is
            // null/empty — otherwise refcounts drift permanently upward when a resubscribe
            // brings a smaller force-patch set (e.g. client upgraded out of legacy path).
            if (_subscriberForcePatchContributions.Remove(playerId, out var prior))
            {
                foreach (var id in prior)
                    DecrementMethodRef(id);
            }
            if (annotated != null && clientToServer != null && _serverSignature != null)
            {
                EnsureForcePatchRefs();
                var contributions = new List<ushort>();
                var statuses = annotated.Statuses;
                int max = statuses.Length < clientToServer.Length ? statuses.Length : clientToServer.Length;
                for (ushort clientId = 0; clientId < max; clientId++)
                {
                    if (statuses[clientId] != MethodStatus.ForceServerPatch) continue;
                    var serverId = clientToServer[clientId];
                    if (serverId == ushort.MaxValue) continue;  // signature drift — server doesn't know this method
                    contributions.Add(serverId);
                    IncrementMethodRef(serverId);
                }
                if (contributions.Count > 0)
                    _subscriberForcePatchContributions[playerId] = contributions;
            }

            // Compute per-entity capability overlay from [MetaConfigStructureBoundary]:
            // which services on this entity need force-patch for this specific subscriber.
            // Stored in the local refcount (HandleCallAsync activates patch tracking) AND
            // shipped on the snapshot (client gates the call locally without round trip).
            // Same unconditional-drop pattern as method-level above.
            var augmentedCaps = ComputePerEntityCapabilities(clientVersion);
            if (_subscriberForcePatchServiceContributions.Remove(playerId, out var priorSvc))
            {
                foreach (var svc in priorSvc)
                    DecrementServiceRef(svc);
            }
            if (augmentedCaps is { ForceServerPatchServices.Count: > 0 })
            {
                var svcContributions = new List<string>(augmentedCaps.ForceServerPatchServices.Count);
                foreach (var svc in augmentedCaps.ForceServerPatchServices)
                {
                    svcContributions.Add(svc);
                    IncrementServiceRef(svc);
                }
                _subscriberForcePatchServiceContributions[playerId] = svcContributions;
            }

            _logger.PlayerSubscribed(_entityId, playerId);

            await _persistentState.WriteStateAsync();
            ResetPersistenceTracking();

            // Snapshot StateBytes is NOT pool-tracked: Orleans's in-silo copier shares the
            // ROM by reference (no defensive copy of the inner byte[]) for cross-grain hops,
            // so releasing the pool slot at the next HandleCallAsync entry would recycle the
            // buffer while the receiving SessionManager / client still references the same
            // bytes. Allocate a fresh byte[] for the snapshot — the cost is a single state
            // serialization per subscribe (not on the per-RPC hot path).
            ReadOnlyMemory<byte> stateBytes = _provider?.GetStateBytes() ?? _serializer.Pack(state.UserState);

            var namedBytes = _provider?.GetNamedRandomsBytes();
            return new EntitySnapshot
            {
                StateBytes = stateBytes,
                CurrentSequenceNumber = state.EntitySequenceNumber,
                OptimisticRandomBytes = _provider?.GetOptimisticRandomBytes(),
                NamedRandomsBytes = namedBytes is { Length: > 0 } ? namedBytes : null,
                // Scope-aware effective version: pinned for Private/Shared, resolver-driven
                // for Global — so the client materializes the same config the server will
                // dispatch under, not its own natural branch.
                ConfigVersion = (_provider as MetaProviderBase<TState>)?.ResolveEffectiveConfigVersion(clientVersion)
                               ?? _provider?.ResolveClientConfigVersion(clientVersion)
                               ?? default,
                AugmentedCapabilities = augmentedCaps,
            };
            }
            catch
            {
                __m.MarkError();
                throw;
            }
        }

        // Delegates the boundary check to ConfigBoundaryEvaluator (pure, unit-tested).
        // Returns null when no boundaries apply.
        private SharedMeta.Core.Transport.EntityAugmentedCapabilities? ComputePerEntityCapabilities(string? clientVersion)
        {
            if (_serverSignature == null
                || _serverSignature.ConfigBoundaries == null
                || _serverSignature.ConfigBoundaries.Count == 0
                || _provider == null)
                return null;

            // Two versions in play:
            //   * pinned     — what the server executes under (Private/Shared pin, or Global's
            //                  CurrentClientVersion resolution). The bytes on the wire reflect this.
            //   * clientCode — what the client's code was built to interpret, derived purely
            //                  from its ClientVersion via [MetaConfigVersion] rules. Unclamped
            //                  by the entity pin — it's the client's natural schema.
            var pinned = (_provider as MetaProviderBase<TState>)?.ResolveEffectiveConfigVersion(clientVersion)
                       ?? _provider.ResolveClientConfigVersion(clientVersion);
            var clientCode = _provider.ResolveClientConfigVersion(clientVersion);

            var services = SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices(
                _serverSignature.ConfigBoundaries, _serverSignature.Methods, pinned, clientCode);
            if (services.Count == 0) return null;

            return new SharedMeta.Core.Transport.EntityAugmentedCapabilities
            {
                ForceServerPatchServices = services
                // RejectedServices stays empty — boundaries only trigger force-patch, not reject.
                // A future severity enum on [MetaConfigStructureBoundary] could populate this.
            };
        }

        public async Task UnsubscribeAsync(string playerId)
        {
            // Telemetry: decrement subscriber gauge regardless of whether the player was
            // actually present (idempotency — UnsubscribeAsync may fire from disposed sessions).
            if (_persistentState.State.Subscribers.ContainsKey(playerId))
            {
                SharedMeta.Server.Core.Telemetry.MetricEvents.Subscriber.Removed(typeof(TState).Name);
            }
            _persistentState.State.Subscribers.Remove(playerId);
            _subscriberRefs.Remove(playerId);

            // Decrement force-patch refcounts for this player's prior contributions.
            if (_subscriberForcePatchContributions.Remove(playerId, out var contributions))
            {
                foreach (var id in contributions)
                    DecrementMethodRef(id);
            }
            if (_subscriberForcePatchServiceContributions.Remove(playerId, out var svcContributions))
            {
                foreach (var svc in svcContributions)
                    DecrementServiceRef(svc);
            }

            _logger.PlayerUnsubscribed(_entityId, playerId);

            // Pin lives only while there are active subscribers. When the last leaves,
            // drop pins so the next first-subscriber re-establishes fresh — picks up any
            // patch published while the entity was effectively idle.
            if (_persistentState.State.Subscribers.Count == 0
                && _provider is MetaProviderBase<TState> mpbClear
                && mpbClear.ActiveConfigPins.Count > 0)
            {
                mpbClear.ClearConfigPins();
                _logger.LogDebug(
                    "[EntityGrain] Cleared config pins on '{EntityId}' — no active subscribers remain.",
                    _entityId);
            }

            await _persistentState.WriteStateAsync();
            ResetPersistenceTracking();
        }

        public async ValueTask<EntityCallResult> HandleCallAsync(RpcCall call)
        {
            if (_provider == null)
            {
                return new EntityCallResult { Error = "Provider not initialized" };
            }

            // 0.24.0+ Resolve names once via the server signature — RpcCall no longer carries
            // the strings. Empty strings when the id is unknown; downstream sites must tolerate
            // that (logs / telemetry / refcount lookup all degrade gracefully).
            var sigEntry = _serverSignature?.GetMethodEntry(call.MethodId);
            var callServiceName = sigEntry?.ServiceName ?? string.Empty;
            var callMethodName = sigEntry?.Alias ?? string.Empty;

            using var __m = SharedMeta.Server.Core.Telemetry.RpcMeasurement.Start(
                SharedMeta.Server.Core.Telemetry.SharedMetaActivities.SpanEntityRpc,
                callServiceName, callMethodName, _entityId, call.CallerId, call.Payload.Length);

            var state = _persistentState.State;
            var operationSequence = ++state.EntitySequenceNumber;
            var forcePersist = false;

            // Update caller's last active time
            if (call.CallerId != null && state.Subscribers.TryGetValue(call.CallerId, out var callerSub))
                callerSub.LastActiveUtc = DateTime.UtcNow;

            try
            {
                // Activate patch tracking when any subscriber needs the patch fallback
                // (method-level MinCompatibleVersion mismatch OR service-level
                // ConfigStructureBoundary trigger). The resulting broadcast carries BOTH
                // ReplayPayload and PatchBytes; per-subscriber tailoring strips one on fan-out.
                bool requirePatchForFanOut =
                       (_forcePatchMethodRefs != null && call.MethodId < _forcePatchMethodRefs.Length && _forcePatchMethodRefs[call.MethodId] > 0)
                    || _forcePatchServiceRefs.ContainsKey(callServiceName);
                if (requirePatchForFanOut)
                {
                    SharedMeta.Server.Core.Telemetry.MetricEvents.Compat.ForcePatchApplied(
                        callServiceName, callMethodName,
                        _forcePatchServiceRefs.ContainsKey(callServiceName) ? "service" : "method");
                }

                // isClientOriginated: true → provider rejects [MetaMethod(GenerateClientApi=false)]
                // methods. Cross-entity peers land at HandleCallFromEntityAsync below with false.
                var providerResult = await _provider.HandleCallAsync(call, isClientOriginated: true, requirePatchForFanOut: requirePatchForFanOut);
                forcePersist = providerResult.ForcePersist;

                // Take outgoing pool-tracked payloads from the provider. Sender (us) does NOT
                // release these — receiving grains (SessionManager / EntityGrain across silos)
                // own them and release via PooledPayloadRegistry once the buffer crosses the
                // grain boundary. Fan-out IncrementRef happens inside DistributeBroadcasts
                // before the Orleans hop. Provider's FlushPendingOutgoing at next call entry
                // is the safety net for tokens we forget to take.
                SharedMeta.Core.Memory.PooledPayload responsePayload = default;
                SharedMeta.Core.Memory.PooledPayload replayPayload = default;
                SharedMeta.Core.Memory.PooledPayload patchPayload = default;
                if (_provider is MetaProviderBase<TState> mpbTake)
                {
                    responsePayload = mpbTake.TakeOutgoingResponse();
                    replayPayload = mpbTake.TakeOutgoingBroadcastReplay();
                    patchPayload = mpbTake.TakeOutgoingBroadcastPatch();
                }

                // Lazy migration: if CheckAndRunLazyMigrationAsync ran a migration, persist the
                // updated state.Version so the schema advance is durable before the next call.
                if (_provider is MetaProviderBase<TState> mpb && mpb.LazyMigrationCompleted)
                {
                    state.Version = mpb.LazyMigrationNewVersion;
                    mpb.LazyMigrationCompleted = false;
                    forcePersist = true;
                }

                PersistRandomBytes();

                await DistributeBroadcasts(
                    replayPayload,
                    patchPayload,
                    callServiceName, callMethodName, call.MethodId,
                    operationSequence, excludePlayerId: call.CallerId);

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    OpBytes = responsePayload,
                    CrossEntityCalls = providerResult.CrossEntityCalls,
                    Error = providerResult.Error
                };
            }
            catch (Exception ex)
            {
                __m.MarkError();
                _logger.ErrorHandlingCall(ex);
                System.Console.WriteLine($"[EG.HandleCallAsync] {_entityId} EXCEPTION: {ex}");
                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message
                };
            }
            finally
            {
                // Outgoing pool tokens are NOT released here — receivers own them once the
                // payload crosses the grain boundary. Untaken tokens (e.g. exception path) are
                // released as a safety net by MetaProviderBase.FlushPendingOutgoing at the
                // next provider entry.
                // Lifecycle work only — telemetry flushes via RpcMeasurement.Dispose.
                await PersistIfNeeded(forcePersist);
            }
        }

        // SECURITY INVARIANT: this entry point must NEVER be reached by direct client traffic.
        // It is intentionally not gated by IsClientCallable because cross-entity calls are
        // server-internal — the caller is another grain that has already authorized the
        // originating client through its own public API. Transports (SignalR MetaHub, HTTP
        // polling, etc.) must route client packets only to HandleCallAsync / HandleQueryAsync /
        // HandleSignalAsync. Adding any client-reachable wiring to HandleCallFromEntityAsync
        // would silently bypass the [MetaMethod(GenerateClientApi = false)] protection.
        public async ValueTask<CrossEntityCallReturn> HandleCallFromEntityAsync(RpcCall call)
        {
            if (_provider == null)
            {
                return new CrossEntityCallReturn { Error = "Provider not initialized" };
            }

            // 0.24.0+ Resolve names from the server signature — cross-entity peers already
            // populated call.MethodId on the recorder side, so no string fallback is needed.
            var sigEntry = _serverSignature?.GetMethodEntry(call.MethodId);
            var callServiceName = sigEntry?.ServiceName ?? string.Empty;
            var callMethodName = sigEntry?.Alias ?? string.Empty;

            // kind="normal" splits the awaited cross-entity path from the [OneWay] notification
            // path (kind="notification") in the same histogram.
            using var __m = SharedMeta.Server.Core.Telemetry.CrossEntityCallMeasurement.Start(
                callServiceName, callMethodName, _entityId, "normal");

            var state = _persistentState.State;
            var operationSequence = ++state.EntitySequenceNumber;
            var forcePersist = false;

            try
            {
                // isClientOriginated: false → cross-entity calls are server-internal; the
                // calling entity's public method already authorized the originating client
                // through its own access policy. [MetaMethod(GenerateClientApi=false)] methods
                // are reachable here.
                var providerResult = await _provider.HandleCallAsync(call, isClientOriginated: false);
                forcePersist = providerResult.ForcePersist;

                SharedMeta.Core.Memory.PooledPayload resultPayload = default;
                SharedMeta.Core.Memory.PooledPayload replayPayload = default;
                SharedMeta.Core.Memory.PooledPayload patchPayload = default;
                if (_provider is MetaProviderBase<TState> mpbTake)
                {
                    resultPayload = mpbTake.TakeOutgoingResult();
                    replayPayload = mpbTake.TakeOutgoingBroadcastReplay();
                    patchPayload = mpbTake.TakeOutgoingBroadcastPatch();
                }

                PersistRandomBytes();

                // Conditional caller exclusion:
                //   * CrossOptimistic outer call — exclude the originating client. The cross-call's
                //     effect on this target is already inlined in the outer call's replay payload
                //     (CrossEntityCallInfo.ResultBytes + inner replay tape), so a duplicate
                //     broadcast would double-apply on the caller's client.
                //   * Non-CrossOptimistic outer call — caller did NOT execute the cross-call
                //     locally, relies on this broadcast to learn the target's state change.
                var excludeCaller = call.IsCrossOptimistic ? call.CallerId : null;
                await DistributeBroadcasts(
                    replayPayload,
                    patchPayload,
                    callServiceName, callMethodName, call.MethodId,
                    operationSequence, excludePlayerId: excludeCaller);

                // Slim return — source grain only reads ResultBytes / EntitySequenceNumber.
                // ResultBytes is the SOURCE grain's responsibility to release (we are the
                // receiver of this outgoing token).
                return new CrossEntityCallReturn
                {
                    EntitySequenceNumber = operationSequence,
                    ResultBytes = resultPayload,
                    Error = providerResult.Error,
                };
            }
            catch (Exception ex)
            {
                __m.MarkError();
                _logger.ErrorHandlingCrossEntityCall(ex);
                return new CrossEntityCallReturn
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message,
                };
            }
            finally
            {
                // Outgoing pool tokens are NOT released here — receivers own them.
                // Lifecycle work only — telemetry flushes via CrossEntityCallMeasurement.Dispose.
                await PersistIfNeeded(forcePersist);
            }
        }

        // Fire-and-forget cross-entity entry: same body as HandleCallFromEntityAsync, result
        // discarded, exceptions logged-only since the caller dispatched via Orleans [OneWay].
        // Broadcasts still reach THIS entity's subscribers normally.
        public async Task HandleCallFromEntityOneWayAsync(RpcCall call)
        {
            // Notification-kind count is recorded both here and as "normal" inside
            // HandleCallFromEntityAsync — intentional, gives "outer dispatch" and "inner
            // execution" perspectives in the same histogram.
            SharedMeta.Server.Core.Telemetry.MetricEvents.CrossEntity.OneWayDispatched(
                _serverSignature?.GetMethodEntry(call.MethodId)?.ServiceName ?? "?");
            try
            {
                await HandleCallFromEntityAsync(call);
            }
            catch (Exception ex)
            {
                _logger.ErrorHandlingCrossEntityCall(ex);
            }
        }

        public async ValueTask<QueryCallResponse> HandleQueryAsync(RpcCall call)
        {
            if (_provider == null)
                return new QueryCallResponse { Error = "Provider not initialized" };

            // Method-level (IsQueryMethod / IsClientCallable / IsOpenAccessQuery) and
            // entity-level (AccessPolicy / CheckAccessAsync) validation lives inside
            // MetaProviderBase.HandleQueryAsync. EntityGrain just routes. 0.24.0+ RpcCall
            // carries only MethodId; the MetaConnectionHandler already translated client→server
            // and rejected unknown ids before reaching the grain.
            try
            {
                return await _provider.HandleQueryAsync(call);
            }
            catch (Exception ex)
            {
                _logger.ErrorHandlingCall(ex);
                return new QueryCallResponse { Error = ex.Message };
            }
        }

        public async Task HandleSignalAsync(RpcCall call)
        {
            if (_provider == null)
            {
                _logger.ErrorHandlingCall(new InvalidOperationException("Provider not initialized"));
                return;
            }

            // Method-level (IsSignalMethod / IsClientCallable) and entity-level (AccessPolicy /
            // CheckAccessAsync) validation lives inside MetaProviderBase.HandleSignalAsync.
            // Fire-and-forget by contract: provider errors are logged and swallowed there.
            // 0.24.0+ RpcCall carries only MethodId; MetaConnectionHandler resolved the wire id.
            await _provider.HandleSignalAsync(call);
        }

        public Task<byte[]?> GetEntityStateAsync()
        {
            var userState = _persistentState.State.UserState;
            return Task.FromResult<byte[]?>(_serializer.Pack(userState).ToArray());
        }

        public async Task<bool> ForceMigrateToFloorAsync(string floorClientVersion)
        {
            if (_provider is not MetaProviderBase<TState> mpb) return false;
            // Drives the standard migration pipeline under the floor's resolved client version.
            // Per-step [MetaInit] runs under each transition config (set by the framework).
            var migrated = await mpb.ForceMigrateToFloorAsync(floorClientVersion);
            if (!migrated) return false;
            var state = _persistentState.State;
            state.Version = mpb.LazyMigrationNewVersion;
            mpb.LazyMigrationCompleted = false;
            PersistRandomBytes();
            await _persistentState.WriteStateAsync();
            ResetPersistenceTracking();
            _logger.EntityStateInitialized(typeof(TState).Name, _entityId, state.Version);
            return true;
        }

        public async ValueTask<EntityCallResult> HandleExternalEventAsync(
            ushort methodId,
            byte[] eventData,
            string? callerId = null)
        {
            if (_provider == null)
            {
                return new EntityCallResult { Error = "Provider not initialized" };
            }

            var state = _persistentState.State;
            var operationSequence = ++state.EntitySequenceNumber;

            try
            {
                var providerResult = await _provider.HandleExternalEventAsync(methodId, eventData, callerId);

                SharedMeta.Core.Memory.PooledPayload eventPayload = default;
                if (_provider is MetaProviderBase<TState> mpbTake)
                    eventPayload = mpbTake.TakeOutgoingEventBroadcast();

                // The event payload is consumed by TWO classes of receivers:
                //   1. Subscribers via DistributeBroadcasts (each releases its share — refcount
                //      management is internal to DistributeBroadcasts via IncrementRef(N-1)).
                //   2. The originating framework caller via EntityCallResult.OpBytes (their
                //      SessionManager releases through CallResultToSessionOp).
                // Bump refcount by 1 BEFORE DistributeBroadcasts so the caller's release also
                // has a share to consume — otherwise DistributeBroadcasts' subscriber-side
                // releases bring refcount to 0 and the caller's release fires on a freed slot.
                if (eventPayload.Ref != 0 && _pooledPayloadRegistry != null)
                    _pooledPayloadRegistry.IncrementRef(eventPayload, 1);

                await DistributeBroadcasts(
                    eventPayload,
                    default,
                    string.Empty, string.Empty, methodId,
                    operationSequence, excludePlayerId: null);

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    OpBytes = eventPayload,  // identical shape for the originator
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorHandlingExternalEvent(ex);
                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message
                };
            }
            finally
            {
                await PersistIfNeeded(forcePersist: false);
            }
        }

        /// <summary>
        /// Determines whether state should be persisted based on the configured policy.
        /// </summary>
        private bool ShouldPersist(bool forcePersist)
        {
            if (forcePersist) return true;
            if (!_isDirty) return false;

            var policy = _options.PersistencePolicy;
            return policy.Mode switch
            {
                PersistenceMode.EveryCall => true,
                PersistenceMode.EveryNRequests => _requestsSinceLastSave >= policy.RequestInterval,
                PersistenceMode.EveryNMinutes =>
                    (DateTime.UtcNow - _lastSaveTime).TotalMinutes >= policy.TimeIntervalMinutes,
                PersistenceMode.RequestsOrTime =>
                    _requestsSinceLastSave >= policy.RequestInterval
                    || (DateTime.UtcNow - _lastSaveTime).TotalMinutes >= policy.TimeIntervalMinutes,
                PersistenceMode.OnDeactivationOnly => false,
                _ => true // Unknown mode: safe default
            };
        }

        /// <summary>
        /// Persist state if the policy allows it. Always updates tracking counters.
        /// Non-async outer: when persistence is debounced (the dominant case under load —
        /// e.g. PersistencePolicy.EveryNMinutes(10) means most RPCs skip the write), we
        /// return `default` ValueTask without constructing a state-machine struct.
        /// </summary>
        private ValueTask PersistIfNeeded(bool forcePersist)
        {
            _requestsSinceLastSave++;
            _isDirty = true;

            if (!ShouldPersist(forcePersist))
            {
                if (_options.PersistencePolicy.Mode != PersistenceMode.EveryCall)
                {
                    _logger.PersistenceDeferred(_entityId,
                        _requestsSinceLastSave, _options.PersistencePolicy.Mode.ToString());
                }
                return default;
            }

            return new ValueTask(PersistIfNeededImpl(forcePersist));
        }

        private async Task PersistIfNeededImpl(bool forcePersist)
        {
            using (SharedMeta.Server.Core.Telemetry.PersistenceWriteMeasurement.Start(typeof(TState).Name))
            {
                await _persistentState.WriteStateAsync();
            }
            var entityId = _entityId;
            if (forcePersist || _options.PersistencePolicy.Mode != PersistenceMode.EveryCall)
                _logger.PersistenceForced(entityId, _requestsSinceLastSave);
            ResetPersistenceTracking();
        }

        private void ResetPersistenceTracking()
        {
            _requestsSinceLastSave = 0;
            _lastSaveTime = DateTime.UtcNow;
            _isDirty = false;
        }

        // Force-patch refcount helpers — indexed by server-side global MethodId.
        // The array is sized to <c>_serverSignature.Methods.Count</c> on first subscribe.

        private void EnsureForcePatchRefs()
        {
            if (_forcePatchMethodRefs == null && _serverSignature != null)
                _forcePatchMethodRefs = new int[_serverSignature.Methods.Count];
        }

        private void IncrementMethodRef(ushort methodId)
        {
            if (_forcePatchMethodRefs == null || methodId >= _forcePatchMethodRefs.Length) return;
            _forcePatchMethodRefs[methodId]++;
        }

        private void DecrementMethodRef(ushort methodId)
        {
            if (_forcePatchMethodRefs == null || methodId >= _forcePatchMethodRefs.Length) return;
            if (_forcePatchMethodRefs[methodId] > 0) _forcePatchMethodRefs[methodId]--;
        }

        private void IncrementServiceRef(string serviceName)
        {
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(_forcePatchServiceRefs, serviceName, out _);
            count++;
        }

        private void DecrementServiceRef(string serviceName)
        {
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrNullRef(_forcePatchServiceRefs, serviceName);
            if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref count)) return;
            if (count <= 1) _forcePatchServiceRefs.Remove(serviceName);
            else count--;
        }

        private void PersistRandomBytes()
        {
            if (_provider == null)
                return;
            var state = _persistentState.State;
            state.ServerRandomBytes = _provider.GetServerRandomBytes();
            state.OptimisticRandomBytes = _provider.GetOptimisticRandomBytes();
            var namedBytes = _provider.GetNamedRandomsBytes();
            state.NamedRandomsBytes = namedBytes.Length > 0 ? namedBytes : null;
        }

        // Fan-out lifecycle (0.25.x pool path):
        //   * Each variant arrives from the provider with refcount=1 (creator's reference).
        //   * Before the fan-out loop we pre-count N = subscribers receiving that variant and
        //     IncrementRef(N-1) → refcount=N. Each receiver's SessionManagerGrain Releases its
        //     share once the broadcast has been delivered / evicted; net refcount → 0.
        //   * When N == 0 for a variant (no subscriber consumes it) we Release the creator's
        //     reference here so the slot returns to the pool — no consumer will ever do it.
        //   * Ref==0 tokens are byte[]-backed fallbacks (no registry slot); skip IncrementRef
        //     and Release for them entirely.
        // EntityBroadcast carries PooledPayload by reference (Data is wrapped in
        // Orleans.Concurrency.Immutable so in-silo hops share-by-reference; no deep copy).
        private ValueTask DistributeBroadcasts(
            SharedMeta.Core.Memory.PooledPayload replayPayload,
            SharedMeta.Core.Memory.PooledPayload patchPayload,
            string serviceName, string methodName, ushort methodId,
            long operationSequence, string? excludePlayerId)
        {
            bool replayEmpty = replayPayload.IsEmpty;
            bool patchEmpty = patchPayload.IsEmpty;
            if (replayEmpty && patchEmpty)
                return default;
            if (_subscriberRefs.Count == 0)
            {
                ReleaseUnusedVariant(replayPayload);
                ReleaseUnusedVariant(patchPayload);
                return default;
            }
            if (excludePlayerId != null && _subscriberRefs.Count == 1 && _subscriberRefs.ContainsKey(excludePlayerId))
            {
                ReleaseUnusedVariant(replayPayload);
                ReleaseUnusedVariant(patchPayload);
                return default;
            }
            return new ValueTask(DistributeBroadcastsImpl(replayPayload, patchPayload, serviceName, methodName, methodId, operationSequence, excludePlayerId));
        }

        // Release the creator's reference on a variant that won't be fanned out to any consumer.
        // Ref==0 means byte[]-backed fallback (no slot to release).
        private void ReleaseUnusedVariant(SharedMeta.Core.Memory.PooledPayload payload)
        {
            if (payload.Ref == 0 || _pooledPayloadRegistry == null) return;
            _pooledPayloadRegistry.Release(payload);
        }

        private async Task DistributeBroadcastsImpl(
            SharedMeta.Core.Memory.PooledPayload replayPayload,
            SharedMeta.Core.Memory.PooledPayload patchPayload,
            string serviceName, string methodName, ushort methodId,
            long operationSequence, string? excludePlayerId)
        {
            var entityId = _entityId;
            var subscriberCount = _subscriberRefs.Count;

            _logger.DistributingBroadcasts(entityId, operationSequence, 1, subscriberCount, excludePlayerId ?? "none");

            bool replayAvailable = !replayPayload.IsEmpty;
            bool patchAvailable = !patchPayload.IsEmpty;

            // Pre-pass: count how many subscribers will consume each variant so we can balance
            // ref-counts BEFORE the fan-out (IncrementRef(N-1) → refcount=N → N receivers each
            // Release → 0). Without this we'd either leak slots (no IncrementRef) or
            // double-release (IncrementRef done in-loop while receivers Release in parallel).
            int replayConsumers = 0;
            int patchConsumers = 0;
            foreach (var (playerId, _) in _subscriberRefs)
            {
                if (excludePlayerId != null && playerId == excludePlayerId) continue;
                bool needsPatch = patchAvailable && SubscriberNeedsPatch(playerId, serviceName, methodId);
                if (needsPatch) patchConsumers++;
                else if (replayAvailable) replayConsumers++;
            }

            // Settle creator-ref vs consumer-count: when N==0 the creator's refcount=1 has no
            // consumer to release it — drop it here. When N>=1, IncrementRef(N-1) so total = N.
            if (replayConsumers == 0) ReleaseUnusedVariant(replayPayload);
            else if (replayConsumers > 1 && replayPayload.Ref != 0 && _pooledPayloadRegistry != null)
                _pooledPayloadRegistry.IncrementRef(replayPayload, replayConsumers - 1);
            if (patchConsumers == 0) ReleaseUnusedVariant(patchPayload);
            else if (patchConsumers > 1 && patchPayload.Ref != 0 && _pooledPayloadRegistry != null)
                _pooledPayloadRegistry.IncrementRef(patchPayload, patchConsumers - 1);

            var sentCount = 0;
            int patchSent = 0;
            int replaySent = 0;
            foreach (var (playerId, sessionManager) in _subscriberRefs)
            {
                if (excludePlayerId != null && playerId == excludePlayerId)
                {
                    _logger.SkippingExcludedSubscriber(playerId);
                    continue;
                }

                try
                {
                    // Pick variant: subscriber's force-patch contributions (method-level OR
                    // service-level) dictate the patch variant; everyone else gets replay.
                    bool subscriberNeedsPatch = patchAvailable && SubscriberNeedsPatch(playerId, serviceName, methodId);
                    var opPayload = subscriberNeedsPatch ? patchPayload : replayPayload;
                    if (opPayload.IsEmpty) continue;

                    // Share-by-reference passthrough: same PooledPayload (Immutable<ROM> inside)
                    // handed to every subscriber needing the same variant. Orleans in-silo
                    // honors the Immutable marker → no defensive deep-copy. The receiving
                    // SessionManager owns the ref-count share and Releases when delivery is
                    // settled (ack / eviction).
                    var broadcast = new EntityBroadcast
                    {
                        ExcludePlayerId = excludePlayerId,
                        OpBytes = opPayload,
                        MethodId = methodId
                    };
                    _logger.SendingBroadcast(playerId, entityId, operationSequence, serviceName, methodName);
                    await sessionManager.ReceiveBroadcastAsync(entityId, broadcast, operationSequence);
                    sentCount++;
                    if (subscriberNeedsPatch) patchSent++;
                    else replaySent++;
                }
                catch (Exception ex)
                {
                    _logger.ErrorBroadcasting(ex, playerId);
                }
            }

            _logger.BroadcastSent(sentCount, subscriberCount, serviceName, methodName);

            SharedMeta.Server.Core.Telemetry.MetricEvents.Broadcast.Recorded(
                typeof(TState).Name, sentCount,
                replayLength: replayAvailable ? replayPayload.Length : 0, replaySent: replaySent,
                patchLength: patchAvailable ? patchPayload.Length : 0, patchSent: patchSent);
        }

        private bool SubscriberNeedsPatch(string playerId, string serviceName, ushort methodId)
        {
            if (_subscriberForcePatchServiceContributions.TryGetValue(playerId, out var svc))
            {
                for (int i = 0; i < svc.Count; i++)
                    if (svc[i] == serviceName) return true;
            }
            if (methodId != 0 && _subscriberForcePatchContributions.TryGetValue(playerId, out var meth))
            {
                for (int i = 0; i < meth.Count; i++)
                    if (meth[i] == methodId) return true;
            }
            return false;
        }
    }
}
