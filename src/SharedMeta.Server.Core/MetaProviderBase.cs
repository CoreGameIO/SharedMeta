using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Network;
using SharedMeta.Core.Patch;
using SharedMeta.Core.Random;
using SharedMeta.Core.Transport;
using SharedMeta.Server;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core;

/// <summary>
/// Base class for generated MetaProvider implementations.
/// Provides common functionality for handling RPC calls and external events.
/// </summary>
public abstract class MetaProviderBase<TState> : IMetaProvider<TState> where TState : class, ISharedState, new()
{
    protected IMetaProviderContext Context { get; private set; } = null!;
    protected TState State { get; private set; } = null!;
    protected ServerMetaContext<TState>? MetaContext { get; private set; }
    protected ILogger Logger { get; private set; } = NullLogger.Instance;

    private MetaRandom _serverRandom = null!;
    private MetaRandom _optimisticRandom = null!;
    private MetaRandom[] _namedRandoms = System.Array.Empty<MetaRandom>();
    private IMetaRandom[]? _namedRandomsView;

    /// <summary>
    /// Service resolver for dependency injection (e.g., IRandomService).
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<Type, object>? ServiceResolver { get; set; }

    /// <summary>
    /// Handler for cross-entity calls (when a service calls another entity).
    /// Returns CrossEntityCallInfo with EntitySequenceNumber and ResultBytes.
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<string, string, string, byte[], long, Task<CrossEntityCallInfo>>? EntityCallHandler { get; set; }

    /// <summary>
    /// Handler for read-only cross-entity state access.
    /// Returns serialized state bytes of the target entity, or null if not found.
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<string, string, Task<byte[]?>>? EntityStateHandler { get; set; }

    /// <summary>
    /// Handler for mid-method state persistence.
    /// Set by EntityGrain to enable Context.SaveStateAsync() from service methods.
    /// </summary>
    public Func<Task>? SaveStateHandler { get; set; }

    /// <summary>
    /// Server-side execution mode provider. Determines per-method execution mode.
    /// Set via DI to enable runtime mode switching (e.g., switching to ServerPatch).
    /// </summary>
    public IExecutionModeProvider? ExecutionModeProvider { get; set; }

    /// <summary>
    /// When true, computes FNV-1a hash of serialized state after each method execution.
    /// Client compares its local hash with the server's to detect state-level desyncs.
    /// </summary>
    public bool DeepDesyncEnabled { get; set; }

    public virtual void Initialize(IMetaProviderContext context, TState state,
        byte[]? serverRandomBytes = null, byte[]? optimisticRandomBytes = null)
    {
        Context = context;
        State = state;
        Logger = context.Logger ?? NullLogger.Instance;
        MetaContext = new ServerMetaContext<TState>(state, context.Serializer);
        MetaContext.EntityId = context.EntityId;
        MetaContext.Log = context.Logger != null
            ? new MetaLoggerAdapter(context.Logger, context.EntityId)
            : NullMetaLogger.Instance;
        MetaContext.ServiceResolver = ServiceResolver;
        MetaContext.EntityCallHandler = EntityCallHandler;
        MetaContext.EntityStateHandler = EntityStateHandler;
        MetaContext.SaveStateHandler = SaveStateHandler;
        // 0.20.0: Wire sibling-service resolver so generated cross-entity accessors can
        // detect self-targeted calls and dispatch directly on the cached sibling impl
        // instance — no serialization, no grain RPC.
        MetaContext.SiblingServiceResolver = ResolveSiblingByType;

        // Initialize deterministic randoms. Seed string only matters for FRESH entities
        // (no persisted bytes). Once persisted, MetaRandom restores its full internal state
        // and the seed is never consulted again — and clients receive the persisted bytes
        // via SubscribeResponse, so the seed is never sent over the wire either.
        _serverRandom = DeserializeOrCreateRandom(context.Serializer, serverRandomBytes, CreateFreshRandomSeed("server"));
        _optimisticRandom = DeserializeOrCreateRandom(context.Serializer, optimisticRandomBytes, CreateFreshRandomSeed("optimistic"));

        // Initialize named randoms from descriptors (+ persisted bytes, if any)
        InitializeNamedRandoms(context);

        // Hook for subclass initialization
        OnInitialize();
    }

