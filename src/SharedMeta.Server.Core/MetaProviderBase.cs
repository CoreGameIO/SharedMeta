using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Network;
using SharedMeta.Core.Packets;
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

    // Thin wrapper exposing ProviderCallError logging to generated code in user assemblies
    // (the underlying Log extension is internal to SharedMeta.Server.Core).
    protected void LogProviderCallError(Exception ex, string serviceName, string methodName)
        => Logger.ProviderCallError(ex, serviceName, methodName);

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

    // Fire-and-forget cross-entity dispatch for [MetaMethod(OneWay = true)] — routes through
    // Orleans [OneWay] so the source doesn't wait for a reply envelope.
    public Action<string, string, string, byte[], long>? EntityCallOneWayHandler { get; set; }

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
        MetaContext.EntityCallOneWayHandler = EntityCallOneWayHandler;
        MetaContext.EntityStateHandler = EntityStateHandler;
        MetaContext.SaveStateHandler = SaveStateHandler;
        // Sibling-service resolver: generated cross-entity accessors detect self-targeted
        // calls and dispatch directly on the cached sibling impl — no serialization, no
        // grain RPC, no re-entrancy deadlock.
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
    /// Dispatches a service method call. Implemented by generated code.
    /// <para><c>methodVersion</c> selects between coexisting <c>[MetaMethod(Version = N)]</c>
    /// declarations on the same alias. Pass 0 for the legacy / unversioned route — the
    /// dispatcher resolves it to the lowest-versioned impl so older clients still dispatch.</para>
    /// </summary>
    protected abstract ValueTask<DispatchResult> DispatchCall(string serviceName, string methodName, byte[] payload, int methodVersion);

    /// <summary>
    /// Resolves a sibling-service impl by interface type. Default returns null; generated
    /// providers override with a switch over services hosted on this entity's TState so
    /// self-targeted cross-entity calls dispatch directly without an Orleans hop.
    /// </summary>
    public virtual object? ResolveSiblingByType(System.Type interfaceType) => null;

    /// <summary>
    /// Dispatches a signal method call (void, fire-and-forget). Default no-op; generated
    /// code overrides for providers that have <c>[MetaMethod(Mode = ExecutionMode.Signal)]</c>
    /// methods, routing them through <c>{Service}SignalDispatcher.Dispatch</c>.
    /// </summary>
    protected virtual Task DispatchSignal(string serviceName, string methodName, byte[] payload, int methodVersion)
        => Task.CompletedTask;

    /// <summary>
    /// Dispatch an external event. Override in derived class if needed.
    /// </summary>
    protected virtual ValueTask<DispatchResult> DispatchEvent(string subscriberInterface, string methodName, byte[] eventData)
    {
        return new ValueTask<DispatchResult>(new DispatchResult { ResultBytes = null, TriggersToExecute = null });
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
    /// Seeds the provider's schema version from persisted state at grain activation, BEFORE
    /// any deferred lazy-migration check runs. Without this the fresh-entity-floor rule in
    /// the generated <c>CheckAndRunLazyMigrationAsync</c> would re-run [MetaInit] on every
    /// activation, mistaking persisted state for fresh.
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
    /// <paramref name="callerClientVersion"/> is the client app version that triggered the
    /// migration (e.g. the calling client on HandleCallAsync, the subscribing client on
    /// EntityGrain.SubscribeAsync). Required — generated migration step conditions resolve
    /// the relevant config versions from it via
    /// <see cref="IMetaConfigProvider{TConfig}.ResolveForClient"/>. Migration is always
    /// client-driven; passing null indicates "no migration available."
    /// </para>
    /// <para>
    /// <paramref name="schemaCap"/> caps the migration target — when non-null, the framework
    /// migrates only up to that schema, not beyond. Used to honour
    /// <c>[MinStateVersion(N)]</c> on the dispatched method.
    /// </para>
    /// </summary>
    protected virtual Task<bool> CheckAndRunLazyMigrationAsync(string? callerClientVersion, int? schemaCap = null)
        => Task.FromResult(false);

    /// <summary>
    /// Holds the <c>CallerClientVersion</c> of the in-flight migration so generated
    /// <c>RunInitAsync</c> step conditions can resolve config versions per-client without
    /// taking a new parameter. Set by <see cref="CheckAndRunLazyMigrationAsync"/> right
    /// before invoking <c>InitializeStateAsync</c>; restored in finally. Mirrors the
    /// <c>_migrationCap</c> pattern.
    /// </summary>
    protected string? MigrationClientVersion { get; set; }

    /// <summary>
    /// The <see cref="EntityScope"/> declared on the state class. Default is
    /// <see cref="EntityScope.Private"/>; generated providers override when the state
    /// class declares <c>[EntityScope(...)]</c>.
    /// </summary>
    public virtual EntityScope Scope => EntityScope.Private;

    /// <summary>
    /// For <see cref="EntityScope.Global"/> entities, returns the client version that should
    /// drive migration checks in <c>HandleCallAsync</c> — typically
    /// <see cref="IConfigVersionResolver.CurrentClientVersion"/>. Generated providers override
    /// this when the state declares <c>[EntityScope(Global)]</c> AND the provider has access
    /// to <see cref="IConfigVersionResolver"/> via DI. Default returns the caller's version
    /// unchanged — safe fallback for providers without a resolver.
    /// </summary>
    protected virtual string? ResolveMigrationDriverForGlobal(string? callerClientVersion) => callerClientVersion;

    // Runtime-only config-version pins (not persisted): die with grain deactivation,
    // re-establish on the next first-subscribe. Keyed by configType.FullName so
    // multi-config entities pin one version per type independently. Per-scope rules:
    //   Private — pin set on owner's subscribe, lives until deactivation.
    //   Shared  — pin set by first subscriber; joiners validated (Major.Minor match,
    //             Patch may downgrade); Major.Minor mismatch rejects.
    //   Global  — never pins; every call resolves freshly from the resolver.
    private readonly Dictionary<string, MetaConfigVersion> _activeConfigPins = new();

    /// <summary>
    /// The set of config-type → pinned-version entries currently active on this provider.
    /// Empty when the grain has no active subscribers (cold) or when the entity's scope is
    /// <see cref="EntityScope.Global"/> (never pins).
    /// </summary>
    public IReadOnlyDictionary<string, MetaConfigVersion> ActiveConfigPins => _activeConfigPins;

    /// <summary>
    /// Sets a pin for a config type. Replaces any existing pin for the same key — callers
    /// that need first-write-wins semantics should check <see cref="TryGetConfigPin"/> first.
    /// </summary>
    public void SetConfigPin(string configTypeFullName, MetaConfigVersion version)
    {
        if (string.IsNullOrEmpty(configTypeFullName))
            throw new ArgumentException("configTypeFullName must be non-empty", nameof(configTypeFullName));
        _activeConfigPins[configTypeFullName] = version;
    }

    /// <summary>
    /// Look up the pinned <see cref="MetaConfigVersion"/> for a config type.
    /// Returns <c>false</c> when no pin is set (cold grain, Global scope, or pin already
    /// cleared) — callers should fall back to per-call resolution from the client's app
    /// version (or to <see cref="IConfigVersionResolver.CurrentClientVersion"/> for
    /// server-internal callers).
    /// </summary>
    public bool TryGetConfigPin(string configTypeFullName, out MetaConfigVersion version)
    {
        return _activeConfigPins.TryGetValue(configTypeFullName, out version);
    }

    /// <summary>
    /// Drops all active pins. Called by EntityGrain when the last subscriber leaves — the
    /// next first-subscribe re-establishes pins, picking up any newer versions published
    /// while the entity was idle.
    /// </summary>
    public void ClearConfigPins()
    {
        _activeConfigPins.Clear();
    }

    /// <summary>
    /// Resolve and set config-version pins from a client app version. Called by EntityGrain
    /// on first subscribe for <see cref="EntityScope.Private"/> and <see cref="EntityScope.Shared"/>
    /// scopes. Generated providers override to walk their registered <c>IMetaConfigProvider&lt;&gt;</c>
    /// fields (primary + secondaries) and call <see cref="SetConfigPin"/> with the resolved
    /// <see cref="MetaConfigVersion"/> for each.
    /// <para>
    /// Default base implementation is a no-op — used by states without any config providers,
    /// where pinning is meaningless.
    /// </para>
    /// </summary>
    public virtual void EstablishConfigPinsFromClientVersion(string? clientVersion)
    {
        // Default: no config providers, no pins to set.
    }

    /// <summary>
    /// Shared scope only: validates a joining client's config versions against the pins.
    /// Returns true when every pinned config's Major.Minor matches the joiner's resolved
    /// version (Patch may differ — joiner downgrades). False = joiner on a different branch;
    /// <paramref name="reason"/> carries a human-readable diff for the rejection exception.
    /// Default returns true; generator emits an override for Shared-scope states.
    /// </summary>
    public virtual bool ValidateClientCompatibleWithPins(string? clientVersion, out string? reason)
    {
        reason = null;
        return true;
    }

    /// <summary>
    /// Returns the maximum state schema permitted for a given client (per its resolved
    /// <c>[MetaConfigVersion]</c> branch). Used to gate activation-time and lazy migration
    /// so a connecting 1.x client cannot trigger a migration to schema 2 even when the
    /// server has a newer config branch published.
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
    public Task<bool> RunInitOrMigrateAsync(string? callerClientVersion, int? schemaCap)
        => CheckAndRunLazyMigrationAsync(callerClientVersion, schemaCap);

    /// <summary>
    /// Admin entry point — drives the standard migration pipeline against
    /// <paramref name="floorClientVersion"/> without requiring a real subscriber. Returns
    /// true when migration ran. Use case: dropping support for an old config branch, iterate
    /// known entity IDs and call on each. EntityGrain force-persists on true.
    /// </summary>
    public Task<bool> ForceMigrateToFloorAsync(string floorClientVersion)
    {
        if (string.IsNullOrEmpty(floorClientVersion))
            throw new ArgumentException("floorClientVersion must be non-empty", nameof(floorClientVersion));
        // Uncapped — admin force-migrate runs the full migration ladder up to the floor.
        // ComputeRequiredStateSchema inside the call resolves floorClientVersion against
        // [MetaConfigVersion] rules to derive the actual schema floor.
        return CheckAndRunLazyMigrationAsync(floorClientVersion, schemaCap: null);
    }

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

    // Per-method validation (e.g. IsClientCallable guards for [GenerateClientApi=false]) is
    // emitted inline by the generator as a HandleCallAsync override that runs the switch then
    // calls base. Conditional: only emitted when at least one method opts out, so projects
    // with no such methods reach this entry point with zero validation overhead.
    public virtual async ValueTask<HandleCallResult> HandleCallAsync(RpcCall call, bool isClientOriginated = true, bool requirePatchForFanOut = false)
    {
        if (MetaContext == null || Context == null)
        {
            return new HandleCallResult { Response = new MetaOperation { Error = "Provider not initialized" } };
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
        // Private/Shared with an active pin lock schema to subscribe-time driver: a
        // cross-entity call from a higher-version client can't advance schema. Global
        // substitutes the resolver's CurrentClientVersion as the migration driver.
        bool pinLocksMigration = ActiveConfigPins.Count > 0 && Scope != EntityScope.Global;
        if (!skipMigration && !pinLocksMigration)
        {
            var methodCap = GetMethodMinStateVersion(call.ServiceName, call.MethodName);
            var migrationDriver = Scope == EntityScope.Global ? ResolveMigrationDriverForGlobal(call.CallerClientVersion) : call.CallerClientVersion;
            var clientCap = ComputeSchemaCapForClient(migrationDriver);
            schemaCap = methodCap.HasValue && clientCap.HasValue
                ? System.Math.Min(methodCap.Value, clientCap.Value)
                : (methodCap ?? clientCap);
            await CheckAndRunLazyMigrationAsync(migrationDriver, schemaCap);
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
            MetaContext.IsCrossOptimistic = call.IsCrossOptimistic;
            MetaContext.ServerTimeTicks = call.ServerTimeTicks;
            MetaContext.IsClientCall = isClientOriginated;
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

            // Activate patch tracking when ServerPatch mode is in effect, deep-desync needs
            // a CRC, OR EntityGrain signals that at least one subscriber needs the patch
            // payload (requirePatchForFanOut) — in the last case the broadcast carries BOTH
            // replay and patch; per-subscriber tailoring strips one on fan-out.
            PatchNode? patchRoot = null;
            bool isServerReplace = executionMode == ExecutionMode.ServerReplace;
            bool deepDesyncActive = DeepDesyncEnabled || call.DeepDesyncRequested;
            if (executionMode == ExecutionMode.ServerPatch || deepDesyncActive || requirePatchForFanOut)
            {
                patchRoot = new PatchNode(-1);
                MetaContext.PatchWrapper = CreatePatchWrapper(patchRoot);
            }

            // Begin recording for replay
            MetaContext.BeginOperation();

            // Dispatch the call
            var result = await DispatchCall(call.ServiceName, call.MethodName, call.Payload, call.MethodVersion);

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

            // Build response (canonical MetaOperation payload — same shape used on both
            // the RPC response side and the EntityBroadcast side after the 0.24 unification).
            var response = new HandleCallResult
            {
                Response = new MetaOperation
                {
                    ServiceName = call.ServiceName,
                    MethodName = call.MethodName,
                    MethodVersion = call.MethodVersion,
                    Payload = call.Payload,
                    ResultBytes = result.ResultBytes,
                    ReplayPayload = replayPayload,
                    Error = null,
                    RandomScrollDelta = randomScrollDelta,
                    NamedRandomScrollDeltas = namedRandomScrollDeltas,
                    PatchBytes = patchBytes,
                    DeepDesyncCrc = deepDesyncCrc,
                    ServerTimeTicks = call.ServerTimeTicks,
                    // The caller's optimistic replay materializes the same config branch the
                    // server used here, decoupling replay from session-resolved versions —
                    // required for Global scope and mid-rollout cases.
                    ExecutedConfigVersion = MetaContext.ConfigVersion
                },
                Broadcasts = new List<EntityBroadcast>(),
                CrossEntityCalls = crossEntityCalls,
                ForcePersist = result.ForcePersist
            };

            // Create broadcast for this call (to other subscribers). The broadcast carries
            // the same MetaOperation shape as the response — only the routing/exclusion
            // fields live on the outer wrapper. CallerId is set so subscribers' broadcast
            // replay path can attribute the operation to the right originator (Context.CallerId
            // flows from this field on the wire).
            var mainBroadcastOp = new MetaOperation
            {
                ServiceName = call.ServiceName,
                MethodName = call.MethodName,
                MethodVersion = call.MethodVersion,
                Payload = call.Payload,
                CallerId = call.CallerId,
                ReplayPayload = replayPayload,
                ServerTimeTicks = call.ServerTimeTicks,
                RandomScrollDelta = randomScrollDelta,
                NamedRandomScrollDeltas = namedRandomScrollDeltas,
                PatchBytes = patchBytes,
                // Observers replay this broadcast under the same config the server used —
                // not their own session-resolved version. Critical for Global scope and for
                // mid-session config rollouts that haven't reached all observers yet.
                ExecutedConfigVersion = MetaContext.ConfigVersion
            };
            var mainBroadcast = new EntityBroadcast
            {
                ExcludePlayerId = call.CallerId, // Don't broadcast back to caller
                Op = mainBroadcastOp,
            };

            // Handle triggers if any — nest inside main broadcast's MetaOperation.Triggers list.
            if (result.TriggersToExecute is { Count: > 0 } triggers)
            {
                mainBroadcastOp.Triggers = new List<MetaOperation>();
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
                    // Triggers always dispatch via the legacy/unversioned route (methodVersion=0).
                    // They're internal hop-overs, not external calls — there is no client-side
                    // [MetaMethod(Version)] choice in play, so the dispatcher resolves them to
                    // the lowest-versioned trigger implementation under the alias.
                    var triggerResult = await DispatchCall(call.ServiceName, triggerMethod, [], 0);
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

                    mainBroadcastOp.Triggers.Add(new MetaOperation
                    {
                        ServiceName = call.ServiceName,
                        MethodName = triggerMethod,
                        MethodVersion = 0, // triggers always run under the legacy alias
                        Payload = [], // Triggers have no arguments
                        CallerId = call.CallerId, // triggers attribute to the outer call's originator
                        ReplayPayload = triggerReplay,
                        ServerTimeTicks = call.ServerTimeTicks,
                        RandomScrollDelta = triggerScrollDelta,
                        NamedRandomScrollDeltas = triggerNamedDeltas,
                        PatchBytes = triggerPatchBytes,
                        // Triggers run under the same MetaContext as the outer call — same config.
                        ExecutedConfigVersion = MetaContext.ConfigVersion
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
                mainBroadcastOp.StateBytes = stateBytes;
                mainBroadcastOp.ReplayPayload = null;
            }

            response.Broadcasts.Add(mainBroadcast);

            return response;
        }
        catch (Exception ex)
        {
            Logger.ProviderCallError(ex, call.ServiceName, call.MethodName);
            return new HandleCallResult { Response = new MetaOperation { Error = ex.Message } };
        }
        finally
        {
            MetaContextAccessor.Current = null;
        }
    }

    /// <summary>
    /// Self-call dispatcher: a cross-entity call whose target resolves to this grain runs
    /// here instead of through Orleans (which would deadlock — EntityGrain is non-reentrant).
    /// The nested op gets its own replay buffer and <c>CrossEntityCalls</c> list but shares
    /// state, randoms, and the outer's <c>PatchWrapper</c> so the patch tree stays one-per-RPC
    /// from the client's perspective. Returns a <see cref="CrossEntityCallInfo"/> shaped
    /// identically to the real cross-entity path so replay sees no difference.
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
            // Sibling/cross-entity nested call. Routes via the legacy/unversioned method-version
            // bucket — internal hop, not a client call carrying a specific [MetaMethod(Version)].
            result = await DispatchCall(serviceName, methodName, argsBytes, 0);
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

    public async ValueTask<HandleEventResult> HandleExternalEventAsync(
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
                ExcludePlayerId = null, // Broadcast to everyone
                Op = new MetaOperation
                {
                    ServiceName = subscriberInterface,
                    MethodName = methodName,
                    Payload = eventData,
                    ReplayPayload = replayPayload,
                    ServerTimeTicks = MetaContext.ServerTimeTicks,
                    ExecutedConfigVersion = MetaContext.ConfigVersion
                }
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

    // Per-method validation (is-a-query-method, IsClientCallable, IsOpenAccessQuery +
    // access-policy ladder) is emitted by the generator as a HandleQueryAsync override that
    // runs an inline switch before delegating to base. Generated only when the project has
    // at least one query method; base behaviour without an override rejects all calls since
    // there's nothing to dispatch.
    public virtual async ValueTask<QueryCallResponse> HandleQueryAsync(RpcCall call)
    {
        if (MetaContext == null || Context == null)
            return new QueryCallResponse { Error = "Provider not initialized" };

        bool skipMigration = ShouldSkipMigration(call.ServiceName, call.MethodName);
        int? schemaCap = null;
        // Pin locks schema for Private/Shared with an active pin; Global substitutes
        // CurrentClientVersion as the migration driver instead of the caller's own version.
        bool pinLocksMigration = ActiveConfigPins.Count > 0 && Scope != EntityScope.Global;
        if (!skipMigration && !pinLocksMigration)
        {
            var methodCap = GetMethodMinStateVersion(call.ServiceName, call.MethodName);
            var migrationDriver = Scope == EntityScope.Global ? ResolveMigrationDriverForGlobal(call.CallerClientVersion) : call.CallerClientVersion;
            var clientCap = ComputeSchemaCapForClient(migrationDriver);
            schemaCap = methodCap.HasValue && clientCap.HasValue
                ? System.Math.Min(methodCap.Value, clientCap.Value)
                : (methodCap ?? clientCap);
        }

        // Queries don't go through HandleCallAsync, so the lazy-migration check is duplicated
        // here. EntityGrain doesn't see LazyMigrationCompleted on the query path, so we
        // persist inline via SaveStateHandler when migration ran.
        if (!skipMigration && !pinLocksMigration)
        {
            var queryDriver = Scope == EntityScope.Global ? ResolveMigrationDriverForGlobal(call.CallerClientVersion) : call.CallerClientVersion;
            if (await CheckAndRunLazyMigrationAsync(queryDriver, schemaCap) && SaveStateHandler != null)
                await SaveStateHandler();
        }

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
            MetaContext.IsClientCall = true; // queries always come from clients
            MetaContextAccessor.Current = MetaContext;

            // Dispatch the call — same dispatcher, but no replay/random/broadcast machinery
            var result = await DispatchCall(call.ServiceName, call.MethodName, call.Payload, call.MethodVersion);

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

    // Per-method classification (IsQueryMethod / IsSignalMethod / IsOpenAccessQuery /
    // IsClientCallable) deliberately lives inline in the generator-emitted Handle*Async
    // overrides, not as virtual lookups here — each switch contains exactly the methods it
    // needs to gate, and projects without those gate flavours emit zero validation code.

    /// <summary>
    /// Handle a signal call. Dispatches the method through <see cref="DispatchSignal"/> but skips
    /// replay recording, broadcasts, random state tracking, persistence, and response generation.
    /// Bridges called from inside the signal body are wrapped by their normal Recorder, but the
    /// Recorder writes into <see cref="NullServerRecordContext"/> — real side-effects still happen,
    /// recording is a no-op.
    /// Errors are caught and logged — they do not propagate back to the client (fire-and-forget).
    /// </summary>
    // Per-method validation (is-a-signal-method, IsClientCallable, access-policy ladder) is
    // emitted by the generator as a HandleSignalAsync override that runs before base.
    // Generated only when the project has at least one signal method.
    public virtual async ValueTask HandleSignalAsync(RpcCall call)
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
            MetaContext.IsClientCall = true; // signals always come from clients
            // Enable signal mode so any bridge Recorder's Writer.Write becomes a no-op
            // (the payload produced during signal execution has no consumer).
            MetaContext.SignalMode = true;
            MetaContextAccessor.Current = MetaContext;

            await DispatchSignal(call.ServiceName, call.MethodName, call.Payload, call.MethodVersion);
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
    /// Records the version. Generated providers additionally materialize
    /// <see cref="MetaContext{TState}.Config"/> from <see cref="IMetaConfigProvider{TConfig}"/>.
    /// Providers that need async materialization should override
    /// <see cref="InitializeConfigAsync"/> instead.
    /// </summary>
    public virtual void InitializeConfig(MetaConfigVersion version)
    {
        ConfigVersion = version;
        if (MetaContext != null) MetaContext.ConfigVersion = version;
    }

    /// <summary>
    /// Async variant of <see cref="InitializeConfig"/> — preferred entry point on grain
    /// activation when the registered <see cref="IMetaConfigProvider{TConfig}"/> may need
    /// to fetch bytes across an async boundary (e.g. <c>BroadcastingConfigProvider</c>
    /// pulling from the per-version <c>IConfigStoreGrain</c>). Default impl delegates to
    /// the sync <see cref="InitializeConfig"/> for back-compat with providers that have
    /// only synchronous config materialization.
    /// <para>
    /// Generated providers with a registered <see cref="IMetaConfigProvider{TConfig}"/>
    /// override this and call <see cref="IMetaConfigProvider{TConfig}.GetConfigAsync"/> —
    /// avoiding sync-over-async on the grain activation path. EntityGrain awaits this
    /// before the per-entity compat gate.
    /// </para>
    /// </summary>
    public virtual Task InitializeConfigAsync(MetaConfigVersion version)
    {
        InitializeConfig(version);
        return Task.CompletedTask;
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

    /// <summary>
    /// Scope-aware effective config version the framework will dispatch under for a given
    /// subscriber. Private/Shared: pin's version when pinned, else the joiner's resolved
    /// version (becomes the pin on first subscribe). Global: <see cref="IConfigVersionResolver.CurrentClientVersion"/>
    /// resolution, ignoring the joiner's own version. Populated on the SubscribeResponse so
    /// the client materializes the same config the server will use.
    /// </summary>
    public virtual MetaConfigVersion ResolveEffectiveConfigVersion(string? clientVersion)
        => ResolveClientConfigVersion(clientVersion);

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
