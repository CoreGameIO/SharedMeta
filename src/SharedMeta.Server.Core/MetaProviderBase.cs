using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Memory;
using SharedMeta.Core.Network;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Patch;
using SharedMeta.Core.Random;
using SharedMeta.Core.Transport;
using SharedMeta.Server;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Memory;

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
    // 0.24.0+ Keyed by ushort methodId — generated overrides log with the wire id only,
    // no name plumbing on the failure path.
    protected void LogProviderCallError(Exception ex, ushort methodId)
        => Logger.ProviderCallError(ex, methodId);

    private MetaRandom _serverRandom = null!;
    private MetaRandom _optimisticRandom = null!;
    private MetaRandom[] _namedRandoms = System.Array.Empty<MetaRandom>();
    private IMetaRandom[]? _namedRandomsView;

    // ── Pooled MetaOperation instances ─────────────────────────────────────────
    // Reused across HandleCallAsync invocations so MetaOperation does not allocate
    // per RPC. The instances live for the grain's lifetime; serialized bytes are
    // the only thing that leaves the grain. Triggers use a growing list of pooled
    // MetaOperation instances — high-water-mark across a session's worst-case
    // trigger fan-out.
    private readonly MetaOperation _pooledResponseOp = new();
    private readonly MetaOperation _pooledBroadcastOp = new();
    private readonly List<MetaOperation> _triggerOpPool = new();
    private readonly List<MetaOperation> _pooledTriggerSlice = new();   // reused holder

    private static void ResetMetaOperation(MetaOperation op)
    {
        op.MethodId = 0;
        op.Payload = null;
        op.CallerId = null;
        op.ResultBytes = null;
        op.ReplayPayload = null;
        op.PatchBytes = null;
        op.StateBytes = null;
        op.RandomScrollDelta = 0;
        op.NamedRandomScrollDeltas = null;
        op.ServerTimeTicks = 0;
        op.ExecutedConfigVersion = default;
        op.DeepDesyncCrc = null;
        op.Error = null;
        op.Debug = null;
        op.Triggers = null;
    }

    private MetaOperation RentTriggerOp(int index)
    {
        while (_triggerOpPool.Count <= index)
            _triggerOpPool.Add(new MetaOperation());
        var op = _triggerOpPool[index];
        ResetMetaOperation(op);
        return op;
    }

    // Terminal-output serialization for EntityCallResult.OpBytes / EntityBroadcast.OpBytes —
    // crosses the Orleans grain boundary, so call PackForExternalUsage which encodes "this
    // result outlives the current grain method" as an explicit method choice (no scratch).
    private static ReadOnlyMemory<byte> PackBytes(IMetaSerializer serializer, MetaOperation op)
    {
        return serializer.PackForExternalUsage(op);
    }

    /// <summary>
    /// Serialize <paramref name="op"/> into a pool-rented buffer when the silo-scoped
    /// <see cref="PooledPayloadRegistry"/> is wired; otherwise serialize via the byte[]
    /// fallback and wrap the result as a Ref=0 <see cref="PooledPayload"/> so callers can
    /// treat both paths uniformly (Ref=0 means "no slot to release"; GC reclaims the byte[]).
    /// Returns <c>(Bytes, Owned)</c>: <c>Bytes</c> is <c>Owned.Memory</c>, exposed separately
    /// so call sites that only need the ROM (state-bytes carrier, error-response) don't have
    /// to peek into the struct.
    /// </summary>
    private (ReadOnlyMemory<byte> Bytes, PooledPayload Owned) PackBroadcastVariant(MetaOperation op)
    {
        var registry = Context.Registry;
        // PooledPayloadOptions.UsePoolPath gates whether outgoing serialization rents a pool
        // slot (ref-counted fan-out) or allocates a fresh byte[] (Ref=0, GC-managed). Default
        // OFF; hosts opt in via services.Configure<PooledPayloadOptions>(o => o.UsePoolPath = true).
        if (registry?.IsEnabled == true)
        {
            var owned = Context.Serializer.PackPooled(op, registry);
            return (owned.Memory, owned);
        }
        var bytes = PackBytes(Context.Serializer, op);
        return (bytes, new PooledPayload(bytes, 0));
    }

    /// <summary>
    /// Service resolver for dependency injection (e.g., IRandomService).
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<Type, object>? ServiceResolver { get; set; }

    /// <summary>
    /// Handler for cross-entity calls (when a service calls another entity).
    /// Returns CrossEntityCallInfo with EntitySequenceNumber and ResultBytes.
    /// The last <c>ushort</c> argument is the server-side global <c>MethodId</c> stamped
    /// by the recorder (<c>0</c> = legacy / unknown, the target grain resolves from strings).
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<string, ushort, ReadOnlyMemory<byte>, long, Task<CrossEntityOperationInfo>>? EntityCallHandler { get; set; }

    // Fire-and-forget cross-entity dispatch for [MetaMethod(OneWay = true)] — routes through
    // Orleans [OneWay] so the source doesn't wait for a reply envelope. Same MethodId
    // semantics as EntityCallHandler. Caller is responsible for ensuring the underlying byte
    // storage outlives the target's wire-serialization (see CallEntityOneWay docstring).
    public Action<string, ushort, ReadOnlyMemory<byte>, long>? EntityCallOneWayHandler { get; set; }

    /// <summary>
    /// Handler for read-only cross-entity state access.
    /// Returns serialized state bytes of the target entity, or null if not found.
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<string, string, Task<byte[]?>>? EntityStateHandler { get; set; }

    /// <summary>
    /// 0.24.0+ Server-side signature for reverse <c>MethodId → ServerMethodEntry</c> lookup.
    /// Wired by <c>EntityGrain</c> from DI. Provider uses this to populate
    /// <c>MetaOperation.ServiceName/MethodName/MethodVersion</c> on the outgoing response/broadcast
    /// (those strings are still on the wire for clients that haven't migrated) and to surface
    /// service/method names in logs and migration policy lookups now that <c>RpcCall</c> only
    /// carries the ushort id.
    /// </summary>
    public SharedMeta.Core.Transport.MetaServerSignature? ServerSignature { get; set; }

    /// <summary>
    /// Resolve a server-side MethodId to <c>(ServiceName, Alias, Version)</c>. Returns
    /// empty strings when no signature is wired — callers must tolerate that, since
    /// pre-0.24 paths still pass MethodId=0 / no-signature builds.
    /// </summary>
    protected (string ServiceName, string MethodName, int MethodVersion) ResolveMethodNames(ushort methodId)
    {
        var entry = ServerSignature?.GetMethodEntry(methodId);
        return entry == null
            ? ("", "", 0)
            : (entry.ServiceName, entry.Alias, entry.Version);
    }

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

    // ── Named random scroll-delta pool ────────────────────────────────────
    // Both the capture-snapshot (before-image) and the compute-deltas result are
    // exactly _namedRandoms.Length longs. We pool them via a single Stack so both
    // are reclaimed and reused across calls. Snapshots are short-lived (Capture →
    // Compute) and returned synchronously inside ComputeNamedScrollDeltas; deltas
    // are long-lived (referenced from MetaOperation.NamedRandomScrollDeltas through
    // serialization) and stashed in _pendingDeltasReturns, flushed at the next
    // HandleCallAsync entry once the wire snapshot has been emitted.
    private readonly Stack<long[]> _namedScrollPool = new();
    private readonly List<long[]> _pendingDeltasReturns = new();

    private long[] RentNamedScrollArray()
    {
        return _namedScrollPool.TryPop(out var arr) ? arr : new long[_namedRandoms.Length];
    }

    private void ReturnNamedScrollArray(long[] arr)
    {
        if (arr.Length == _namedRandoms.Length) _namedScrollPool.Push(arr);
    }

    /// <summary>
    /// Return any scroll-delta arrays referenced by the previous call to the snapshot pool.
    /// Called at HandleCallAsync entry — by that point the previous call's wire frame has
    /// been serialized and shipped; the underlying arrays are no longer observed.
    /// </summary>
    private void FlushPendingNamedScrollReturns()
    {
        for (int i = 0; i < _pendingDeltasReturns.Count; i++)
            ReturnNamedScrollArray(_pendingDeltasReturns[i]);
        _pendingDeltasReturns.Clear();
    }

    // ── Intermediate scratch buffer ───────────────────────────────────────
    // Single growable byte[] used for ALL intermediate serializations within one
    // Handle*Async invocation (recorder replay, patch tree, state snapshot, per-trigger
    // replay/patch). Lifetime is provider/grain — the buffer is allocated once, grown
    // on-demand, and reused across calls. PooledPayloadRegistry is NOT involved: it stays
    // reserved exclusively for outgoing payloads that cross the grain boundary.
    //
    // Growth strategy: when a writer doesn't fit, it allocates a new byte[] and copies its
    // already-written bytes there; the pool's Buffer field is repointed. The OLD byte[]
    // is NOT explicitly freed — it stays alive through outstanding ROM references handed
    // out by earlier writers in the same call (GC reclaims it once those references fall
    // out of scope, typically at the next call's reset when the embedding response/
    // broadcast has been packed and shipped).
    private readonly ScratchBufferPool _scratchPool = new();
    // Single reusable writer for all intermediate slices within a grain call (replay tape,
    // patch tree, state snapshot, per-trigger replay/patch). Each call site does
    // _intermediateWriter.Reset() to re-arm onto the current pool tail; the previously
    // captured WrittenMemory snapshots stay valid (they're (array,start,length) value tuples).
    // Zero allocation per slice — single writer instance lives for the grain's lifetime.
    private readonly ScratchBufferWriter _intermediateWriter;

    protected MetaProviderBase()
    {
        _intermediateWriter = new ScratchBufferWriter(_scratchPool);
    }

    // ── Outgoing pool tokens ──────────────────────────────────────────────
    // Filled by serialization paths inside HandleCallAsync / HandleExternalEventAsync; taken
    // by EntityGrain via TakeOutgoing* before wrapping into PooledPayload-typed wire fields
    // (EntityCallResult.OpBytes / EntityBroadcast.OpBytes etc.).
    // <para>
    // The sender (us) does NOT release outgoing payloads — the receiving grain / wire pipeline
    // owns the buffer once the PooledPayload crosses the boundary, and Release fires there.
    // Untaken tokens (e.g. fan-out had zero subscribers, or a code path forgot to Take) are
    // released as a safety net at the next provider entry by <see cref="FlushPendingOutgoing"/>.
    // </para>
    private PooledPayload _outgoingResponse;
    private PooledPayload _outgoingResult;
    private PooledPayload _outgoingBroadcastReplay;
    private PooledPayload _outgoingBroadcastPatch;
    private PooledPayload _outgoingEventBroadcast;

    public PooledPayload TakeOutgoingResponse()
    {
        var p = _outgoingResponse;
        _outgoingResponse = default;
        return p;
    }

    public PooledPayload TakeOutgoingResult()
    {
        var p = _outgoingResult;
        _outgoingResult = default;
        return p;
    }

    public PooledPayload TakeOutgoingBroadcastReplay()
    {
        var p = _outgoingBroadcastReplay;
        _outgoingBroadcastReplay = default;
        return p;
    }

    public PooledPayload TakeOutgoingBroadcastPatch()
    {
        var p = _outgoingBroadcastPatch;
        _outgoingBroadcastPatch = default;
        return p;
    }

    public PooledPayload TakeOutgoingEventBroadcast()
    {
        var p = _outgoingEventBroadcast;
        _outgoingEventBroadcast = default;
        return p;
    }

    private void FlushPendingOutgoing()
    {
        var registry = Context?.Registry;
        if (registry == null) return;
        // Ref==0 is the byte[]-fallback wrapper — no slot to release, GC reclaims naturally.
        // Only pool-backed (Ref!=0) tokens need explicit Release here.
        if (_outgoingResponse.Ref != 0) registry.Release(_outgoingResponse);
        if (_outgoingResult.Ref != 0) registry.Release(_outgoingResult);
        if (_outgoingBroadcastReplay.Ref != 0) registry.Release(_outgoingBroadcastReplay);
        if (_outgoingBroadcastPatch.Ref != 0) registry.Release(_outgoingBroadcastPatch);
        if (_outgoingEventBroadcast.Ref != 0) registry.Release(_outgoingEventBroadcast);
        _outgoingResponse = default;
        _outgoingResult = default;
        _outgoingBroadcastReplay = default;
        _outgoingBroadcastPatch = default;
        _outgoingEventBroadcast = default;
    }

    /// <summary>
    /// Snapshot current ScrollId of each named random. Returns null if no named randoms declared
    /// so callers can skip the work entirely.
    /// </summary>
    private long[]? CaptureNamedScrolls()
    {
        if (_namedRandoms.Length == 0) return null;
        var snap = RentNamedScrollArray();
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
            if (deltas == null)
            {
                deltas = RentNamedScrollArray();
                // Pool arrays may carry stale data from a prior tenant — zero out so unused
                // indices don't surface as fake non-zero deltas in the wire frame.
                System.Array.Clear(deltas);
            }
            deltas[i] = d;
        }
        // Snapshot is consumed — pool it for the next Capture.
        ReturnNamedScrollArray(before);
        // Stash the deltas so the next HandleCallAsync entry reclaims it after the wire
        // snapshot has shipped (MetaOperation.NamedRandomScrollDeltas still references it
        // through serialization).
        if (deltas != null) _pendingDeltasReturns.Add(deltas);
        return deltas;
    }


    /// <summary>
    /// Override to perform additional initialization after context and state are set.
    /// </summary>
    protected virtual void OnInitialize() { }

    /// <summary>
    /// Dispatches a service method call by global method id. Implemented by generated code
    /// as a jump table on <see cref="ServerMethodEntry.GlobalIndex"/> (via
    /// <c>SharedMeta.Generated.GameMethodIds</c> constants). The version is encoded in
    /// the id, so no separate version parameter is needed.
    /// </summary>
    protected abstract ValueTask<DispatchResult> DispatchCall(ushort methodId, ReadOnlyMemory<byte> payload);

    /// <summary>
    /// Resolves a sibling-service impl by interface type. Default returns null; generated
    /// providers override with a switch over services hosted on this entity's TState so
    /// self-targeted cross-entity calls dispatch directly without an Orleans hop.
    /// </summary>
    public virtual object? ResolveSiblingByType(System.Type interfaceType) => null;

    /// <summary>
    /// Dispatches a signal method call (void, fire-and-forget). Default no-op; generated
    /// code overrides with a <c>switch (methodId)</c> against <c>GameMethodIds</c> for
    /// providers that have <c>[MetaMethod(Mode = ExecutionMode.Signal)]</c> methods,
    /// routing them through <c>{Service}SignalDispatcher.Dispatch</c>.
    /// 0.24.0+ Keyed by client-local <c>MethodId</c> — no string-name plumbing.
    /// </summary>
    protected virtual Task DispatchSignal(ushort methodId, ReadOnlyMemory<byte> payload)
        => Task.CompletedTask;

    /// <summary>
    /// Dispatch an external event. Override in derived class if needed.
    /// 0.24.0+ dispatches by <see cref="SharedMeta.Core.Framework.FrameworkMethodIds"/> ushort
    /// constants rather than <c>(subscriberInterface, methodName)</c> string pair.
    /// </summary>
    protected virtual ValueTask<DispatchResult> DispatchEvent(ushort methodId, ReadOnlyMemory<byte> eventData)
    {
        return new ValueTask<DispatchResult>(new DispatchResult { ResultBytes = default, TriggersToExecute = null });
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
    /// <c>[NoMigrate]</c>. Generated providers override with a <c>switch (methodId)</c>
    /// against <c>GameMethodIds</c> constants; default is no skips.
    /// 0.24.0+ Keyed by client-local <c>MethodId</c> — no string-name plumbing on the hot path.
    /// </summary>
    protected virtual bool ShouldSkipMigration(ushort methodId) => false;

    /// <summary>
    /// Per-method migration policy: returns the schema cap declared via
    /// <c>[MinStateVersion(N)]</c>, or null when uncapped. Generated providers override with
    /// a <c>switch (methodId)</c> against <c>GameMethodIds</c> constants.
    /// 0.24.0+ Keyed by client-local <c>MethodId</c>.
    /// </summary>
    protected virtual int? GetMethodMinStateVersion(ushort methodId) => null;

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
    public virtual async ValueTask<HandleCallResult> HandleCallAsync(RpcCall call, bool isClientOriginated = true, bool requirePatchForFanOut = false, long entitySequenceNumber = 0)
    {
        if (MetaContext == null || Context == null)
        {
            return new HandleCallResult { Error = "Provider not initialized" };
        }

        // Per-call migration policy. Two caps stack:
        //   [NoMigrate]            → skip lazy migration AND pin config to schema-floor branch.
        //   [MinStateVersion(N)]   → migrate only up to N (no further).
        //   ComputeSchemaCapForClient → migrate only up to caller's resolved config branch
        //                                (so a 1.x client doesn't trigger a 2.0 migration on
        //                                 a fresh entity it just activated).
        // Effective cap = min of the two non-null caps. Per-caller config pin then runs
        // through GetCachedConfigForClient.
        // 0.24.0+ Migration-policy virtuals are keyed by the ushort MethodId. Generated
        // providers emit a switch (methodId) against GameMethodIds constants — no
        // string-name resolution on the hot path.
        bool skipMigration = ShouldSkipMigration(call.MethodId);
        int? schemaCap = null;
        // Private/Shared with an active pin lock schema to subscribe-time driver: a
        // cross-entity call from a higher-version client can't advance schema. Global
        // substitutes the resolver's CurrentClientVersion as the migration driver.
        bool pinLocksMigration = ActiveConfigPins.Count > 0 && Scope != EntityScope.Global;
        if (!skipMigration && !pinLocksMigration)
        {
            var methodCap = GetMethodMinStateVersion(call.MethodId);
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

            // Reclaim arrays referenced by the previous call: named-scroll snapshots/deltas,
            // any pool-rented replay/patch buffers stashed mid-dispatch, and any outgoing
            // pool tokens that the previous call's caller forgot to take (safety net — should
            // be empty in normal flow because EntityGrain Takes everything before returning).
            FlushPendingNamedScrollReturns();
            FlushPendingOutgoing();

            // Rewind the intermediate scratch buffer for this call. Any ROMs the PREVIOUS
            // call handed out (replay/patch/state/triggers) have already been embedded into
            // the outgoing PooledPayload during PackBroadcastVariant and shipped — they
            // mustn't be re-read after this point.
            _scratchPool.Reset();
            // Same for the per-grain GrainScopedSerializer's pool used by implicit Pack(T)
            // calls (dispatcher result, patchable setters going through Context.Serializer).
            (Context.Serializer as Memory.IServerMetaSerializer)?.ResetScratch();

            // Capture optimistic random scroll position before dispatch
            var scrollIdBefore = _optimisticRandom.ScrollId;
            var namedScrollsBefore = CaptureNamedScrolls();

            // Determine server-side execution mode. 0.24.0+ keyed by methodId — no name plumbing.
            var executionMode = ExecutionModeProvider?.GetMode(
                call.MethodId, ExecutionMode.Optimistic) ?? ExecutionMode.Optimistic;

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

            // Dispatch the call. The generated dispatcher emits serializer.Pack(result) via
            // Context.Serializer (GrainScopedSerializer) — result.ResultBytes is a ROM slice
            // over the per-grain scratch buffer, valid until the next Handle*Async entry.
            var result = await DispatchCall(call.MethodId, call.Payload);

            // For cross-entity calls, the inner result IS the outgoing payload (the source
            // grain hands it back via CrossEntityCallReturn.ResultBytes). The dispatcher's
            // result bytes are now ROM-over-scratch (GrainScopedSerializer.Pack), which the
            // next HandleCallAsync entry would invalidate via _scratchPool.Reset(). For
            // in-silo Orleans hops PooledPayload is [Immutable] and skipped during deep-copy
            // → the receiver would read stale bytes. .ToArray() here puts the result on a
            // stable byte[] that outlives the source grain's next call.
            if (!isClientOriginated)
                _outgoingResult = new PooledPayload(result.ResultBytes.ToArray(), 0);

            // End recording. EndOperation returns ROM over the recorder writer's internal
            // pool buffer — valid only until the NEXT BeginOperation on this context (which
            // happens per-trigger inside the loop below). Copy into the per-grain scratch
            // pool so the snapshot survives until the call's outgoing ops are packed.
            var replayRom = MetaContext.EndOperation();
            ReadOnlyMemory<byte> replayPayload;
            if (!replayRom.IsEmpty)
            {
                _intermediateWriter.Reset(); var w = _intermediateWriter;
                var span = w.GetSpan(replayRom.Length);
                replayRom.Span.CopyTo(span);
                w.Advance(replayRom.Length);
                replayPayload = w.WrittenMemory;
            }
            else
            {
                replayPayload = default;
            }

            // Collect patch bytes if ServerPatch mode was active. Scratch-backed writer —
            // content is copied into the response/broadcast pool slot during
            // PackBroadcastVariant below, scratch is reset at the next call entry.
            ReadOnlyMemory<byte> patchBytes = default;
            if (patchRoot != null)
            {
                patchRoot.Prune();
                if (patchRoot.HasChanges)
                {
                    _intermediateWriter.Reset();
                    Context.Serializer.Pack(patchRoot, _intermediateWriter);
                    patchBytes = _intermediateWriter.WrittenMemory;
                }
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

            // Populate pooled response — the originator's RPC reply shape. Carries both
            // ReplayPayload and PatchBytes when both are available; the client picks the
            // applicable form based on its own execution mode for this method.
            ResetMetaOperation(_pooledResponseOp);
            _pooledResponseOp.MethodId = call.MethodId;
            _pooledResponseOp.Payload = call.Payload;
            _pooledResponseOp.ResultBytes = result.ResultBytes;
            _pooledResponseOp.ReplayPayload = replayPayload;
            _pooledResponseOp.PatchBytes = patchBytes;
            _pooledResponseOp.RandomScrollDelta = randomScrollDelta;
            _pooledResponseOp.NamedRandomScrollDeltas = namedRandomScrollDeltas;
            _pooledResponseOp.DeepDesyncCrc = deepDesyncCrc;
            _pooledResponseOp.ServerTimeTicks = call.ServerTimeTicks;
            // Debug field intentionally left null — mirroring entity-seq + originating caller
            // on every wire packet was a per-RPC string allocation regardless of debug state.
            _pooledResponseOp.Debug = null;
            // Caller's optimistic replay materializes the same config branch the server used —
            // decoupling replay from session-resolved versions (Global scope, mid-rollout).
            _pooledResponseOp.ExecutedConfigVersion = MetaContext.ConfigVersion;

            // Populate pooled broadcast — the form sent to OTHER subscribers. CallerId is
            // set so subscribers can attribute the op to the originator on their side.
            // Cross-entity broadcasts (isClientOriginated=false) NULL the CallerId so the
            // outer caller's client doesn't filter them as own-RPC echoes — the outer caller
            // didn't directly RPC THIS entity, just transitively touched it through another
            // entity's method. Direct subscribers of THIS entity still get the broadcast;
            // they just lose cross-entity attribution (the broadcast wasn't directly theirs).
            ResetMetaOperation(_pooledBroadcastOp);
            _pooledBroadcastOp.MethodId = call.MethodId;
            _pooledBroadcastOp.Payload = call.Payload;
            _pooledBroadcastOp.CallerId = isClientOriginated ? call.CallerId : null;
            _pooledBroadcastOp.ReplayPayload = replayPayload;
            _pooledBroadcastOp.PatchBytes = patchBytes;
            _pooledBroadcastOp.RandomScrollDelta = randomScrollDelta;
            _pooledBroadcastOp.NamedRandomScrollDeltas = namedRandomScrollDeltas;
            _pooledBroadcastOp.ServerTimeTicks = call.ServerTimeTicks;
            // Same as response above — Debug intentionally null to avoid per-RPC string allocation.
            _pooledBroadcastOp.Debug = null;
            _pooledBroadcastOp.ExecutedConfigVersion = MetaContext.ConfigVersion;

            // Handle triggers — populate pooled trigger ops, attach to broadcast's Triggers list.
            // 0.24.0+ TriggersToExecute carries client-local MethodIds directly (emitted by the
            // dispatcher as GameMethodIds constants). No runtime alias-to-id lookup or string
            // plumbing — DispatchCall takes the id straight through.
            if (result.TriggersToExecute is { Count: > 0 } triggers)
            {
                _pooledTriggerSlice.Clear();
                int triggerIndex = 0;
                foreach (var triggerMethodId in triggers)
                {
                    var triggerScrollBefore = _optimisticRandom.ScrollId;
                    var triggerNamedScrollsBefore = CaptureNamedScrolls();

                    PatchNode? triggerPatchRoot = null;
                    if (executionMode == ExecutionMode.ServerPatch)
                    {
                        triggerPatchRoot = new PatchNode(-1);
                        MetaContext.PatchWrapper = CreatePatchWrapper(triggerPatchRoot);
                    }

                    MetaContext.BeginOperation();
                    var triggerResult = await DispatchCall(triggerMethodId, default(ReadOnlyMemory<byte>));
                    // triggerResult.ResultBytes is byte[]-backed (dispatcher no longer pools).
                    // Embedded into the trigger op below; GC reclaims when the op resets.

                    // Per-trigger replay payload — copy recorder's ROM into the shared
                    // scratch pool so it survives the next BeginOperation (which would Reset
                    // the recorder's pool buffer).
                    var trigRom = MetaContext.EndOperation();
                    ReadOnlyMemory<byte> triggerReplay;
                    if (!trigRom.IsEmpty)
                    {
                        _intermediateWriter.Reset(); var w = _intermediateWriter;
                        var span = w.GetSpan(trigRom.Length);
                        trigRom.Span.CopyTo(span);
                        w.Advance(trigRom.Length);
                        triggerReplay = w.WrittenMemory;
                    }
                    else
                    {
                        triggerReplay = default;
                    }

                    ReadOnlyMemory<byte> triggerPatchBytes = default;
                    if (triggerPatchRoot != null)
                    {
                        triggerPatchRoot.Prune();
                        if (triggerPatchRoot.HasChanges)
                        {
                            _intermediateWriter.Reset(); var w = _intermediateWriter;
                            Context.Serializer.Pack(triggerPatchRoot, w);
                            triggerPatchBytes = w.WrittenMemory;
                        }
                        MetaContext.PatchWrapper = null;
                    }

                    var triggerScrollDelta = _optimisticRandom.ScrollId - triggerScrollBefore;
                    var triggerNamedDeltas = ComputeNamedScrollDeltas(triggerNamedScrollsBefore);

                    var triggerOp = RentTriggerOp(triggerIndex++);
                    triggerOp.MethodId = triggerMethodId;
                    triggerOp.Payload = System.Array.Empty<byte>();
                    triggerOp.CallerId = call.CallerId;
                    triggerOp.ReplayPayload = triggerReplay;
                    triggerOp.PatchBytes = triggerPatchBytes;
                    triggerOp.RandomScrollDelta = triggerScrollDelta;
                    triggerOp.NamedRandomScrollDeltas = triggerNamedDeltas;
                    triggerOp.ServerTimeTicks = call.ServerTimeTicks;
                    triggerOp.ExecutedConfigVersion = MetaContext.ConfigVersion;
                    _pooledTriggerSlice.Add(triggerOp);
                }
                // Both response and broadcast carry the SAME trigger list (same object reference
                // is OK pre-serialization — neither is mutated independently). Will be cleared
                // before next call via the broadcast op's reset.
                _pooledResponseOp.Triggers = _pooledTriggerSlice;
                _pooledBroadcastOp.Triggers = _pooledTriggerSlice;
            }

            // ServerReplace: serialize full state AFTER all triggers, capturing final state.
            // Strips ReplayPayload from both response and broadcast (state replaces it on the client).
            // State is intermediate (content is copied into response/broadcast pool slots during
            // PackBroadcastVariant below) — use the ArrayPool-backed writer, NOT the registry.
            ReadOnlyMemory<byte> stateBytes = default;
            if (isServerReplace)
            {
                if (State != null)
                {
                    _intermediateWriter.Reset();
                    Context.Serializer.Pack(State, _intermediateWriter);
                    stateBytes = _intermediateWriter.WrittenMemory;
                }
                _pooledResponseOp.StateBytes = stateBytes;
                _pooledResponseOp.ReplayPayload = default;
                _pooledBroadcastOp.StateBytes = stateBytes;
                _pooledBroadcastOp.ReplayPayload = default;
            }

            // Serialize response once — only when needed for the originating client's RPC reply.
            // Cross-entity callers (isClientOriginated=false) read just ResultBytes from the
            // slim CrossEntityCallReturn; the full ResponseBytes would be ignored, so skip it.
            // Pool path: owned slot rides out as EntityCallResult.OpBytes (PooledPayload).
            // Sender does not Release — the SessionManager / wire pipeline owns the buffer
            // once the result crosses the grain boundary.
            ReadOnlyMemory<byte> responseBytes = default;
            PooledPayload ownedResponse = default;
            if (isClientOriginated)
                (responseBytes, ownedResponse) = PackBroadcastVariant(_pooledResponseOp);

            // Serialize broadcast variants — high fan-out, this is where the pool win is.
            // Common case: one variant (replay-only). When requirePatchForFanOut is on we also
            // need a patch-only variant for the legacy subscriber population.
            ReadOnlyMemory<byte> broadcastReplayBytes = default;
            ReadOnlyMemory<byte> broadcastPatchBytes = default;
            PooledPayload ownedBroadcastReplay = default;
            PooledPayload ownedBroadcastPatch = default;

            // Variant 1: replay-eligible audience. Strip PatchBytes so the variant carries
            // only the replay payload (and state, for ServerReplace which already null'd replay).
            var origPatch = _pooledBroadcastOp.PatchBytes;
            _pooledBroadcastOp.PatchBytes = default;
            (broadcastReplayBytes, ownedBroadcastReplay) = PackBroadcastVariant(_pooledBroadcastOp);
            _pooledBroadcastOp.PatchBytes = origPatch;

            // Variant 2: patch-eligible audience. Only emit when force-patch tailoring is
            // requested OR the call executed under ServerPatch (then ALL subscribers get patch
            // and the replay variant is what we don't ship). For ServerPatch we still emit
            // both variants but the broadcast distributor will use the patch variant.
            if (requirePatchForFanOut && !origPatch.IsEmpty)
            {
                var origReplay = _pooledBroadcastOp.ReplayPayload;
                _pooledBroadcastOp.ReplayPayload = default;
                (broadcastPatchBytes, ownedBroadcastPatch) = PackBroadcastVariant(_pooledBroadcastOp);
                _pooledBroadcastOp.ReplayPayload = origReplay;
            }

            // Outgoing pool tokens — EntityGrain takes them via TakeOutgoing* and wraps into
            // PooledPayload-typed wire fields (EntityCallResult.OpBytes / EntityBroadcast.OpBytes).
            // Sender (us) does NOT release these; the receiving grain / wire pipeline owns the
            // buffer once it crosses the boundary. Any token left un-taken at the next provider
            // entry is released as a safety net via FlushPendingOutgoing.
            _outgoingResponse = ownedResponse;
            _outgoingBroadcastReplay = ownedBroadcastReplay;
            _outgoingBroadcastPatch = ownedBroadcastPatch;

            return new HandleCallResult
            {
                ResponseBytes = responseBytes,
                ResultBytes = result.ResultBytes,
                BroadcastReplayBytes = broadcastReplayBytes,
                BroadcastPatchBytes = broadcastPatchBytes,
                CrossEntityCalls = crossEntityCalls,
                ForcePersist = result.ForcePersist,
                Error = null,
            };
        }
        catch (Exception ex)
        {
            Logger.ProviderCallError(ex, call.MethodId);
            // Build an error-only response op so the wire payload is well-formed (client
            // dispatchers always Unpack OpBytes). Error path stays byte[]-backed (Ref=0):
            // hot path is the success branch, error allocations are not on a budget.
            ResetMetaOperation(_pooledResponseOp);
            _pooledResponseOp.MethodId = call.MethodId;
            _pooledResponseOp.Error = ex.Message;
            var errBytes = PackBytes(Context.Serializer, _pooledResponseOp);
            _outgoingResponse = new PooledPayload(errBytes, 0);
            return new HandleCallResult
            {
                ResponseBytes = errBytes,
                Error = ex.Message,
            };
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
    /// from the client's perspective. Returns a <see cref="CrossEntityOperationInfo"/> shaped
    /// identically to the real cross-entity path so replay sees no difference.
    /// </summary>
    public async Task<CrossEntityOperationInfo> HandleNestedCallAsync(string targetEntityId, ushort methodId, byte[] argsBytes)
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
            // Sibling nested call.
            result = await DispatchCall(methodId, argsBytes);
        }
        finally
        {
            // Always pop, even on exception, to keep outer recorder state coherent.
            MetaContext.PopNestedOperation(frame, out _);
        }

        return new CrossEntityOperationInfo {
            EntityId = targetEntityId,
            MethodId = methodId,
            ResultBytes = result.ResultBytes,
            EntitySequenceNumber = 0  // self-call shares outer's sequence; no separate seq increment
        };
    }

    public async ValueTask<HandleEventResult> HandleExternalEventAsync(
        ushort methodId,
        ReadOnlyMemory<byte> eventData,
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

            // Reclaim any pool buffers / outgoing tokens left over from the previous call.
            FlushPendingNamedScrollReturns();
            FlushPendingOutgoing();
            _scratchPool.Reset();
            (Context.Serializer as Memory.IServerMetaSerializer)?.ResetScratch();

            MetaContext.BeginOperation();
            var result = await DispatchEvent(methodId, eventData);
            // result.ResultBytes is byte[]-backed (dispatcher no longer pools). It's embedded
            // into the broadcast op below and GC reclaims after the op is packed.

            // Replay payload: copy recorder's ROM into per-grain scratch pool so it survives.
            var rom = MetaContext.EndOperation();
            ReadOnlyMemory<byte> replayPayload;
            if (!rom.IsEmpty)
            {
                _intermediateWriter.Reset(); var w = _intermediateWriter;
                var span = w.GetSpan(rom.Length);
                rom.Span.CopyTo(span);
                w.Advance(rom.Length);
                replayPayload = w.WrittenMemory;
            }
            else
            {
                replayPayload = default;
            }

            // Populate pooled broadcast for this event and serialize. Wire identifier is the
            // framework subscriber method id (high-range ushort from FrameworkMethodIds);
            // client-side dispatch keys on this. ServiceName/MethodName left empty — they're
            // diagnostic only and not used by per-method routing anymore.
            ResetMetaOperation(_pooledBroadcastOp);
            _pooledBroadcastOp.MethodId = methodId;
            _pooledBroadcastOp.Payload = eventData;
            _pooledBroadcastOp.ReplayPayload = replayPayload;
            _pooledBroadcastOp.ServerTimeTicks = MetaContext.ServerTimeTicks;
            _pooledBroadcastOp.ExecutedConfigVersion = MetaContext.ConfigVersion;

            // Pool path: owned slot rides out as EntityBroadcast.OpBytes (PooledPayload) and
            // receivers release. Falls back to byte[] when registry isn't wired.
            var (broadcastBytes, ownedEventBroadcast) = PackBroadcastVariant(_pooledBroadcastOp);
            _outgoingEventBroadcast = ownedEventBroadcast;
            return new HandleEventResult
            {
                BroadcastBytes = broadcastBytes,
            };
        }
        catch (Exception ex)
        {
            Logger.ProviderEventError(ex, "(framework)", "id=" + methodId);
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

        // 0.24.0+ Migration policy keyed by ushort MethodId — generated overrides switch on
        // GameMethodIds constants. No name plumbing on the hot path.
        bool skipMigration = ShouldSkipMigration(call.MethodId);
        int? schemaCap = null;
        // Pin locks schema for Private/Shared with an active pin; Global substitutes
        // CurrentClientVersion as the migration driver instead of the caller's own version.
        bool pinLocksMigration = ActiveConfigPins.Count > 0 && Scope != EntityScope.Global;
        if (!skipMigration && !pinLocksMigration)
        {
            var methodCap = GetMethodMinStateVersion(call.MethodId);
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

            // Reclaim any pool buffers / outgoing tokens left over from the previous call.
            FlushPendingNamedScrollReturns();
            FlushPendingOutgoing();
            _scratchPool.Reset();
            (Context.Serializer as Memory.IServerMetaSerializer)?.ResetScratch();

            // Dispatch the call — same dispatcher, but no replay/random/broadcast machinery.
            // result.ResultBytes is byte[]-backed (dispatcher no longer uses PooledPayload
            // for intermediate dispatcher results); the byte[] survives the EntityGrain →
            // SessionManager grain hop normally and reaches the client.
            var result = await DispatchCall(call.MethodId, call.Payload);

            return new QueryCallResponse
            {
                Success = true,
                ResultBytes = result.ResultBytes
            };
        }
        catch (Exception ex)
        {
            Logger.ProviderCallError(ex, call.MethodId);
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
            Logger.ProviderCallError(new InvalidOperationException("Provider not initialized"), call.MethodId);
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

            // 0.24.0+ DispatchSignal keyed by methodId — generated override switches on
            // GameMethodIds constants per signal method on this provider's services.
            await DispatchSignal(call.MethodId, call.Payload);
        }
        catch (Exception ex)
        {
            // Signal is fire-and-forget by contract — log and swallow so the entity grain
            // does not see an exception that would become a transport-level error on the session.
            Logger.ProviderCallError(ex, call.MethodId);
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
        // Snapshot / persistence — caller owns the byte[] past the grain method.
        return Context.Serializer.PackForExternalUsage(State);
    }

    public byte[] GetServerRandomBytes()
    {
        if (Context == null) return [];
        return Context.Serializer.PackForExternalUsage(_serverRandom);
    }

    public byte[] GetOptimisticRandomBytes()
    {
        if (Context == null) return [];
        return Context.Serializer.PackForExternalUsage(_optimisticRandom);
    }

    public byte[] GetNamedRandomsBytes()
    {
        if (Context == null || _namedRandoms.Length == 0) return [];
        return Context.Serializer.PackForExternalUsage(_namedRandoms);
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