    /// <summary>
    /// Returns the seed string for a fresh random stream — invoked only when no persisted
    /// bytes exist for that stream (first activation of this entity, or a stream slot whose
    /// <see cref="NamedRandomDescriptor"/> name shifted positionally).
    /// <para>
    /// The seed is consumed locally by <c>MetaRandom.FromString</c> on the server and never
    /// transmitted to the client — once the random advances and the entity persists, the
    /// internal state (s0/s1/s2/s3 + ScrollId) is what flows over the wire on subscribe.
    /// </para>
    /// <para>
    /// Default: deterministic <c>"{entityId}:{streamName}"</c> — same entityId always
    /// produces the same stream, useful for reproducible tests. Override to mix in a
    /// non-deterministic component (e.g. <c>DateTime.UtcNow.Ticks</c>, <c>Random.Shared</c>)
    /// when you want fresh entities recreated under the same id (profile reset, generated
    /// expedition recycled) to produce different streams.
    /// </para>
    /// <para>
    /// <b>Replay safety:</b> overriding this with non-deterministic entropy is safe — the
    /// resulting random state is captured in the entity snapshot the client receives on
    /// subscribe, so client-side replay/optimistic execution sees the same advanced state
    /// the server has, without ever needing to reconstruct the seed.
    /// </para>
    /// <para>
    /// <c>[NamedRandom(Seed = "literal")]</c> bypasses this method — that attribute is the
    /// explicit "pin to a fixed seed" override.
    /// </para>
    /// </summary>
    /// <param name="streamName">
    /// <c>"server"</c> for <see cref="ServerMetaContext{TState}.ServerRandom"/>,
    /// <c>"optimistic"</c> for <see cref="MetaContext.Random"/>,
    /// or the <see cref="NamedRandomAttribute.Name"/> of a <c>[NamedRandom]</c> stream.
    /// </param>
    protected virtual string CreateFreshRandomSeed(string streamName)
        => FreshRandomSeedFactory?.Invoke(Context.EntityId, streamName)
           ?? (Context.EntityId + ":" + streamName);

    /// <summary>
    /// Optional seed factory wired by EntityGrain from
    /// <see cref="SharedMeta.Server.Core.Grains.EntityGrainOptions.FreshRandomSeedFactory"/>.
    /// Read by the default <see cref="CreateFreshRandomSeed"/> implementation; ignored when
    /// a derived class overrides that method directly.
    /// </summary>
    public System.Func<string, string, string>? FreshRandomSeedFactory { get; set; }

    private static MetaRandom DeserializeOrCreateRandom(IMetaSerializer serializer, byte[]? bytes, string seed)
    {
        if (bytes != null && bytes.Length > 0)
            return serializer.Unpack<MetaRandom>(bytes);
        return MetaRandom.FromString(seed);
    }

    /// <summary>
    /// Descriptors for named randoms declared via [NamedRandom] on the state.
    /// Overridden by the generated provider. Positional — index is stable across activations.
    /// </summary>
    protected virtual IReadOnlyList<NamedRandomDescriptor> NamedRandomDescriptors
        => System.Array.Empty<NamedRandomDescriptor>();

    private void InitializeNamedRandoms(IMetaProviderContext context)
    {
        var descriptors = NamedRandomDescriptors;
        if (descriptors.Count == 0)
        {
            _namedRandoms = System.Array.Empty<MetaRandom>();
            _namedRandomsView = null;
            return;
        }

        MetaRandom[]? persisted = null;
        var bytes = context.NamedRandomsBytes;
        if (bytes != null && bytes.Length > 0)
        {
            // Stored as positional array; treat mismatched length as a code change → reseed missing slots.
            persisted = context.Serializer.Unpack<MetaRandom[]>(bytes);
        }

        _namedRandoms = new MetaRandom[descriptors.Count];
        for (int i = 0; i < descriptors.Count; i++)
        {
            if (persisted != null && i < persisted.Length && persisted[i] != null)
            {
                _namedRandoms[i] = persisted[i];
            }
            else
            {
                var d = descriptors[i];
                // SeedOverride from [NamedRandom(Seed = "...")] is a literal pin and bypasses
                // the user hook by design — that attribute exists specifically so the user
                // can lock a stream to a fixed seed across all entities.
                var seed = d.SeedOverride ?? CreateFreshRandomSeed(d.Name);
                _namedRandoms[i] = MetaRandom.FromString(seed);
            }
        }

        _namedRandomsView = new IMetaRandom[_namedRandoms.Length];
        for (int i = 0; i < _namedRandoms.Length; i++)
            _namedRandomsView[i] = _namedRandoms[i];
    }

    /// <summary>
    /// Snapshot current ScrollId of each named random. Returns null if no named randoms declared
    /// so callers can skip the work entirely.
    /// </summary>
    private long[]? CaptureNamedScrolls()
    {
        if (_namedRandoms.Length == 0) return null;
        var snap = new long[_namedRandoms.Length];
        for (int i = 0; i < _namedRandoms.Length; i++)
            snap[i] = _namedRandoms[i].ScrollId;
        return snap;
    }

    /// <summary>
    /// Compute per-index deltas vs a previous snapshot. Returns null if all deltas are zero
    /// (no named random was advanced), so wire overhead stays at zero for the common case.
    /// </summary>
    private long[]? ComputeNamedScrollDeltas(long[]? before)
    {
        if (before == null || before.Length == 0) return null;
        long[]? deltas = null;
        for (int i = 0; i < before.Length; i++)
        {
            var d = _namedRandoms[i].ScrollId - before[i];
            if (d == 0) continue;
            deltas ??= new long[before.Length];
            deltas[i] = d;
        }
        return deltas;
    }


    /// <summary>
    /// Override to perform additional initialization after context and state are set.
    /// </summary>
    protected virtual void OnInitialize() { }

