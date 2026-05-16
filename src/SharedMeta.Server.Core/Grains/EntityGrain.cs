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

        public MetaProviderContext(string entityId, IMetaSerializer serializer, IGrainFactory grainFactory,
            ILogger? logger = null, byte[]? namedRandomsBytes = null)
        {
            EntityId = entityId;
            Serializer = serializer;
            GrainFactory = grainFactory;
            Logger = logger;
            NamedRandomsBytes = namedRandomsBytes;
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

        private IMetaProvider<TState>? _provider;

        // 0.23.0+ Cache for `_entityId` — Orleans allocates a fresh string
        // on every call. EntityGrain hot paths (HandleCallAsync, DistributeBroadcasts, logger
        // args, telemetry tags) hit this 5-10× per RPC, and at 60K RPS that's noticeable in
        // the allocation profile. Set once in OnActivateAsync.
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

        // 0.22.0+ Aggregated force-ServerPatch refcount across all active subscribers,
        // keyed by (ServiceName, Alias, MethodVersion). HandleCallAsync consults this to
        // decide whether to activate patch tracking for the current dispatch even when the
        // method's declared mode isn't ServerPatch — so the broadcast can carry both replay
        // and patch payloads, and per-subscriber tailoring in SessionManagerGrain ships only
        // what each player needs. Refcounted to handle subscribe/unsubscribe churn in O(1).
        private readonly Dictionary<(string Service, string Alias, int Version), int> _forcePatchMethodRefs = new();

        // Per-subscriber snapshot of contributed methods so Unsubscribe can decrement the
        // refcounts deterministically without re-asking the session for capabilities.
        private readonly Dictionary<string, List<(string Service, string Alias, int Version)>> _subscriberForcePatchContributions = new();

        // 0.22.0+ Aggregated service-level force-ServerPatch refcount. Populated from
        // per-entity capabilities (config-boundary effects) that EntityGrain computes locally
        // at subscribe time + from session-level ForceServerPatchServices forwarded by
        // SessionManager. HandleCallAsync ORs this with _forcePatchMethodRefs to decide patch
        // tracking activation. Keyed by ServiceName so any method on that service triggers.
        private readonly Dictionary<string, int> _forcePatchServiceRefs = new();

        // Per-subscriber service-level contribution snapshot for symmetric Unsubscribe.
        private readonly Dictionary<string, List<string>> _subscriberForcePatchServiceContributions = new();

        public EntityGrain(
            [PersistentState("entity", "Default")] IPersistentState<EntityGrainState<TState>> persistentState,
            IMetaProviderFactory<TState> providerFactory,
            IMetaSerializer serializer,
            ILogger<EntityGrain<TState>> logger,
            IOptions<EntityGrainOptions> options,
            IEntityGrainResolver entityGrainResolver,
            IExecutionModeProvider? executionModeProvider = null,
            IConfigVersionResolver? configVersionResolver = null,
            SharedMeta.Server.Core.Session.MetaServerSignature? serverSignature = null)
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
        }

        /// <summary>
        /// 0.22.0+ Injected server signature carrying <c>ConfigBoundaries</c> + service-to-config
        /// bindings. Null when the host hasn't registered <c>MetaServerSignature</c> in DI —
        /// in that mode <see cref="SubscribeAsync"/> emits no per-entity capability overlay.
        /// </summary>
        private readonly SharedMeta.Server.Core.Session.MetaServerSignature? _serverSignature;

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _entityId = this.GetPrimaryKeyString();   // cache once for the grain lifetime
            var entityId = _entityId;
            var state = _persistentState.State;

            _logger.EntityGrainActivated(typeof(TState).Name, entityId);
            _logger.EntityStateLoaded(typeof(TState).Name, entityId, state.Subscribers.Count, state.EntitySequenceNumber);

            // 0.23.0+ Telemetry: grain lifecycle counters.
            SharedMeta.Server.Core.Telemetry.SharedMetaMeters.GrainActivation.Add(1,
                new KeyValuePair<string, object?>("state_type", typeof(TState).Name));
            SharedMeta.Server.Core.Telemetry.SharedMetaMeters.GrainsActive.Add(1,
                new KeyValuePair<string, object?>("state_type", typeof(TState).Name));

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
                providerBase.EntityCallHandler = async (targetEntityId, serviceName, methodName, argsBytes, serverTimeTicks) =>
                {
                    // 0.20.0: Gift-to-self short-circuit. When the target entity id matches
                    // this grain's id and the requested service is hosted on this grain's
                    // TState, the cross-entity call is a self-call. Routing it through an
                    // Orleans grain RPC would deadlock because EntityGrain is non-reentrant
                    // (the outer call holds the grain's task scheduler while awaiting the
                    // self-call, which can never start). Instead, dispatch the call locally
                    // on this provider as a nested operation — same MetaContext, same state,
                    // same randoms, fresh inner replay buffer.
                    if (targetEntityId == _entityId)
                    {
                        var resolved = _entityGrainResolver.GetEntityGrainByService(
                            GrainFactory, serviceName, targetEntityId);
                        // Same TState if the resolver returns a grain reference whose primary
                        // key matches us AND its grain interface is IEntityGrain<TState>.
                        // Cheap structural check: the resolver returns IEntityGrain<TState>
                        // for any service hosted on this state — if so, we are it.
                        if (resolved is IEntityGrain<TState>)
                        {
                            return await providerBase.HandleNestedCallAsync(
                                targetEntityId, serviceName, methodName, argsBytes);
                        }
                    }

                    var targetGrain = _entityGrainResolver.GetEntityGrainByService(
                        GrainFactory, serviceName, targetEntityId);
                    if (targetGrain == null)
                        throw new InvalidOperationException(
                            $"Cannot resolve entity grain for service {serviceName}, entity {targetEntityId}");

                    // Propagate the originating client's app version so the target entity's
                    // ComputeSchemaCapForClient sees the same version as the session-level call.
                    // 0.21.0 strict: when there is no current MetaContext (server-internal
                    // cross-entity call from a timer / background job / server-only service),
                    // fall back to IConfigVersionResolver.CurrentClientVersion so the target
                    // resolves under a defined version instead of throwing downstream.
                    var callerClientVersion = SharedMeta.Core.MetaContextAccessor.Current?.CallerClientVersion
                        ?? _configVersionResolver?.CurrentClientVersion;
                    var result = await targetGrain.HandleCallFromEntityAsync(new RpcCall
                    {
                        ServiceName = serviceName,
                        MethodName = methodName,
                        Payload = argsBytes,
                        ServerTimeTicks = serverTimeTicks,
                        CallerClientVersion = callerClientVersion
                    });

                    if (result.HasError)
                        throw new InvalidOperationException(
                            $"Cross-entity call failed: {result.ErrorMessage}");

                    return new CrossEntityCallInfo
                    {
                        EntityId = targetEntityId,
                        EntitySequenceNumber = result.EntitySequenceNumber,
                        ResultBytes = result.MainOperation?.Response?.ResultBytes,
                        ServiceName = serviceName,
                        MethodName = methodName
                    };
                };

                // 0.22.0+: Fire-and-forget cross-entity dispatch for [MetaMethod(OneWay = true)]
                // methods. Mirrors EntityCallHandler's lookup logic but routes through the
                // [OneWay]-marked grain entry point so the source grain doesn't wait. No result
                // recording, no return — the handler is void.
                providerBase.EntityCallOneWayHandler = (targetEntityId, serviceName, methodName, argsBytes, serverTimeTicks) =>
                {
                    var targetGrain = _entityGrainResolver.GetEntityGrainByService(
                        GrainFactory, serviceName, targetEntityId);
                    if (targetGrain == null)
                    {
                        _logger.LogWarning(
                            "[EntityGrain] OneWay {Service}.{Method} dropped: cannot resolve grain for entity {EntityId}",
                            serviceName, methodName, targetEntityId);
                        return;
                    }

                    var callerClientVersion = SharedMeta.Core.MetaContextAccessor.Current?.CallerClientVersion
                        ?? _configVersionResolver?.CurrentClientVersion;
                    var rpcCall = new RpcCall
                    {
                        ServiceName = serviceName,
                        MethodName = methodName,
                        Payload = argsBytes,
                        ServerTimeTicks = serverTimeTicks,
                        CallerClientVersion = callerClientVersion
                    };

                    // [OneWay] on IEntityGrainBase.HandleCallFromEntityOneWayAsync makes this a
                    // genuine fire-and-forget on the Orleans wire — the discard `_ =` makes the
                    // C# compiler happy about the unobserved Task and ensures no SynchronizationContext
                    // hop happens here.
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

                // Forward optional global seed factory so MetaProviderBase.CreateFreshRandomSeed
                // can mix in non-deterministic entropy when host opts in. Must be set BEFORE
                // Initialize() — Initialize seeds fresh randoms during its body.
                providerBase.FreshRandomSeedFactory = _options.FreshRandomSeedFactory;
            }

            var context = new MetaProviderContext(entityId, _serializer, GrainFactory, _logger, state.NamedRandomsBytes);
            _provider.Initialize(context, state.UserState, state.ServerRandomBytes, state.OptimisticRandomBytes);

            // Apply global deep desync override from EntityGrainOptions
            if (_options.DeepDesyncEnabled.HasValue && _provider is MetaProviderBase<TState> ddProvider)
                ddProvider.DeepDesyncEnabled = _options.DeepDesyncEnabled.Value;

            // 0.20.0 fix: seed the provider's schema version from persisted state.Version.
            // Lazy migration is still deferred until subscribe/HandleCall (we don't yet know
            // the client's version), but the provider must know which schema the state is
            // already at — otherwise the fresh-entity-floor rule re-runs [MetaInit] on every
            // activation, even when the state was initialized in a previous session.
            if (_provider is MetaProviderBase<TState> seedProvider)
                seedProvider.SeedSchemaVersion(state.Version);

            // Activation is intentionally NOT running [MetaInit] migration here. We don't yet
            // know which client triggered activation, so we cannot decide which config branch
            // to migrate to. Eagerly migrating to the provider's CurrentVersion would lock
            // older clients out of fresh entities (their resolved config branch can't satisfy
            // the new schema's IsClientConfigCompatible gate).
            //
            // Init/migration is deferred to:
            //   • SubscribeAsync — runs migration capped to the subscribing client's branch.
            //   • HandleCallAsync — lazy migration capped to call.CallerClientVersion.
            // First-time base init for a fresh entity (state.Version == 0) is handled by the
            // same path: ComputeRequiredStateSchema + the generated "fresh entity floor" rule
            // ensure schema 1 always runs once a client interacts with the entity.

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

            // 0.23.0+ Telemetry: grain lifecycle counters.
            SharedMeta.Server.Core.Telemetry.SharedMetaMeters.GrainDeactivation.Add(1,
                new KeyValuePair<string, object?>("state_type", typeof(TState).Name),
                new KeyValuePair<string, object?>("reason", reason.ReasonCode.ToString()));
            SharedMeta.Server.Core.Telemetry.SharedMetaMeters.GrainsActive.Add(-1,
                new KeyValuePair<string, object?>("state_type", typeof(TState).Name));

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        public async Task<EntitySnapshot> SubscribeAsync(string playerId, ISessionManagerReference sessionManager, string? clientVersion = null, IReadOnlyList<MethodIdentity>? forceServerPatchMethods = null)
        {
            using var __subActivity = SharedMeta.Server.Core.Telemetry.SharedMetaActivities.Source.StartActivity(
                SharedMeta.Server.Core.Telemetry.SharedMetaActivities.SpanEntitySubscribe);
            if (__subActivity != null)
            {
                __subActivity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagStateType, typeof(TState).Name);
                __subActivity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagEntityId, _entityId);
                __subActivity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagPlayerId, playerId);
            }
            var __subStart = System.Diagnostics.Stopwatch.GetTimestamp();
            string __subResult = "success";
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

            // Drive client-aware init/migration BEFORE the compatibility gate. For fresh
            // entities (state.Version == 0) this runs the base [MetaInit] capped to the
            // subscriber's resolved config branch, so a 1.x client never causes a 2.0 jump.
            // For entities already at a higher schema than the client supports, this is a
            // no-op and the gate below rejects the subscribe.
            //
            // 0.21.0 Global: migration is driven by IConfigVersionResolver.CurrentClientVersion
            // (server-set) — NOT the joiner's own version. Schema progression on Global entities
            // tracks the server's CurrentClientVersion regardless of who's joining. Private/Shared
            // keep the joiner-driven model (pin establishes / validates per first subscriber).
            if (_provider is MetaProviderBase<TState> mpbInit)
            {
                var migrationDriverVersion = mpbInit.Scope == EntityScope.Global
                    ? (_configVersionResolver?.CurrentClientVersion ?? clientVersion)
                    : clientVersion;
                var clientCfg = mpbInit.ResolveClientConfigVersion(migrationDriverVersion);
                // 0.21.0: async variant avoids sync-over-async when the provider needs to
                // fetch bytes across a grain boundary (e.g. BroadcastingConfigProvider on
                // cold-cache version). Default impl forwards to sync InitializeConfig for
                // providers that don't need async materialization.
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

            // Per-entity config compatibility gate: reject clients whose resolved config version
            // is below the minimum required for the entity's current state schema — but only
            // when the schema bump was marked Breaking = true on [MetaStateVersion]. 0.22.0:
            // generator emits IsClientConfigCompatible that returns false ONLY for breaking
            // bumps; non-breaking schema advances allow old clients to subscribe and rely on
            // VersionTolerant deserialization to skip new fields.
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

            // 0.21.0 Phases 5+6: establish runtime config-version pin (first subscriber) or
            // validate against existing pin (subsequent Shared subscribers).
            //
            //   • EntityScope.Private — pin set once on owner connect; survives subscriber
            //     churn until grain deactivation. Owner is the sole subscriber so the
            //     ActiveConfigPins.Count check resolves "first-time" trivially.
            //
            //   • EntityScope.Shared — first subscriber establishes pin; every subsequent
            //     joiner is validated against it. Patch differences tolerated (joiner gets
            //     pinned patch via GetCachedConfigForClient); Major.Minor mismatch rejects.
            //
            //   • EntityScope.Global — pin is NEVER set. Every call resolves freshly from
            //     IConfigVersionResolver.CurrentClientVersion.
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

            // 0.22.0 Aggregate this player's force-patch declarations. Only methods on services
            // hosted on THIS entity matter; other-service entries from the player's global
            // capabilities are ignored. The provider knows which services it hosts via
            // _provider.AccessPolicy / generated infrastructure; for now we accept everything
            // the caller passed since they pre-filtered to this entity's bound services.
            if (forceServerPatchMethods is { Count: > 0 })
            {
                // Discard any prior contribution for this player (defensive — resubscribe after
                // reconnect, capabilities may have changed). Refcount stays consistent.
                if (_subscriberForcePatchContributions.TryGetValue(playerId, out var prior))
                {
                    foreach (var key in prior)
                    {
                        if (_forcePatchMethodRefs.TryGetValue(key, out var c) && c > 0)
                            _forcePatchMethodRefs[key] = c - 1;
                        if (_forcePatchMethodRefs.TryGetValue(key, out var c2) && c2 == 0)
                            _forcePatchMethodRefs.Remove(key);
                    }
                }
                var contributions = new List<(string, string, int)>(forceServerPatchMethods.Count);
                foreach (var m in forceServerPatchMethods)
                {
                    var key = (m.ServiceName, m.Alias, m.Version);
                    contributions.Add(key);
                    _forcePatchMethodRefs[key] = _forcePatchMethodRefs.TryGetValue(key, out var c) ? c + 1 : 1;
                }
                _subscriberForcePatchContributions[playerId] = contributions;
            }

            // 0.22.0 Compute per-entity capability overlay from [MetaConfigStructureBoundary]
            // declarations. Resolved config version for this player + this entity's bound
            // config(s) → which services need force-patch. Stored in BOTH the local refcount
            // (so HandleCallAsync activates patch tracking) AND the returned snapshot (so
            // SessionManagerGrain can cache for broadcast fan-out tailoring + forward to client).
            var augmentedCaps = ComputePerEntityCapabilities(clientVersion);
            if (augmentedCaps is { ForceServerPatchServices.Count: > 0 })
            {
                // Drop prior contribution to keep refcount in sync across resubscribe.
                if (_subscriberForcePatchServiceContributions.TryGetValue(playerId, out var priorSvc))
                {
                    foreach (var svc in priorSvc)
                    {
                        if (_forcePatchServiceRefs.TryGetValue(svc, out var sc))
                        {
                            if (sc <= 1) _forcePatchServiceRefs.Remove(svc);
                            else _forcePatchServiceRefs[svc] = sc - 1;
                        }
                    }
                }
                var svcContributions = new List<string>(augmentedCaps.ForceServerPatchServices.Count);
                foreach (var svc in augmentedCaps.ForceServerPatchServices)
                {
                    svcContributions.Add(svc);
                    _forcePatchServiceRefs[svc] = _forcePatchServiceRefs.TryGetValue(svc, out var sc) ? sc + 1 : 1;
                }
                _subscriberForcePatchServiceContributions[playerId] = svcContributions;
            }

            _logger.PlayerSubscribed(_entityId, playerId);

            await _persistentState.WriteStateAsync();
            ResetPersistenceTracking();

            var stateBytes = _provider?.GetStateBytes() ?? _serializer.Pack(state.UserState);

            var namedBytes = _provider?.GetNamedRandomsBytes();
            return new EntitySnapshot
            {
                StateBytes = stateBytes,
                CurrentSequenceNumber = state.EntitySequenceNumber,
                OptimisticRandomBytes = _provider?.GetOptimisticRandomBytes(),
                NamedRandomsBytes = namedBytes is { Length: > 0 } ? namedBytes : null,
                // 0.21.0 Phase 5+7: scope-aware effective version. For Private/Shared this is
                // the pinned version (so the client materializes the same config the server
                // will dispatch under). For Global it's the IConfigVersionResolver.CurrentClientVersion
                // resolution, NOT the joiner's own resolved version — Global entities run under
                // server-driven config regardless of who's calling.
                ConfigVersion = (_provider as MetaProviderBase<TState>)?.ResolveEffectiveConfigVersion(clientVersion)
                               ?? _provider?.ResolveClientConfigVersion(clientVersion)
                               ?? default,
                AugmentedCapabilities = augmentedCaps,
            };
            }
            catch
            {
                __subResult = "error";
                throw;
            }
            finally
            {
                var __subElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(__subStart).TotalMilliseconds;
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SubscribeDuration.Record(__subElapsed,
                    new KeyValuePair<string, object?>("state_type", typeof(TState).Name),
                    new KeyValuePair<string, object?>("result", __subResult));
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SubscribeCount.Add(1,
                    new KeyValuePair<string, object?>("state_type", typeof(TState).Name));
                if (__subResult == "success")
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SubscribersActive.Add(1,
                        new KeyValuePair<string, object?>("state_type", typeof(TState).Name));
                __subActivity?.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagResult, __subResult);
            }
        }

        /// <summary>
        /// 0.22.0+ Compute per-entity capability overlay for a specific subscriber. Delegates
        /// the pure boundary check to <see cref="SharedMeta.Server.Core.Session.ConfigBoundaryEvaluator.ComputeAffectedServices"/>
        /// (unit-tested independently). Returns <c>null</c> when no boundaries apply.
        /// </summary>
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
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SubscribersActive.Add(-1,
                    new KeyValuePair<string, object?>("state_type", typeof(TState).Name));
            }
            _persistentState.State.Subscribers.Remove(playerId);
            _subscriberRefs.Remove(playerId);

            // 0.22.0 Decrement force-patch refcounts for what this player contributed at
            // subscribe time. Removing zero-count entries keeps the dispatch-time lookup tight.
            if (_subscriberForcePatchContributions.Remove(playerId, out var contributions))
            {
                foreach (var key in contributions)
                {
                    if (_forcePatchMethodRefs.TryGetValue(key, out var c))
                    {
                        if (c <= 1) _forcePatchMethodRefs.Remove(key);
                        else _forcePatchMethodRefs[key] = c - 1;
                    }
                }
            }
            // Service-level (per-entity boundary) symmetric decrement.
            if (_subscriberForcePatchServiceContributions.Remove(playerId, out var svcContributions))
            {
                foreach (var svc in svcContributions)
                {
                    if (_forcePatchServiceRefs.TryGetValue(svc, out var sc))
                    {
                        if (sc <= 1) _forcePatchServiceRefs.Remove(svc);
                        else _forcePatchServiceRefs[svc] = sc - 1;
                    }
                }
            }

            _logger.PlayerUnsubscribed(_entityId, playerId);

            // 0.21.0: pin lives only while there are active subscribers. When the last
            // subscriber leaves, drop pins so the next first-subscriber re-establishes
            // fresh — picks up any patch published while the entity was effectively idle.
            // For Private (single owner): disconnect + reconnect → fresh pin, hot-patches apply.
            // For Shared: all-leave → next "first joiner" of a fresh session re-pins.
            // Global never holds pins, no-op there.
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

        public async Task<EntityCallResult> HandleCallAsync(RpcCall call)
        {
            if (_provider == null)
            {
                return new EntityCallResult { Error = "Provider not initialized" };
            }

            using var activity = SharedMeta.Server.Core.Telemetry.SharedMetaActivities.Source.StartActivity(
                SharedMeta.Server.Core.Telemetry.SharedMetaActivities.SpanEntityRpc);
            if (activity != null)
            {
                activity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagService, call.ServiceName);
                activity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagMethod, call.MethodName);
                activity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagEntityId, _entityId);
                if (call.CallerId != null)
                    activity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagPlayerId, call.CallerId);
            }
            var __metricStart = System.Diagnostics.Stopwatch.GetTimestamp();
            string __metricResult = "success";

            var state = _persistentState.State;
            var operationSequence = ++state.EntitySequenceNumber;
            var forcePersist = false;

            // Update caller's last active time
            if (call.CallerId != null && state.Subscribers.TryGetValue(call.CallerId, out var callerSub))
                callerSub.LastActiveUtc = DateTime.UtcNow;

            try
            {
                // 0.22.0 Decide if this dispatch needs patch tracking to satisfy any legacy
                // subscriber's force-patch capability. O(1) lookup — refcount Dictionary
                // populated at subscribe time. When true, the provider activates the
                // PatchWrapper alongside normal replay recording, so the resulting broadcast
                // carries both PatchBytes and ReplayPayload. SessionManagerGrain then strips
                // per-subscriber on fan-out.
                // ORs two refcounts:
                //   * _forcePatchMethodRefs   — session-level MinCompatibleVersion mismatches.
                //   * _forcePatchServiceRefs  — per-entity ConfigStructureBoundary triggers.
                bool requirePatchForFanOut =
                       _forcePatchMethodRefs.ContainsKey((call.ServiceName, call.MethodName, call.MethodVersion))
                    || _forcePatchServiceRefs.ContainsKey(call.ServiceName);
                if (requirePatchForFanOut)
                {
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.ForcePatchApplied.Add(1,
                        new KeyValuePair<string, object?>("service", call.ServiceName),
                        new KeyValuePair<string, object?>("method", call.MethodName),
                        new KeyValuePair<string, object?>("kind", _forcePatchServiceRefs.ContainsKey(call.ServiceName) ? "service" : "method"));
                }

                // isClientOriginated: true → provider rejects [MetaMethod(GenerateClientApi=false)]
                // methods. Cross-entity peers land at HandleCallFromEntityAsync below with false.
                var providerResult = await _provider.HandleCallAsync(call, isClientOriginated: true, requirePatchForFanOut: requirePatchForFanOut);
                forcePersist = providerResult.ForcePersist;

                // Lazy migration: if CheckAndRunLazyMigrationAsync ran a migration, persist the
                // updated state.Version so the schema advance is durable before the next call.
                if (_provider is MetaProviderBase<TState> mpb && mpb.LazyMigrationCompleted)
                {
                    state.Version = mpb.LazyMigrationNewVersion;
                    mpb.LazyMigrationCompleted = false;
                    forcePersist = true;
                }

                PersistRandomBytes();

                // Distribute broadcasts to ALL EXCEPT caller
                await DistributeBroadcasts(providerResult.Broadcasts, operationSequence, excludePlayerId: call.CallerId);

                // Build trigger OperationResults from nested broadcasts
                var mainBroadcast = providerResult.Broadcasts.FirstOrDefault();
                var triggerOps = mainBroadcast?.TriggerBroadcasts?.Select(t => new OperationResult
                {
                    Call = new RpcCall { ServiceName = t.ServiceName, MethodName = t.MethodName, Payload = t.Payload },
                    Response = new RpcResponse { ReplayPayload = t.ReplayPayload, RandomScrollDelta = t.RandomScrollDelta, PatchBytes = t.PatchBytes }
                }).ToList();

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    MainOperation = new OperationResult
                    {
                        Call = call,
                        Response = providerResult.Response
                    },
                    TriggerOperations = triggerOps,
                    CrossEntityCalls = providerResult.CrossEntityCalls,
                    Error = providerResult.Response.Error
                };
            }
            catch (Exception ex)
            {
                __metricResult = "error";
                _logger.ErrorHandlingCall(ex);
                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message
                };
            }
            finally
            {
                await PersistIfNeeded(forcePersist);
                var __elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(__metricStart).TotalMilliseconds;
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.RpcDuration.Record(__elapsed,
                    new KeyValuePair<string, object?>("service", call.ServiceName),
                    new KeyValuePair<string, object?>("method", call.MethodName),
                    new KeyValuePair<string, object?>("result", __metricResult));
                if (call.Payload != null && call.Payload.Length > 0)
                {
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.RpcRequestBytes.Record(call.Payload.Length,
                        new KeyValuePair<string, object?>("service", call.ServiceName),
                        new KeyValuePair<string, object?>("method", call.MethodName));
                }
                activity?.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagResult, __metricResult);
            }
        }

        // SECURITY INVARIANT: this entry point must NEVER be reached by direct client traffic.
        // It is intentionally not gated by IsClientCallable because cross-entity calls are
        // server-internal — the caller is another grain that has already authorized the
        // originating client through its own public API. Transports (SignalR MetaHub, HTTP
        // polling, etc.) must route client packets only to HandleCallAsync / HandleQueryAsync /
        // HandleSignalAsync. Adding any client-reachable wiring to HandleCallFromEntityAsync
        // would silently bypass the [MetaMethod(GenerateClientApi = false)] protection.
        public async Task<EntityCallResult> HandleCallFromEntityAsync(RpcCall call)
        {
            if (_provider == null)
            {
                return new EntityCallResult { Error = "Provider not initialized" };
            }

            using var __xeActivity = SharedMeta.Server.Core.Telemetry.SharedMetaActivities.Source.StartActivity(
                SharedMeta.Server.Core.Telemetry.SharedMetaActivities.SpanCrossEntityCall);
            if (__xeActivity != null)
            {
                __xeActivity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagService, call.ServiceName);
                __xeActivity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagMethod, call.MethodName);
                __xeActivity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagKind, "normal");
                __xeActivity.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagEntityId, _entityId);
            }
            var __xeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            string __xeResult = "success";

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

                PersistRandomBytes();

                // Distribute broadcasts to ALL subscribers (no exclusion for cross-entity calls)
                await DistributeBroadcasts(providerResult.Broadcasts, operationSequence, excludePlayerId: null);

                var mainBroadcast = providerResult.Broadcasts.FirstOrDefault();
                var triggerOps = mainBroadcast?.TriggerBroadcasts?.Select(t => new OperationResult
                {
                    Call = new RpcCall { ServiceName = t.ServiceName, MethodName = t.MethodName, Payload = t.Payload },
                    Response = new RpcResponse { ReplayPayload = t.ReplayPayload, RandomScrollDelta = t.RandomScrollDelta, PatchBytes = t.PatchBytes }
                }).ToList();

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    MainOperation = new OperationResult
                    {
                        Call = call,
                        Response = providerResult.Response
                    },
                    TriggerOperations = triggerOps,
                    CrossEntityCalls = providerResult.CrossEntityCalls,
                    Error = providerResult.Response.Error
                };
            }
            catch (Exception ex)
            {
                __xeResult = "error";
                _logger.ErrorHandlingCrossEntityCall(ex);
                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message
                };
            }
            finally
            {
                await PersistIfNeeded(forcePersist);
                var __xeElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(__xeStart).TotalMilliseconds;
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.CrossEntityCallDuration.Record(__xeElapsed,
                    new KeyValuePair<string, object?>("to_service", call.ServiceName),
                    new KeyValuePair<string, object?>("kind", "normal"),
                    new KeyValuePair<string, object?>("result", __xeResult));
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.CrossEntityCallCount.Add(1,
                    new KeyValuePair<string, object?>("to_service", call.ServiceName),
                    new KeyValuePair<string, object?>("kind", "normal"));
                __xeActivity?.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagResult, __xeResult);
            }
        }

        // 0.22.0+: Fire-and-forget cross-entity entry. Same body as HandleCallFromEntityAsync
        // but no result is sent back — caller side dispatched via Orleans [OneWay], so even
        // unhandled exceptions die here in the catch and are only logged. Broadcasts still
        // reach THIS entity's own subscribers normally (clan-power change is visible to clan
        // subscribers even though the source grain isn't waiting).
        public async Task HandleCallFromEntityOneWayAsync(RpcCall call)
        {
            // OneWay (Notification mode) telemetry: separate count from the awaited cross-entity
            // path so dashboards can see how much of the cross-entity traffic is fire-and-forget.
            // The inner HandleCallFromEntityAsync also records its own normal-kind cross-entity
            // metric for the same call — that's intentional (we want both "outer dispatch" and
            // "inner execution" perspectives). For Activity tracing the inner span nests under
            // this one via Activity.Current.
            SharedMeta.Server.Core.Telemetry.SharedMetaMeters.CrossEntityCallCount.Add(1,
                new KeyValuePair<string, object?>("to_service", call.ServiceName),
                new KeyValuePair<string, object?>("kind", "notification"));
            try
            {
                // Reuse the standard cross-entity path. The result is computed (state mutates,
                // broadcasts produced) and we just discard the returned EntityCallResult.
                await HandleCallFromEntityAsync(call);
            }
            catch (Exception ex)
            {
                // Source grain isn't waiting; surface only in the log.
                _logger.ErrorHandlingCrossEntityCall(ex);
            }
        }

        public async Task<QueryCallResponse> HandleQueryAsync(RpcCall call)
        {
            if (_provider == null)
                return new QueryCallResponse { Error = "Provider not initialized" };

            // Method-level (IsQueryMethod / IsClientCallable / IsOpenAccessQuery) and
            // entity-level (AccessPolicy / CheckAccessAsync) validation lives inside
            // MetaProviderBase.HandleQueryAsync. EntityGrain just routes.
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
            await _provider.HandleSignalAsync(call);
        }

        public Task<byte[]?> GetEntityStateAsync()
        {
            var userState = _persistentState.State.UserState;
            return Task.FromResult<byte[]?>(_serializer.Pack(userState));
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

        public async Task<EntityCallResult> HandleExternalEventAsync(
            string subscriberInterface,
            string methodName,
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
                var providerResult = await _provider.HandleExternalEventAsync(subscriberInterface, methodName, eventData, callerId);

                // Distribute broadcasts to ALL subscribers (no exclusion for external events)
                await DistributeBroadcasts(providerResult.Broadcasts, operationSequence, excludePlayerId: null);

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    MainOperation = new OperationResult
                    {
                        Call = new RpcCall
                        {
                            ServiceName = subscriberInterface,
                            MethodName = methodName,
                            Payload = eventData,
                            CallerId = callerId
                        },
                        Response = new RpcResponse()
                    }
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
        /// </summary>
        private async Task PersistIfNeeded(bool forcePersist)
        {
            _requestsSinceLastSave++;
            _isDirty = true;

            if (ShouldPersist(forcePersist))
            {
                // 0.23.0+ Telemetry: per-write duration. Payload-size bucket is left to the
                // underlying storage provider to surface (the grain doesn't see the serialized
                // bytes — that's deep inside IPersistentState's pipeline).
                var __pStart = System.Diagnostics.Stopwatch.GetTimestamp();
                using var __pActivity = SharedMeta.Server.Core.Telemetry.SharedMetaActivities.Source.StartActivity(
                    SharedMeta.Server.Core.Telemetry.SharedMetaActivities.SpanPersistenceWrite);
                __pActivity?.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagStateType, typeof(TState).Name);
                try
                {
                    await _persistentState.WriteStateAsync();
                }
                finally
                {
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.PersistenceWriteDuration.Record(
                        System.Diagnostics.Stopwatch.GetElapsedTime(__pStart).TotalMilliseconds,
                        new KeyValuePair<string, object?>("state_type", typeof(TState).Name));
                }
                var entityId = _entityId;
                if (forcePersist || _options.PersistencePolicy.Mode != PersistenceMode.EveryCall)
                    _logger.PersistenceForced(entityId, _requestsSinceLastSave);
                ResetPersistenceTracking();
            }
            else if (_options.PersistencePolicy.Mode != PersistenceMode.EveryCall)
            {
                _logger.PersistenceDeferred(_entityId,
                    _requestsSinceLastSave, _options.PersistencePolicy.Mode.ToString());
            }
        }

        private void ResetPersistenceTracking()
        {
            _requestsSinceLastSave = 0;
            _lastSaveTime = DateTime.UtcNow;
            _isDirty = false;
        }

        private void PersistRandomBytes()
        {
            if (_provider == null) return;
            var state = _persistentState.State;
            state.ServerRandomBytes = _provider.GetServerRandomBytes();
            state.OptimisticRandomBytes = _provider.GetOptimisticRandomBytes();
            var namedBytes = _provider.GetNamedRandomsBytes();
            state.NamedRandomsBytes = namedBytes.Length > 0 ? namedBytes : null;
        }

        private async Task DistributeBroadcasts(List<EntityBroadcast> broadcasts, long operationSequence, string? excludePlayerId)
        {
            var entityId = _entityId;
            var subscriberCount = _subscriberRefs.Count;

            _logger.DistributingBroadcasts(entityId, operationSequence, broadcasts.Count, subscriberCount, excludePlayerId ?? "none");

            foreach (var broadcast in broadcasts)
            {
                var sentCount = 0;
                int patchCount = 0;
                int replayCount = 0;
                foreach (var (playerId, sessionManager) in _subscriberRefs)
                {
                    if (excludePlayerId != null && playerId == excludePlayerId)
                    {
                        _logger.SkippingExcludedSubscriber(playerId);
                        continue;
                    }

                    try
                    {
                        // 0.22.0 Per-subscriber tailoring at fan-out. Decide once whether THIS
                        // subscriber needs the patch variant (legacy: their session-level or
                        // per-entity caps mark this method/service as force-patch) or the replay
                        // variant (modern: native execution). Then send the appropriately-stripped
                        // broadcast — no extra bytes on the wire and no work for SessionManagerGrain.
                        var tailored = TailorBroadcastForSubscriber(broadcast, playerId);
                        _logger.SendingBroadcast(playerId, entityId, operationSequence, tailored.ServiceName, tailored.MethodName);
                        await sessionManager.ReceiveBroadcastAsync(entityId, tailored, operationSequence);
                        sentCount++;
                        // Telemetry: which path did this subscriber receive? Tailor produces a
                        // broadcast with only ReplayPayload (modern) OR only PatchBytes (legacy).
                        if (tailored.PatchBytes is { Length: > 0 }) patchCount++;
                        else replayCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorBroadcasting(ex, playerId);
                    }
                }

                _logger.BroadcastSent(sentCount, subscriberCount, broadcast.ServiceName, broadcast.MethodName);

                // 0.23.0+ Telemetry: fan-out size + per-payload-kind size distribution.
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.BroadcastFanOutSize.Record(sentCount,
                    new KeyValuePair<string, object?>("state_type", typeof(TState).Name));
                if (broadcast.ReplayPayload is { Length: > 0 } replay)
                {
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.BroadcastPayloadBytes.Record(replay.Length,
                        new KeyValuePair<string, object?>("state_type", typeof(TState).Name),
                        new KeyValuePair<string, object?>("kind", "replay"));
                }
                if (broadcast.PatchBytes is { Length: > 0 } patch)
                {
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.BroadcastPayloadBytes.Record(patch.Length,
                        new KeyValuePair<string, object?>("state_type", typeof(TState).Name),
                        new KeyValuePair<string, object?>("kind", "patch"));
                }
                if (broadcast.StateBytes is { Length: > 0 } stateBytes)
                {
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.BroadcastPayloadBytes.Record(stateBytes.Length,
                        new KeyValuePair<string, object?>("state_type", typeof(TState).Name),
                        new KeyValuePair<string, object?>("kind", "state"));
                }
                if (patchCount > 0)
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.BroadcastTailored.Add(patchCount,
                        new KeyValuePair<string, object?>("state_type", typeof(TState).Name),
                        new KeyValuePair<string, object?>("path", "patch"));
                if (replayCount > 0)
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.BroadcastTailored.Add(replayCount,
                        new KeyValuePair<string, object?>("state_type", typeof(TState).Name),
                        new KeyValuePair<string, object?>("path", "replay"));
            }
        }

        /// <summary>
        /// 0.22.0+ Per-subscriber tailoring at fan-out. Delegates to the pure static helper
        /// <see cref="BroadcastTailor.TailorForSubscriber"/> for the actual strip logic.
        /// </summary>
        private EntityBroadcast TailorBroadcastForSubscriber(EntityBroadcast original, string playerId)
        {
            _subscriberForcePatchContributions.TryGetValue(playerId, out var methodContribs);
            _subscriberForcePatchServiceContributions.TryGetValue(playerId, out var serviceContribs);
            return BroadcastTailor.TailorForSubscriber(original, methodContribs, serviceContribs);
        }
    }
}