    /// <summary>
    /// Dispatch a service method call. Implemented by generated code.
    /// </summary>
    protected abstract Task<DispatchResult> DispatchCall(string serviceName, string methodName, byte[] payload);

    /// <summary>
    /// 0.20.0: Resolve a sibling-service impl instance by interface type. Default returns
    /// null. Generated providers override with a switch over service interface types,
    /// returning the cached <c>Get{Name}()</c> instance for any service hosted on this
    /// entity's <c>TState</c>. Wired into <see cref="MetaContext.SiblingServiceResolver"/>
    /// by <c>EntityGrain.OnActivateAsync</c> so generated cross-entity accessors can
    /// short-circuit self-targeted calls into typed sibling invocations.
    /// </summary>
    public virtual object? ResolveSiblingByType(System.Type interfaceType) => null;

    /// <summary>
    /// Dispatch a signal method call (void return, fire-and-forget). Default no-op;
    /// generated code overrides when the provider has one or more <c>[MetaMethod(Signal = true)]</c>
    /// methods and wires them into a generated <c>{Service}SignalDispatcher.Dispatch</c>.
    /// </summary>
    protected virtual Task DispatchSignal(string serviceName, string methodName, byte[] payload)
        => Task.CompletedTask;

    /// <summary>
    /// Dispatch an external event. Override in derived class if needed.
    /// </summary>
    protected virtual Task<DispatchResult> DispatchEvent(string subscriberInterface, string methodName, byte[] eventData)
    {
        return Task.FromResult(new DispatchResult { ResultBytes = null, TriggersToExecute = null });
    }

    /// <summary>
    /// Create a patch wrapper for the current state. Override in generated code.
    /// </summary>
    protected virtual object? CreatePatchWrapper(PatchNode root) => null;

    /// <summary>
    /// Current state schema version as last returned by <see cref="RunInitAsync"/>.
    /// Tracked so the lazy migration check knows when the schema needs advancing.
    /// </summary>
    protected int CurrentStateSchemaVersion { get; private set; }

    /// <summary>
    /// 0.20.0 fix: Seed the provider's schema version from the persisted <c>state.Version</c>
    /// at grain activation, BEFORE any deferred lazy-migration check runs. Without this, the
    /// fresh-entity-floor rule in the generated <c>CheckAndRunLazyMigrationAsync</c> sees
    /// <c>CurrentStateSchemaVersion == 0</c> on every activation and re-runs <c>[MetaInit]</c>
    /// even when the state has already been initialized in a previous session.
    /// </summary>
    public void SeedSchemaVersion(int persistedVersion)
    {
        CurrentStateSchemaVersion = persistedVersion;
        if (MetaContext != null) MetaContext.Version = persistedVersion;
    }

    /// <summary>
    /// Set to true by <see cref="CheckAndRunLazyMigrationAsync"/> when a lazy migration ran
    /// and the caller (EntityGrain) must force-persist state.
    /// Cleared by EntityGrain after it has persisted.
    /// </summary>
    public bool LazyMigrationCompleted { get; set; }

    /// <summary>New schema version after a lazy migration. Read by EntityGrain when
    /// <see cref="LazyMigrationCompleted"/> is true.</summary>
    public int LazyMigrationNewVersion { get; set; }

    /// <summary>
    /// Called at the start of <see cref="HandleCallAsync"/> to lazily apply any config-driven
    /// state schema migration that became necessary since the last activation. Returns true
    /// when migration ran and <see cref="LazyMigrationCompleted"/> / <see cref="LazyMigrationNewVersion"/>
    /// have been updated. Generated providers override this when the state declares
    /// <c>[MetaStateVersion]</c> attributes.
    /// <para>
    /// <paramref name="schemaCap"/> caps the migration target — when non-null, the framework
    /// migrates only up to that schema, not beyond. Used to honour
    /// <c>[MinStateVersion(N)]</c> on the dispatched method.
    /// </para>
    /// </summary>
    protected virtual Task<bool> CheckAndRunLazyMigrationAsync(int? schemaCap = null) => Task.FromResult(false);

    /// <summary>
    /// Returns the maximum state schema permitted for a given client (per its resolved
    /// <c>[MetaConfigVersion]</c> branch). Used to gate activation-time and lazy migration
    /// so a connecting 1.x client cannot trigger a migration to schema 2 even when the
    /// provider's <c>CurrentVersion</c> would otherwise satisfy that step.
    /// <para>
    /// Default returns null (uncapped) — same as before, preserving behaviour when no
    /// <c>[MetaStateVersion]</c> declarations exist. Generated providers override.
    /// </para>
    /// </summary>
    public virtual int? ComputeSchemaCapForClient(string? clientVersion) => null;

    /// <summary>
    /// Public entry point for EntityGrain to drive client-aware init/migration outside the
    /// per-call dispatch path (e.g. on Subscribe). Returns true when the schema advanced.
    /// </summary>
    public Task<bool> RunInitOrMigrateAsync(int? schemaCap)
        => CheckAndRunLazyMigrationAsync(schemaCap);

    /// <summary>
    /// Per-method migration policy: returns true when the dispatched method carries
    /// <c>[NoMigrate]</c>. Generated providers override; default is no skips.
    /// </summary>
    protected virtual bool ShouldSkipMigration(string serviceName, string methodName) => false;

    /// <summary>
    /// Per-method migration policy: returns the schema cap declared via
    /// <c>[MinStateVersion(N)]</c>, or null when uncapped. Generated providers override.
    /// </summary>
    protected virtual int? GetMethodMinStateVersion(string serviceName, string methodName) => null;

    /// <summary>
    /// For <c>[NoMigrate]</c> calls: returns the config object pinned to the schema-floor
    /// branch — i.e. the highest config branch that does not require migration past
    /// the entity's current state schema. Generated providers override when the state
    /// declares <c>[MetaStateVersion]</c>; default returns null (no pinning).
    /// </summary>
    protected virtual object? GetSchemaFloorConfig(int stateSchema) => null;

    /// <summary>
    /// Returns the resolved <see cref="MetaConfigVersion"/> behind <see cref="GetSchemaFloorConfig"/>
    /// (so <c>Context.ConfigVersion</c> can stay in sync with <c>Context.Config</c>).
    /// Default: zero version.
    /// </summary>
    protected virtual MetaConfigVersion GetSchemaFloorConfigVersion(int stateSchema) => default;

    public async Task<HandleCallResult> HandleCallAsync(RpcCall call)
    {
        if (MetaContext == null || Context == null)
        {
            return new HandleCallResult { Response = new RpcResponse { Error = "Provider not initialized" } };
        }

        // Per-call migration policy. Two caps stack:
        //   [NoMigrate]            → skip lazy migration AND pin config to schema-floor branch.
        //   [MinStateVersion(N)]   → migrate only up to N (no further).
        //   ComputeSchemaCapForClient → migrate only up to caller's resolved config branch
        //                                (so a 1.x client doesn't trigger a 2.0 migration on
        //                                 a fresh entity it just activated).
        // Effective cap = min of the two non-null caps. Per-caller config pin then runs
        // through GetCachedConfigForClient.
        bool skipMigration = ShouldSkipMigration(call.ServiceName, call.MethodName);
        int? schemaCap = null;
        if (!skipMigration)
        {
            var methodCap = GetMethodMinStateVersion(call.ServiceName, call.MethodName);
            var clientCap = ComputeSchemaCapForClient(call.CallerClientVersion);
            schemaCap = methodCap.HasValue && clientCap.HasValue
                ? System.Math.Min(methodCap.Value, clientCap.Value)
                : (methodCap ?? clientCap);
            await CheckAndRunLazyMigrationAsync(schemaCap);
        }

        if (skipMigration)
        {
            // Pin to schema-floor config so this call sees the same config branch the entity
            // was last persisted under — never the latest branch (which would require migration).
            var floorConfig = GetSchemaFloorConfig(CurrentStateSchemaVersion);
            if (floorConfig != null)
            {
                MetaContext.Config = floorConfig;
                MetaContext.ConfigVersion = GetSchemaFloorConfigVersion(CurrentStateSchemaVersion);
            }
        }
        else
        {
            // Per-call config resolution: each call computes against the config branch
            // appropriate for its caller. Cached internally so it costs O(1) per call.
            var perCallConfig = GetCachedConfigForClient(call.CallerClientVersion);
            if (perCallConfig != null)
            {
                MetaContext.Config = perCallConfig;
                MetaContext.ConfigVersion = ResolveClientConfigVersion(call.CallerClientVersion);
            }
        }

        // Expose current schema version on the context so service code can branch on it
        // (e.g. inside [NoMigrate] methods that must be schema-tolerant).
        MetaContext.Version = CurrentStateSchemaVersion;

        try
        {
            MetaContext.CallerId = call.CallerId;
            MetaContext.CallerClientVersion = call.CallerClientVersion;
            MetaContext.ServerTimeTicks = call.ServerTimeTicks;
            MetaContext.Random = _optimisticRandom;
            MetaContext.ServerRandom = new MetaRandomRecorder(_serverRandom, MetaContext);
            MetaContext.NamedRandoms = _namedRandomsView;
            MetaContextAccessor.Current = MetaContext;

            // Capture optimistic random scroll position before dispatch
            var scrollIdBefore = _optimisticRandom.ScrollId;
            var namedScrollsBefore = CaptureNamedScrolls();

            // Determine server-side execution mode
            var executionMode = ExecutionModeProvider?.GetMode(
                call.ServiceName, call.MethodName, ExecutionMode.Optimistic) ?? ExecutionMode.Optimistic;

            // Set up patch tracking for ServerPatch mode or deep desync detection
            PatchNode? patchRoot = null;
            bool isServerReplace = executionMode == ExecutionMode.ServerReplace;
            bool deepDesyncActive = DeepDesyncEnabled || call.DeepDesyncRequested;
            if (executionMode == ExecutionMode.ServerPatch || deepDesyncActive)
            {
                patchRoot = new PatchNode(-1);
                MetaContext.PatchWrapper = CreatePatchWrapper(patchRoot);
            }

            // Begin recording for replay
            MetaContext.BeginOperation();

            // Dispatch the call
            var result = await DispatchCall(call.ServiceName, call.MethodName, call.Payload);

            // End recording and get replay payload
            var replayPayload = MetaContext.EndOperation();

            // Collect patch bytes if ServerPatch mode was active
            byte[]? patchBytes = null;
            if (patchRoot != null)
            {
                patchRoot.Prune();
                if (patchRoot.HasChanges)
                    patchBytes = Context.Serializer.Pack(patchRoot);
                MetaContext.PatchWrapper = null;
            }

            // Compute optimistic random scroll delta for desync detection
            var randomScrollDelta = _optimisticRandom.ScrollId - scrollIdBefore;
            var namedRandomScrollDeltas = ComputeNamedScrollDeltas(namedScrollsBefore);

            // Deep desync: compute CRC from patch (field-level mutation tracking)
            uint? deepDesyncCrc = null;
            if (deepDesyncActive && patchRoot != null)
            {
                patchRoot.Prune();
                if (patchRoot.HasChanges)
                {
                    var deepDesyncPatchBytes = Context.Serializer.Pack(patchRoot);
                    deepDesyncCrc = SharedMeta.Core.Patch.PatchCrc.Compute(deepDesyncPatchBytes);
                }
                else
                {
                    deepDesyncCrc = 0; // no changes
                }
            }

            // Capture cross-entity calls made during this operation
            var crossEntityCalls = MetaContext.CrossEntityCalls;

            // Build response
            var response = new HandleCallResult
            {
                Response = new RpcResponse
                {
                    ResultBytes = result.ResultBytes,
                    ReplayPayload = replayPayload,
                    Error = null,
                    RandomScrollDelta = randomScrollDelta,
                    NamedRandomScrollDeltas = namedRandomScrollDeltas,
                    PatchBytes = patchBytes,
                    DeepDesyncCrc = deepDesyncCrc
                },
                Broadcasts = new List<EntityBroadcast>(),
                CrossEntityCalls = crossEntityCalls,
                ForcePersist = result.ForcePersist
            };

            // Create broadcast for this call (to other subscribers)
            var mainBroadcast = new EntityBroadcast
            {
                ServiceName = call.ServiceName,
                MethodName = call.MethodName,
                Payload = call.Payload,
                ReplayPayload = replayPayload,
                ExcludePlayerId = call.CallerId, // Don't broadcast back to caller
                ServerTimeTicks = call.ServerTimeTicks,
                RandomScrollDelta = randomScrollDelta,
                NamedRandomScrollDeltas = namedRandomScrollDeltas,
                PatchBytes = patchBytes
            };

            // Handle triggers if any — nest inside main broadcast
            if (result.TriggersToExecute is { Count: > 0 } triggers)
            {
                mainBroadcast.TriggerBroadcasts = new List<EntityBroadcast>();
                foreach (var triggerMethod in triggers)
                {
                    var triggerScrollBefore = _optimisticRandom.ScrollId;
                    var triggerNamedScrollsBefore = CaptureNamedScrolls();

                    // Set up patch tracking for trigger (if ServerPatch mode)
                    PatchNode? triggerPatchRoot = null;
                    if (executionMode == ExecutionMode.ServerPatch)
                    {
                        triggerPatchRoot = new PatchNode(-1);
                        MetaContext.PatchWrapper = CreatePatchWrapper(triggerPatchRoot);
                    }

                    MetaContext.BeginOperation();
                    var triggerResult = await DispatchCall(call.ServiceName, triggerMethod, []);
                    var triggerReplay = MetaContext.EndOperation();

                    // Collect trigger patch bytes
                    byte[]? triggerPatchBytes = null;
                    if (triggerPatchRoot != null)
                    {
                        triggerPatchRoot.Prune();
                        if (triggerPatchRoot.HasChanges)
                            triggerPatchBytes = Context.Serializer.Pack(triggerPatchRoot);
                        MetaContext.PatchWrapper = null;
                    }

                    var triggerScrollDelta = _optimisticRandom.ScrollId - triggerScrollBefore;
                    var triggerNamedDeltas = ComputeNamedScrollDeltas(triggerNamedScrollsBefore);

                    mainBroadcast.TriggerBroadcasts.Add(new EntityBroadcast
                    {
                        ServiceName = call.ServiceName,
                        MethodName = triggerMethod,
                        Payload = [], // Triggers have no arguments
                        ReplayPayload = triggerReplay,
                        ExcludePlayerId = null, // Broadcast triggers to everyone
                        ServerTimeTicks = call.ServerTimeTicks,
                        RandomScrollDelta = triggerScrollDelta,
                        NamedRandomScrollDeltas = triggerNamedDeltas,
                        PatchBytes = triggerPatchBytes
                    });
                }
            }

            // ServerReplace: serialize full state AFTER all triggers, capturing final state
            byte[]? stateBytes = null;
            if (isServerReplace)
            {
                stateBytes = GetStateBytes();
                response.Response.StateBytes = stateBytes;
                response.Response.ReplayPayload = null; // no replay needed
                mainBroadcast.StateBytes = stateBytes;
                mainBroadcast.ReplayPayload = null;
            }

            response.Broadcasts.Add(mainBroadcast);

            return response;
        }
        catch (Exception ex)
        {
            Logger.ProviderCallError(ex, call.ServiceName, call.MethodName);
            return new HandleCallResult { Response = new RpcResponse { Error = ex.Message } };
        }
        finally
        {
            MetaContextAccessor.Current = null;
        }
    }

    /// <summary>
    /// 0.20.0: Same-grain nested call dispatcher. Used by <c>EntityGrain.EntityCallHandler</c>
    /// when a cross-entity call's <c>targetEntityId</c> resolves to the calling grain itself
    /// (gift-to-self). Bypasses Orleans grain RPC — which would deadlock since
    /// <c>EntityGrain</c> is non-reentrant — and dispatches directly on this provider's
    /// cached service instances under the same <see cref="MetaContext"/>.
    /// <para>
    /// The nested call runs as a sub-operation: it gets its own replay-payload buffer and
    /// its own <c>CrossEntityCalls</c> list (so further-nested calls don't pollute the
    /// outer's collection), but shares state, randoms, named-randoms, and the outer's
    /// <c>PatchWrapper</c> (mutations flow into the outer's patch tree — patches stay one
    /// per outer call from the client's perspective).
    /// </para>
    /// <para>
    /// Caller-context fields (<c>CallerId</c>, <c>CallerClientVersion</c>,
    /// <c>ServerTimeTicks</c>) are inherited from the outer call — the original network
    /// caller is still the originator of every nested step.
    /// </para>
    /// <para>
    /// Returns a <see cref="CrossEntityCallInfo"/> populated with inner replay bytes and
    /// result bytes so the upstream <c>CallEntityAsync</c> caller produces the same
    /// recording shape as a real cross-entity hop — clients replaying the outer operation
    /// see the inner step exactly as if it had crossed grain boundaries.
    /// </para>
    /// </summary>
    public async Task<CrossEntityCallInfo> HandleNestedCallAsync(string targetEntityId, string serviceName, string methodName, byte[] argsBytes)
    {
        if (MetaContext == null)
            throw new InvalidOperationException("Provider not initialized.");

        // Push nested writer/debug/crossEntityCalls — saves outer state, starts fresh inner op.
        // Inner-call replay bytes are discarded (matches the CrossEntityCallInfo shape produced
        // by the real grain-RPC path, which also doesn't ship the target's full replay payload
        // back through the cross-entity tuple — only ResultBytes does).
        var frame = MetaContext.PushNestedOperation();

        DispatchResult result;
        try
        {
            result = await DispatchCall(serviceName, methodName, argsBytes);
        }
        finally
        {
            // Always pop, even on exception, to keep outer recorder state coherent.
            MetaContext.PopNestedOperation(frame, out _);
        }

        return new CrossEntityCallInfo
        {
            EntityId = targetEntityId,
            ServiceName = serviceName,
            MethodName = methodName,
            ResultBytes = result.ResultBytes,
            EntitySequenceNumber = 0  // self-call shares outer's sequence; no separate seq increment
        };
    }

    public async Task<HandleEventResult> HandleExternalEventAsync(
        string subscriberInterface,
        string methodName,
        byte[] eventData,
        string? callerId = null)
    {
        if (MetaContext == null || Context == null)
        {
            return new HandleEventResult();
        }

        try
        {
            MetaContext.CallerId = callerId;
            MetaContext.ServerTimeTicks = DateTime.UtcNow.Ticks;
            MetaContextAccessor.Current = MetaContext;

            MetaContext.BeginOperation();
            var result = await DispatchEvent(subscriberInterface, methodName, eventData);
            var replayPayload = MetaContext.EndOperation();

            var broadcasts = new List<EntityBroadcast>();

            // Create broadcast for this event
            broadcasts.Add(new EntityBroadcast
            {
                ServiceName = subscriberInterface,
                MethodName = methodName,
                Payload = eventData,
                ReplayPayload = replayPayload,
                ExcludePlayerId = null, // Broadcast to everyone
                ServerTimeTicks = MetaContext.ServerTimeTicks
            });

            return new HandleEventResult { Broadcasts = broadcasts };
        }
        catch (Exception ex)
        {
            Logger.ProviderEventError(ex, subscriberInterface, methodName);
            return new HandleEventResult();
        }
        finally
        {
            MetaContextAccessor.Current = null;
        }
    }

    public async Task<QueryCallResponse> HandleQueryAsync(RpcCall call)
    {
        if (MetaContext == null || Context == null)
            return new QueryCallResponse { Error = "Provider not initialized" };

        bool skipMigration = ShouldSkipMigration(call.ServiceName, call.MethodName);
        int? schemaCap = null;
        if (!skipMigration)
        {
            var methodCap = GetMethodMinStateVersion(call.ServiceName, call.MethodName);
            var clientCap = ComputeSchemaCapForClient(call.CallerClientVersion);
            schemaCap = methodCap.HasValue && clientCap.HasValue
                ? System.Math.Min(methodCap.Value, clientCap.Value)
                : (methodCap ?? clientCap);
        }

        // Lazy migration: run any pending config-driven schema steps before serving the
        // query so the result reflects the up-to-date state. Queries don't go through
        // HandleCallAsync, so the check is duplicated here. EntityGrain checks
        // LazyMigrationCompleted after HandleCallAsync; for queries we persist inline via
        // SaveStateHandler when migration ran. [NoMigrate]/[MinStateVersion] policies apply.
        if (!skipMigration && await CheckAndRunLazyMigrationAsync(schemaCap) && SaveStateHandler != null)
            await SaveStateHandler();

        if (skipMigration)
        {
            var floorConfig = GetSchemaFloorConfig(CurrentStateSchemaVersion);
            if (floorConfig != null)
            {
                MetaContext.Config = floorConfig;
                MetaContext.ConfigVersion = GetSchemaFloorConfigVersion(CurrentStateSchemaVersion);
            }
        }
        else
        {
            var perCallConfig = GetCachedConfigForClient(call.CallerClientVersion);
            if (perCallConfig != null)
            {
                MetaContext.Config = perCallConfig;
                MetaContext.ConfigVersion = ResolveClientConfigVersion(call.CallerClientVersion);
            }
        }

        MetaContext.Version = CurrentStateSchemaVersion;

        try
        {
            // Set up minimal context for the query (read-only)
            MetaContext.CallerId = call.CallerId;
            MetaContext.CallerClientVersion = call.CallerClientVersion;
            MetaContext.ServerTimeTicks = DateTime.UtcNow.Ticks;
            MetaContextAccessor.Current = MetaContext;

            // Dispatch the call — same dispatcher, but no replay/random/broadcast machinery
            var result = await DispatchCall(call.ServiceName, call.MethodName, call.Payload);

            return new QueryCallResponse
            {
                Success = true,
                ResultBytes = result.ResultBytes
            };
        }
        catch (Exception ex)
        {
            Logger.ProviderCallError(ex, call.ServiceName, call.MethodName);
            return new QueryCallResponse { Error = ex.Message };
        }
        finally
        {
            MetaContextAccessor.Current = null;
        }
    }

    /// <summary>
    /// Check if a method is a query method. Generated code overrides.
    /// </summary>
    public virtual bool IsQueryMethod(string serviceName, string methodName) => false;

    /// <summary>
    /// Check if a query method has OpenAccess. Generated code overrides.
    /// </summary>
    public virtual bool IsOpenAccessQuery(string serviceName, string methodName) => false;

    /// <summary>
    /// Check if a method is a signal method (fire-and-forget, void return). Generated code overrides.
    /// </summary>
    public virtual bool IsSignalMethod(string serviceName, string methodName) => false;

    /// <summary>
    /// Handle a signal call. Dispatches the method through <see cref="DispatchSignal"/> but skips
    /// replay recording, broadcasts, random state tracking, persistence, and response generation.
    /// Bridges called from inside the signal body are wrapped by their normal Recorder, but the
    /// Recorder writes into <see cref="NullServerRecordContext"/> — real side-effects still happen,
    /// recording is a no-op.
    /// Errors are caught and logged — they do not propagate back to the client (fire-and-forget).
    /// </summary>
    public async Task HandleSignalAsync(RpcCall call)
    {
        if (MetaContext == null || Context == null)
        {
            Logger.ProviderCallError(new InvalidOperationException("Provider not initialized"), call.ServiceName, call.MethodName);
            return;
        }

        try
        {
            MetaContext.CallerId = call.CallerId;
            MetaContext.ServerTimeTicks = DateTime.UtcNow.Ticks;
            // Enable signal mode so any bridge Recorder's Writer.Write becomes a no-op
            // (the payload produced during signal execution has no consumer).
            MetaContext.SignalMode = true;
            MetaContextAccessor.Current = MetaContext;

            await DispatchSignal(call.ServiceName, call.MethodName, call.Payload);
        }
        catch (Exception ex)
        {
            // Signal is fire-and-forget by contract — log and swallow so the entity grain
            // does not see an exception that would become a transport-level error on the session.
            Logger.ProviderCallError(ex, call.ServiceName, call.MethodName);
        }
        finally
        {
            MetaContext.SignalMode = false;
            MetaContextAccessor.Current = null;
        }
    }

    public byte[] GetStateBytes()
    {
        if (Context == null || State == null) return [];
        return Context.Serializer.Pack(State);
    }

    public byte[] GetServerRandomBytes()
    {
        if (Context == null) return [];
        return Context.Serializer.Pack(_serverRandom);
    }

    public byte[] GetOptimisticRandomBytes()
    {
        if (Context == null) return [];
        return Context.Serializer.Pack(_optimisticRandom);
    }

    public byte[] GetNamedRandomsBytes()
    {
        if (Context == null || _namedRandoms.Length == 0) return [];
        return Context.Serializer.Pack(_namedRandoms);
    }

    public async Task<int> InitializeStateAsync(int currentVersion)
    {
        if (MetaContext == null) return currentVersion;

        // Set up ServerRandom so [MetaInit] methods can use it
        MetaContext.ServerRandom = new MetaRandomRecorder(_serverRandom, MetaContext);
        MetaContext.Random = _optimisticRandom;
        MetaContext.NamedRandoms = _namedRandomsView;
        MetaContext.Version = currentVersion;
        MetaContextAccessor.Current = MetaContext;
        MetaContext.BeginOperation();

        try
        {
            var newVersion = await RunInitAsync(currentVersion);
            CurrentStateSchemaVersion = newVersion;
            MetaContext.Version = newVersion;
            return newVersion;
        }
        finally
        {
            MetaContext.EndOperation(); // discard replay payload (init is server-only)
            MetaContextAccessor.Current = null;
            MetaContext.ServerRandom = null;
        }
    }

    /// <summary>
    /// Override in generated code to call [MetaInit] methods.
    /// ServerRandom and Config are available during this call.
    /// When the state declares [MetaStateVersion] attributes, the generated override calls
    /// each service's [MetaInit] with the config pinned to the transition version.
    /// </summary>
    protected virtual Task<int> RunInitAsync(int currentVersion)
    {
        return Task.FromResult(currentVersion);
    }

    public virtual void OnDeactivating() { }

    /// <summary>
    /// Access policy for this entity type. Default: Open.
    /// Generated providers override for OwnerOnly or Authorized.
    /// </summary>
    public virtual EntityAccessPolicy AccessPolicy => EntityAccessPolicy.Open;

    /// <summary>
    /// Config version for this entity. Set during activation via InitializeConfig.
    /// </summary>
    public MetaConfigVersion ConfigVersion { get; private set; }

    /// <summary>
    /// Initialize config for this entity with the given version.
    /// Generated code overrides to resolve config from IMetaConfigProvider.
    /// </summary>
    public virtual void InitializeConfig(MetaConfigVersion version)
    {
        ConfigVersion = version;
        if (MetaContext != null) MetaContext.ConfigVersion = version;
    }

    /// <summary>
    /// Resolve the config version appropriate for a specific connecting client. Called by
    /// EntityGrain when returning a subscribe snapshot so each subscriber's
    /// <c>SubscribeResponse.ConfigVersion</c> reflects the correct config branch for their
    /// app version per <c>[MetaConfigVersion]</c> rules on the config class.
    ///
    /// Generated providers override this when the config class declares
    /// <see cref="SharedMeta.Core.MetaConfigVersionAttribute"/> rules and a config provider
    /// is registered. The default falls back to the entity's pinned <see cref="ConfigVersion"/>.
    /// </summary>
    public virtual MetaConfigVersion ResolveClientConfigVersion(string? clientVersion)
        => ConfigVersion;

    /// <summary>
    /// Per-call config resolution: returns the config object appropriate for the caller
    /// identified by <paramref name="clientVersion"/>. The default returns null (no config
    /// system); generated providers override this with a typed two-level cache:
    ///   1. clientVersion → resolved <see cref="MetaConfigVersion"/> (via [MetaConfigVersion] rules)
    ///   2. resolved version → TConfig instance (via IMetaConfigProvider.GetConfig)
    /// Both caches are invalidated when the provider's <c>CurrentVersion</c> advances
    /// (runtime patch deploy) so the next call picks up the new branch.
    /// </summary>
    protected virtual object? GetCachedConfigForClient(string? clientVersion) => null;

    /// <summary>Called by generated <see cref="OnDeactivating"/> overrides to drop the per-call config cache.</summary>
    protected virtual void ClearConfigCache() { }

    /// <summary>
    /// Returns true when the client's resolved config version is high enough to be
    /// compatible with the entity's current state schema. Called by EntityGrain during
    /// SubscribeAsync — if false the subscribe is rejected so the client knows to upgrade.
    ///
    /// Generated providers override this when the state class declares
    /// <see cref="SharedMeta.Core.MetaStateVersionAttribute"/> attributes.
    /// Default returns true (no schema requirements).
    /// </summary>
    public virtual bool IsClientConfigCompatible(MetaConfigVersion resolvedClientConfigVersion)
        => true;

    /// <summary>
    /// Check if a player is authorized to subscribe to this entity.
    /// Default: Open → true, OwnerOnly → entityId == playerId, Authorized → false (must be overridden).
    /// Generated providers override for Authorized to call service.IsAuthorized(playerId).
    /// </summary>
    public virtual Task<bool> CheckAccessAsync(string playerId)
    {
        return AccessPolicy switch
        {
            EntityAccessPolicy.Open => Task.FromResult(true),
            EntityAccessPolicy.OwnerOnly or EntityAccessPolicy.UserOwned
                => Task.FromResult(Context.EntityId == playerId),
            _ => Task.FromResult(false) // Authorized without override → deny by default
        };
    }
}
