using System;

namespace SharedMeta.Core
{
    /// <summary>
    /// Access policy for entity subscription.
    /// Defines who can subscribe to entities of this type.
    /// </summary>
    public enum EntityAccessPolicy
    {
        /// <summary>Any player can subscribe. Default behavior.</summary>
        Open = 0,
        /// <summary>
        /// Access checked via IsAuthorized(string playerId) on service implementation.
        /// The service impl must define a public bool IsAuthorized(string playerId) method.
        /// </summary>
        Authorized = 1,
        /// <summary>Only the entity owner (entityId == playerId) can subscribe.</summary>
        OwnerOnly = 2,
        /// <summary>
        /// Entity owned by the player (entityId == playerId).
        /// Same server-side behavior as OwnerOnly.
        /// Generates convenience methods on MetaClient (no entityId needed).
        /// </summary>
        UserOwned = 3
    }

    /// <summary>
    /// Attribute to mark a class as a shared state entity.
    /// The generator will produce a partial class with serialization support.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class SharedStateAttribute : Attribute
    {
    }

    /// <summary>
    /// Lifecycle / sharing model of an entity. Drives how the framework resolves config
    /// versions, pins them across calls, validates joining subscribers, and which execution
    /// modes are admissible.
    ///
    /// Declared via <see cref="EntityScopeAttribute"/> on the <see cref="ISharedState"/> class.
    /// Default (no attribute) is <see cref="Private"/>.
    /// </summary>
    public enum EntityScope
    {
        /// <summary>
        /// Single-owner entity — typically the player's profile or a player-owned activity
        /// (e.g. solo expedition). Only the owner subscribes; other players may issue calls
        /// (e.g. send a gift) without subscribing.
        ///
        /// Config-version pin is established on the owner's subscribe and lives in grain
        /// memory for the active session. State migrates forward via <c>[MetaStateVersion]</c>
        /// thresholds when the subscriber's resolved config requires a higher schema.
        /// When the grain deactivates (no active subscribers + idle TTL) the pin is dropped;
        /// next activation re-establishes it from the next subscribe.
        ///
        /// Cold calls into a deactivated Private entity (no active subscriber) fall back to
        /// project policy via <c>IConfigVersionResolver</c>.
        /// </summary>
        Private = 0,

        /// <summary>
        /// Multi-player session entity — e.g. a PvP match, a co-op raid, an ephemeral party.
        /// The first subscriber establishes the config-version pin for the duration of the
        /// active session. Subsequent subscribers' resolved versions must agree with the
        /// pin on Major.Minor of every relevant config; patch differences are tolerated by
        /// downgrading the joiner to the entity's pinned patch. Major.Minor mismatch rejects
        /// the subscribe — incompatible clients cannot play together in the same session.
        ///
        /// When all subscribers leave and the grain deactivates, the pin is dropped. The next
        /// session naturally picks up newer versions and migrates state via
        /// <c>[MetaStateVersion]</c> thresholds.
        /// </summary>
        Shared = 1,

        /// <summary>
        /// Global / shared-world entity — e.g. a clan, a leaderboard, a global PvP arena.
        /// No per-session pin; the entity always operates under the versions resolved from
        /// <c>IConfigVersionResolver.CurrentClientVersion</c>. Subscribers must support those
        /// versions or are rejected. Admin-driven config rollouts cause a push notification
        /// to active subscribers, who fetch the new configs to keep replaying broadcasts.
        ///
        /// <see cref="ExecutionMode.Optimistic"/> and <see cref="ExecutionMode.CrossOptimistic"/>
        /// are not admissible on Global entities — the framework emits a compile-time error
        /// (<c>SHMETA_OPT_GLOBAL</c>) because the client cannot guarantee its local config
        /// matches the version under which the server will execute.
        /// </summary>
        Global = 2,
    }

    /// <summary>
    /// Declares the <see cref="EntityScope"/> of an <see cref="ISharedState"/> class.
    /// Absent attribute = <see cref="EntityScope.Private"/>. See <see cref="EntityScope"/>
    /// for the per-scope semantics.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class EntityScopeAttribute : Attribute
    {
        public EntityScope Scope { get; }

        public EntityScopeAttribute(EntityScope scope)
        {
            Scope = scope;
        }
    }

    /// <summary>
    /// Marks a private backing field for push-based change tracking.
    /// The source generator produces a public property with a tracking setter that
    /// records changes into ChangeTracker during method execution (client-only, zero server overhead).
    /// Field must be private, named with underscore prefix (e.g. _health),
    /// and have a serialization attribute ([Key(n)] or [MemoryPackOrder(n)]).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class TrackedAttribute : Attribute
    {
    }

    /// <summary>
    /// Declares a named deterministic random on a shared state class.
    /// The generator emits a typed <see cref="SharedMeta.Core.Random.IMetaRandom"/> property on service contexts
    /// operating on this state (e.g. <c>CombatRandom</c>), and the framework persists the random state
    /// alongside the entity as a packed list in attribute declaration order.
    /// <para>
    /// Both server and client run the same PRNG with the same seed — use this to isolate independent
    /// random streams within one entity (e.g. "Combat" vs "Loot") so advancing one does not affect another.
    /// </para>
    /// <para>
    /// Reordering or adding/removing attributes is a code change: positional storage means renamed or shifted
    /// entries are re-seeded from the default derivation. The existing entity's unrelated randoms keep advancing.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class NamedRandomAttribute : Attribute
    {
        /// <summary>Identifier used to generate the context property (e.g. "Combat" → Context.CombatRandom).</summary>
        public string Name { get; }

        /// <summary>
        /// Optional explicit seed literal. If null, the random is seeded from <c>entityId + ":" + Name</c>.
        /// Set this when you need a globally fixed stream regardless of the entity.
        /// </summary>
        public string? Seed { get; set; }

        public NamedRandomAttribute(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// Marks a class as static game configuration data.
    /// Config is provided by IMetaConfigProvider on the server and sent to clients on subscribe.
    /// Access via Context.Config in service methods and [MetaInit].
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MetaConfigAttribute : Attribute
    {
        /// <summary>
        /// If true, this config type is the default config.
        /// Services with DefaultConfig = true on [MetaService] will use this config type
        /// without needing an explicit ConfigType reference.
        /// Only one config class should be marked as default per assembly.
        /// </summary>
        public bool Default { get; set; }
    }

    /// <summary>
    /// 0.22.0+ Declares a structural-change cutoff on a config class. Clients running on
    /// config versions <c>&lt;</c> <see cref="MinConfigVersion"/> must execute every method
    /// of any service bound to this config as <see cref="ExecutionMode.ServerPatch"/> — the
    /// config structure changed enough that optimistic local execution would interpret data
    /// differently from the server and produce a desync.
    /// <para>
    /// Annotate the config class wherever a structural break shipped: a renamed field, a
    /// re-typed column, a re-keyed table. Additive config changes (new fields, new rows)
    /// do <b>not</b> need this annotation — the existing config-version pin handles them.
    /// </para>
    /// <para>
    /// Multiple attributes may stack to mark several cutoffs across the config's history.
    /// The compute pipeline applies the highest <c>MinConfigVersion</c> that exceeds the
    /// client's resolved config version, picking the most-restrictive applicable boundary.
    /// </para>
    /// <para>Example:</para>
    /// <code>
    /// [MetaConfig]
    /// [MetaConfigStructureBoundary("2.0", Reason = "ExpeditionConfig.Difficulty was split into two enums.")]
    /// public class ExpeditionConfig { ... }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class MetaConfigStructureBoundaryAttribute : Attribute
    {
        /// <summary>Minimum config version (Major.Minor) above which structure is stable.
        /// Clients below this version are force-downgraded to ServerPatch for all services
        /// bound to this config.</summary>
        public string MinConfigVersion { get; }

        /// <summary>Optional developer-facing explanation of what broke and why. Surfaced
        /// in diagnostic logs and dev tooling but never shown to end users.</summary>
        public string Reason { get; set; } = "";

        public MetaConfigStructureBoundaryAttribute(string minConfigVersion)
        {
            MinConfigVersion = minConfigVersion;
        }
    }

    /// <summary>
    /// Attribute to mark an interface as a shared meta service.
    /// The generator will produce Client Proxies and Server Dispatchers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public class MetaServiceAttribute : Attribute
    {
        /// <summary>
        /// The state type this service operates on.
        /// Required for DI registration code generation.
        /// </summary>
        public Type? StateType { get; set; }

        /// <summary>
        /// Explicit config type for this service.
        /// The config is provided by IMetaConfigProvider and available via Context.Config.
        /// Takes priority over DefaultConfig.
        /// </summary>
        public Type? ConfigType { get; set; }

        /// <summary>
        /// If true, use the config class marked with [MetaConfig(Default = true)].
        /// No explicit ConfigType reference needed.
        /// </summary>
        public bool DefaultConfig { get; set; }

        /// <summary>
        /// Framework subscriber interfaces implemented by this service.
        /// Used to generate subscriber method invokers for client-side replay.
        /// Example: SubscriberInterfaces = new[] { typeof(ILobbySubscriber) }
        /// </summary>
        public Type[]? SubscriberInterfaces { get; set; }

        /// <summary>
        /// Access policy for entities of this service's state type.
        /// Controls who can subscribe to the entity.
        /// If multiple services share the same state type, the most restrictive policy wins.
        /// </summary>
        public EntityAccessPolicy AccessPolicy { get; set; } = EntityAccessPolicy.Open;

        /// <summary>
        /// Whether the framework may auto-generate a patch-tracking copy of this service so the
        /// server can produce a state diff for clients force-downgraded to ServerPatch. Default
        /// <c>true</c>.
        /// <para>
        /// A service is "force-patch-able" when it declares a client-callable
        /// <c>Optimistic</c>/<c>Server</c>/<c>CrossOptimistic</c> method whose
        /// <c>Version &gt; MinCompatibleVersion</c> (version-fallback band) or whose bound config
        /// carries <c>[MetaConfigStructureBoundary]</c>, or any client-callable <c>ServerPatch</c>
        /// method (always emits a diff). All three of those modes run the body on the client
        /// (<c>Server</c> replays it from the recorded buffer after the server responds), so all
        /// diverge from a changed server body; <c>Query</c>/<c>Signal</c>/<c>Notification</c>/
        /// <c>Local</c>/<c>ServerReplace</c> do not. For such services the generator emits a
        /// <c>{Impl}_PatchTracked</c> copy whose <c>State</c> routes through the typed
        /// <c>{State}PatchWrapper</c>, so a normal <c>State.X = …</c> write transparently records
        /// a patch when the server runs under force-patch — no manual <c>PatchState</c> needed.
        /// </para>
        /// <para>
        /// Set <c>false</c> to opt out: no copy is generated, and a client that would otherwise be
        /// force-downgraded to ServerPatch for one of this service's methods is instead
        /// <b>rejected</b> at negotiation (it has no safe way to run the diverged method).
        /// Independent of <c>[MetaServiceImpl(DeepDesync = true)]</c>, which generates the same
        /// copy for CRC desync detection; opting out here still rejects force-patch clients.
        /// </para>
        /// </summary>
        public bool PatchTracking { get; set; } = true;
    }


    public enum ExecutionMode
    {
        /// <summary>
        /// 0.29.0+ Read-only method executed on the client over the locally replicated
        /// state — no RPC, no server roundtrip. Method body returns a value computed over
        /// <c>State</c>; result is never confirmed by the server because the body never
        /// runs there. Pair with <c>{Service}ApiClient</c> sync overloads.
        /// <para>Contract (enforced where possible by the generator):</para>
        /// <list type="bullet">
        /// <item>Must return a value (<c>Task&lt;T&gt;</c> or <c>T</c>) — <c>void</c> / bare
        ///       <c>Task</c> rejected.</item>
        /// <item>Must not mutate <c>State</c> — divergence between clients would be permanent.</item>
        /// <item>Must not call cross-entity services — those round-trip to the server.</item>
        /// <item>Must not consume <c>Context.Random</c> — would advance scroll position
        ///       only on the calling client, desyncing every other observer.</item>
        /// <item>Requires the entity to be subscribed at call time — otherwise <c>State</c> is null.</item>
        /// </list>
        /// <para>Differs from <see cref="Query"/> which is server-roundtrip read for entities the
        /// caller has NOT subscribed to. Use <see cref="LocalQuery"/> for cheap method-level
        /// computations over your own (or any subscribed) entity's state; use <see cref="Query"/>
        /// when authoritative source is on the server.</para>
        /// <para>Replaces the pre-0.29.0 <c>Local</c> mode which permitted client-only writes —
        /// that was a divergence anti-pattern (server never confirms, two clients diverge forever).
        /// UI-state mutations belong in a ViewModel / POCO outside SharedMeta.</para>
        /// </summary>
        LocalQuery,
        /// <summary>Execute on client first (optimistic), then sync with server. Default mode.</summary>
        Optimistic,
        /// <summary>Execute on server first, then replay on client.</summary>
        Server,
        /// <summary>
        /// Execute locally including cross-entity calls on local state,
        /// then confirm on server. Cross-entity broadcasts are suppressed
        /// for the caller. Server-only services will throw at runtime.
        /// </summary>
        CrossOptimistic,
        /// <summary>
        /// Execute only on server, generate state diff patch.
        /// Client applies patch directly without executing the method.
        /// Used for hotfixing server-side bugs when clients can't be updated.
        /// </summary>
        ServerPatch,
        /// <summary>
        /// Execute only on server, send full serialized state.
        /// Client replaces its entire state without executing the method or applying patches.
        /// Efficient when state is fully regenerated (e.g., generating a game map).
        /// </summary>
        ServerReplace,

        /// <summary>
        /// Read-only request/response method. Must return a value (void not allowed),
        /// callable without subscribing to the entity, and does not participate in state sync,
        /// broadcasts, replay, random state, or persistence. Client may generate a
        /// synchronous local-execution path (post-subscribe) or an async pre-subscribe proxy
        /// (see generated <c>{Service}QueryApi</c>). Cannot be overridden at runtime via
        /// <see cref="SharedMeta.Core.Network.IExecutionModeProvider"/> — this is a structural
        /// trait of the method, not a routing strategy.
        /// </summary>
        Query,

        /// <summary>
        /// Fire-and-forget signal. Method must return <c>void</c>; the client generates a
        /// synchronous <c>{Method}Signal()</c> that delegates to
        /// <see cref="SharedMeta.Core.Network.INetwork.SendSignalAsync"/> and never waits for a
        /// response. Server-side execution is read-only (no state mutation, no broadcast,
        /// no sequence increment, no persistence); errors are logged and swallowed.
        /// Cannot be overridden at runtime — this is a structural trait, not a routing strategy.
        /// </summary>
        Signal,

        /// <summary>
        /// 0.22.0+: Entity-to-entity fire-and-forget notification. The peer of <see cref="Signal"/>
        /// on the cross-entity axis — Signal is "client → entity, no wait", Notification is
        /// "entity → entity, no wait". Source grain dispatches via an Orleans <c>[OneWay]</c>
        /// path and continues without awaiting; the target processes normally (state mutates,
        /// broadcasts its own subscribers), but no result is recorded into the replay payload
        /// and no exception is propagated back to the source.
        /// <para>
        /// Constraints (validated by the generator with <c>#error</c>):
        /// <list type="bullet">
        /// <item>Return type must be <c>Task</c> or <c>void</c> — no <c>Task&lt;T&gt;</c>.</item>
        /// <item>Implicit <c>GenerateClientApi = false</c> — clients never originate notifications.</item>
        /// <item>Cannot combine with <c>Sync</c>, <c>SkipServerOnFalse</c>, <c>ForcePersist</c>.</item>
        /// <item>Cannot be overridden at runtime — structural trait.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Use when the cross-entity call doesn't need to block the caller — e.g.
        /// <c>ProfileService.GainPoints → ClanService.AddPower</c>: the profile doesn't need
        /// to wait for the clan to apply the delta because clan broadcasts to its own
        /// subscribers and the profile never reads clan state after the call. Removes one
        /// grain-to-grain await from the latency path.
        /// </para>
        /// </summary>
        Notification
    }

    /// <summary>
    /// Controls generation of a synchronous client API overload for an Optimistic (or Local) method.
    /// </summary>
    public enum SyncApi
    {
        /// <summary>Do not generate a sync overload. Only the async method is produced.</summary>
        None = 0,

        /// <summary>Generate both the async method and a sync `{Method}Sync` overload.</summary>
        Generate = 1,

        /// <summary>Generate ONLY the sync method; no async overload is produced.</summary>
        OnlySync = 2
    }

    /// <summary>
    /// What to do at runtime if the sync overload is invoked but conditions block synchronous execution
    /// (e.g. the effective execution mode has been overridden to a server-round-trip mode via config).
    /// </summary>
    public enum SyncPolicy
    {
        /// <summary>Throw InvalidOperationException. Default — catches misuse immediately.</summary>
        Throw = 0,

        /// <summary>Log a warning via MetaLog + IDiagnostics and continue executing locally.</summary>
        Warn = 1,

        /// <summary>Silently continue executing locally. Use only when you accept the risk.</summary>
        Silent = 2
    }

    /// <summary>
    /// 0.26.6+ Per-method opt-in for deep state comparison between client and server.
    /// Used via <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c>.
    /// <para>
    /// Mechanism: when set on a method, the client serializes the entity state at the
    /// chosen moment(s), computes an FNV-1a CRC, and stamps it on
    /// <c>RpcCallRequest.PreStateCrc</c>. The server (real Orleans <c>MetaProviderBase</c>
    /// or local <c>LocalConnection</c>) re-serializes its own state at the same moments,
    /// compares CRCs, and on mismatch ships its own full serialized state back via
    /// <c>MetaOperation.DesyncStateBytes</c>. The client's generated <c>*ApiClient</c>
    /// fires <c>IDesyncDiagnostics.OnDeepStateDesync</c> with both binaries so a user
    /// callback can deserialize and diff (e.g. via JSON conversion).
    /// </para>
    /// <para>
    /// Zero-cost when not used: methods without this attribute property compile with no
    /// snapshot / CRC / wire overhead — the generator emits the wrap path only for
    /// annotated methods.
    /// </para>
    /// <para>
    /// Only meaningful for <see cref="ExecutionMode.Optimistic"/> and
    /// <see cref="ExecutionMode.CrossOptimistic"/> — modes where both client and server
    /// actually execute the method. Server-only modes (Server / ServerPatch / ServerReplace)
    /// don't run the method on the client, so a "diff" before reaching the server is
    /// expected, not a desync.
    /// </para>
    /// </summary>
    public enum SnapshotTiming : byte
    {
        /// <summary>Default — no deep state check on this method. Generator emits no wrap.</summary>
        None = 0,

        /// <summary>Snapshot the state BEFORE the method runs on each side. Catches state
        /// drift that accumulated from prior calls — the current call's body never gets to
        /// diverge if pre-states already disagree.</summary>
        Before = 1,

        /// <summary>Snapshot the state AFTER the method runs on each side. Catches divergence
        /// in the method's own execution (e.g. non-deterministic ordering, missed
        /// <see cref="Context.Random"/> path).</summary>
        After = 2,

        /// <summary>Snapshot at BOTH moments. Identifies whether divergence existed before
        /// the call or was introduced by it.</summary>
        Both = 3,
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class MetaMethodAttribute : Attribute
    {
        /// <summary>
        /// RPC method alias (defaults to method name). When multiple <c>[MetaMethod]</c>
        /// declarations share the same <see cref="Alias"/>, they must differ in
        /// <see cref="Version"/>; the framework dispatches by <c>(Alias, MethodVersion)</c>
        /// tuple and routes legacy clients (MethodVersion == 0) to the lowest-versioned
        /// implementation under that alias.
        /// </summary>
        public string Alias { get; set; } = "";

        /// <summary>
        /// Method version for compatibility (0.22.0+).
        /// <para>
        /// Default <c>0</c> (legacy / unversioned) — the dispatcher treats it as the single
        /// implementation under the alias. Set explicitly (1, 2, ...) when introducing a
        /// new method body that must coexist with an older one for old clients. Each
        /// <c>(Alias, Version)</c> tuple must be unique within a service.
        /// </para>
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// Lowest client method-version the server will still serve for this method (0.23.0+).
        /// Default <c>0</c> = serve everyone.
        /// <para>
        /// Works together with the version-fallback matching in the compatibility handshake:
        /// </para>
        /// <list type="bullet">
        ///   <item>A client whose declared <see cref="Version"/> exactly matches a version the
        ///     server still declares runs the method <b>locally</b> (its body matches a live
        ///     server surface). <c>MinCompatibleVersion</c> does NOT gate exact matches.</item>
        ///   <item>A client whose version isn't declared on the server but is <c>&gt;=
        ///     MinCompatibleVersion</c> (and arg-shape-compatible) is force-downgraded to
        ///     <see cref="ExecutionMode.ServerPatch"/> — the server runs the authoritative body
        ///     and ships a state diff, so the stale client body never executes.</item>
        ///   <item>A client whose version is <c>&lt; MinCompatibleVersion</c> is <b>rejected</b>
        ///     for this method (per-method <see cref="System.InvalidOperationException"/> on
        ///     call) — too old to serve even via patch; force the client to update.</item>
        /// </list>
        /// <para>
        /// Typical hotfix: you changed a method body in a way that diverges from deployed
        /// clients. Bump <see cref="Version"/> (the new body's version) and leave
        /// <c>MinCompatibleVersion = 0</c> — deployed clients (below the new version) auto-patch,
        /// freshly-built clients at the new version run locally, and once everyone updates the
        /// patch path naturally stops. Raise <c>MinCompatibleVersion</c> only when you want to
        /// hard-block clients below a floor rather than patch them.
        /// </para>
        /// </summary>
        public int MinCompatibleVersion { get; set; }

        public bool GenerateClientApi { get; set; } = true;

        /// <summary>Execution mode. Defaults to Optimistic (client-first).</summary>
        public ExecutionMode Mode { get; set; } = ExecutionMode.Optimistic;

        /// <summary>
        /// If true: when local execution returns false/default, skip server call.
        /// Use for validation methods that can fail locally.
        /// </summary>
        public bool SkipServerOnFalse { get; set; }

        /// <summary>
        /// If true, EntityGrain always persists state after this method executes,
        /// regardless of the configured PersistencePolicy.
        /// Use for critical operations: purchases, currency changes, etc.
        /// </summary>
        public bool ForcePersist { get; set; }

        /// <summary>
        /// [DEPRECATED in 0.12.0] Use <c>Mode = ExecutionMode.Query</c> instead. Retained for
        /// backward compatibility; the generator accepts either form but not both on the same method.
        /// </summary>
        [System.Obsolete("Use Mode = ExecutionMode.Query instead. This bool flag will be removed in a future major version.")]
        public bool Query { get; set; }

        /// <summary>
        /// If true AND Query is true, skip EntityAccessPolicy checks for this query method.
        /// Allows reading public info from entities the caller cannot subscribe to.
        /// Ignored if Query is false.
        /// </summary>
        public bool OpenAccess { get; set; }

        /// <summary>
        /// Controls generation of a synchronous client API overload (e.g. `{Method}Sync`).
        /// Only valid for <see cref="ExecutionMode.Optimistic"/> and <see cref="ExecutionMode.LocalQuery"/>.
        /// The service method itself must have a non-Task return type.
        /// Use this in frame-critical code paths (e.g. DOTS main-thread) where the mutation must be
        /// visible in the same frame without an `await` boundary.
        /// </summary>
        public SyncApi Sync { get; set; } = SyncApi.None;

        /// <summary>
        /// What the generated sync method does when runtime conditions block synchronous execution
        /// (e.g. execution mode was overridden to Server via config). Defaults to <see cref="SyncPolicy.Throw"/>.
        /// </summary>
        public SyncPolicy SyncPolicy { get; set; } = SyncPolicy.Throw;

        /// <summary>
        /// [DEPRECATED in 0.12.0] Use <c>Mode = ExecutionMode.Signal</c> instead. Retained for
        /// backward compatibility; the generator accepts either form but not both on the same method.
        /// </summary>
        [System.Obsolete("Use Mode = ExecutionMode.Signal instead. This bool flag will be removed in a future major version.")]
        public bool Signal { get; set; }

        /// <summary>
        /// 0.26.6+ Per-method opt-in for deep state comparison between client and server. When
        /// set to anything other than <see cref="SnapshotTiming.None"/>, the generator emits
        /// snapshot + FNV-1a CRC logic on the client *ApiClient for THIS method only — other
        /// methods are unaffected. The client stamps the resulting CRC on
        /// <c>RpcCallRequest.PreStateCrc</c>; the server (real or local) re-computes its own
        /// CRC at the matching moment and ships full serialized state back via
        /// <c>MetaOperation.DesyncStateBytes</c> on mismatch. Client fires
        /// <c>IDesyncDiagnostics.OnDeepStateDesync</c> with both binaries.
        /// <para>
        /// Only meaningful for <see cref="ExecutionMode.Optimistic"/> /
        /// <see cref="ExecutionMode.CrossOptimistic"/> — modes where both sides execute the
        /// method body.
        /// </para>
        /// </summary>
        public SnapshotTiming DeepStateCheck { get; set; } = SnapshotTiming.None;
    }

    /// <summary>
    /// Marks a method on a [MetaServiceImpl] class as a state initializer.
    /// Called during EntityGrain activation with the current state version.
    /// <para>
    /// Two signatures are supported:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>Task&lt;int&gt; Init(int version)</c> — receives current schema version.</item>
    ///   <item><c>Task&lt;int&gt; Init(int version, int targetVersion)</c> — also receives the
    ///   target schema for this step. On first init (version == 0), the framework computes target
    ///   as the highest <see cref="MetaStateVersionAttribute.StateVersion"/> whose config conditions
    ///   are satisfied by the current provider config (or 1 when no <c>[MetaStateVersion]</c> is
    ///   declared). On subsequent migration steps, target equals that step's
    ///   <see cref="MetaStateVersionAttribute.StateVersion"/>. Use the two-arg form to write
    ///   idempotent migrations: <c>if (version &lt; N &amp;&amp; target &gt;= N) { ... }</c>.</item>
    /// </list>
    /// Returns the new version number to persist.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class MetaInitAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a <c>[MetaMethod]</c> as exempt from lazy schema migration. When the method is
    /// invoked on an entity whose state schema is below the current required version, the
    /// framework will <b>not</b> trigger a <c>[MetaInit]</c> migration — the method runs against
    /// whatever schema is currently persisted.
    /// <para>
    /// Use for cross-entity "administrative" calls (e.g. inbox/gift sending) that must work on
    /// entities owned by clients on older config branches without forcing a migration the
    /// receiver hasn't asked for. The method body is responsible for being schema-tolerant.
    /// </para>
    /// <para>
    /// While this method runs, <c>Context.Config</c> is pinned to the config branch that matches
    /// the entity's current state schema (per the inverse of <see cref="MetaStateVersionAttribute"/>
    /// thresholds), not the latest provider version.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class NoMigrateAttribute : Attribute
    {
    }

    /// <summary>
    /// Caps lazy schema migration for this method. If the entity's state schema is below
    /// <see cref="StateVersion"/>, the framework migrates only up to <see cref="StateVersion"/>
    /// before dispatching — never beyond. If the schema is already at or above
    /// <see cref="StateVersion"/>, no migration runs (the method may still observe a higher schema).
    /// <para>
    /// Use when a release intentionally raises the floor for a specific operation (e.g. a new
    /// feature path requires schema ≥ 2) without forcing a full migration to whatever the
    /// current latest schema is.
    /// </para>
    /// <para>
    /// Conflicts with <see cref="NoMigrateAttribute"/> on the same method (compile-time error).
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class MinStateVersionAttribute : Attribute
    {
        public int StateVersion { get; }

        public MinStateVersionAttribute(int stateVersion)
        {
            StateVersion = stateVersion;
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class MetaServiceImplAttribute : Attribute
    {
        public Type ServiceInterface { get; }
        public Type StateType { get; }
        public Type[] Dependencies { get; }

        /// <summary>
        /// When true, the generator produces an additional PatchTracked copy of this service class.
        /// The copy routes all State access through PatchWrapper, enabling field-level desync detection
        /// by comparing patch CRCs between server and client.
        /// </summary>
        public bool DeepDesync { get; set; }

        public MetaServiceImplAttribute(Type serviceInterface, Type stateType, params Type[] dependencies)
        {
            ServiceInterface = serviceInterface;
            StateType = stateType;
            Dependencies = dependencies;
        }
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public class ServerMetaServiceAttribute : Attribute
    {
        // Marker for services that run on Server and Replay on Client
    }

    /// <summary>
    /// Defines a trigger that executes a method when a condition is met after another method completes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class TriggerAttribute : Attribute
    {
        /// <summary>
        /// The method name that triggers this check.
        /// </summary>
        public string On { get; set; } = "";
        
        /// <summary>
        /// Boolean method name to check. If true, the marked method is executed.
        /// </summary>
        public string Condition { get; set; } = "";
    }

    /// <summary>
    /// Subscribe to receive callbacks before or after a method executes.
    /// Used for UI integration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class SubscribeAttribute : Attribute
    {
        /// <summary>
        /// If true, callback is invoked before method execution.
        /// </summary>
        public bool Before { get; set; }
        
        /// <summary>
        /// If true, callback is invoked after method execution.
        /// </summary>
        public bool After { get; set; }
        
        /// <summary>
        /// Method name to subscribe to.
        /// </summary>
        public string Method { get; set; } = "";
    }

    /// <summary>
    /// Marks a transformer class and specifies resolution mode.
    /// By default, transformers are auto-registered in generated code.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class TransformerAttribute : Attribute
    {
        /// <summary>
        /// If true, resolve transformer via DI. If false, create via Activator.
        /// </summary>
        public bool UseResolver { get; set; } = false;

        /// <summary>
        /// If true, skip auto-registration in generated TransformerRegistry.
        /// Use this when you want to register the transformer manually.
        /// </summary>
        public bool NoAutoRegister { get; set; } = false;
    }

    /// <summary>
    /// Explicitly specify a transformer for a method parameter.
    /// Overrides auto-discovery from TransformerRegistry.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class TransformAttribute : Attribute
    {
        public Type TransformerType { get; }

        /// <summary>Use specific transformer type.</summary>
        public TransformAttribute(Type transformerType) => TransformerType = transformerType;
    }

    /// <summary>
    /// Explicitly skip transformation for a method parameter.
    /// Use this to disable auto-discovery for a specific parameter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class SkipTransformAttribute : Attribute
    {
    }

    /// <summary>
    /// Defines a trigger that fires when a framework service event is received.
    /// Use this to trigger MetaMethods in response to service events (e.g., Lobby.OnMatchFound).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ServiceTriggerAttribute : Attribute
    {
        /// <summary>
        /// The subscriber interface type (e.g., typeof(ILobbySubscriber)).
        /// </summary>
        public Type Service { get; set; } = null!;

        /// <summary>
        /// The method name on the subscriber interface that triggers this (e.g., "OnMatchFound").
        /// </summary>
        public string Method { get; set; } = "";

        /// <summary>
        /// Optional boolean method name to check before executing.
        /// </summary>
        public string Condition { get; set; } = "";
    }

    /// <summary>
    /// Marker interface for framework service subscriber interfaces.
    /// Implement this on interfaces like ILobbySubscriber.
    /// </summary>
    public interface IFrameworkServiceSubscriber
    {
    }

    /// <summary>
    /// Ordering mode for client-side operation execution.
    /// Controls how operations are applied relative to broadcasts from other clients.
    /// </summary>
    public enum OrderingMode
    {
        /// <summary>
        /// Operations are queued and applied in entity sequence order.
        /// Ensures all clients see the same order of operations.
        /// Use for state-mutating operations where order matters.
        /// </summary>
        Ordered,

        /// <summary>
        /// Operations are applied immediately without waiting for sequence ordering.
        /// Use for read-only operations or when ordering doesn't matter.
        /// </summary>
        Immediate
    }

    /// <summary>
    /// Specifies the ordering mode for a service or method.
    /// When applied to an interface, sets the default for all methods.
    /// When applied to a method, overrides the service-level default.
    /// Default behavior (no attribute): Ordered.
    /// </summary>
    /// <example>
    /// // Service-level: all methods are immediate by default
    /// [OrderedExecution(OrderingMode.Immediate)]
    /// public interface IReadOnlyService { ... }
    ///
    /// // Method-level: override service default
    /// [MetaMethod]
    /// [OrderedExecution(OrderingMode.Immediate)]
    /// void GetStatus();
    /// </example>
    [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method)]
    public class OrderedExecutionAttribute : Attribute
    {
        /// <summary>
        /// The ordering mode for this service or method.
        /// </summary>
        public OrderingMode Mode { get; }

        public OrderedExecutionAttribute(OrderingMode mode)
        {
            Mode = mode;
        }
    }

    /// <summary>
    /// Serializer type for code generation.
    /// Controls how the generator produces serialization code.
    /// </summary>
    public enum SerializerType
    {
        /// <summary>
        /// Auto-detect serializer from referenced assemblies.
        /// Checks for MemoryPack first, falls back to generic IPayloadWriter.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Generate MemoryPack-specific code using ref struct writers.
        /// Requires MemoryPack package reference.
        /// </summary>
        MemoryPack = 1,

        /// <summary>
        /// Generate generic code using IPayloadWriter interface.
        /// Works with any serializer implementing IMetaSerializer.
        /// </summary>
        Generic = 2
    }

    /// <summary>
    /// Specifies the serializer type for code generation.
    /// Apply to the assembly to control generated serialization code.
    /// If not specified, auto-detection is used (MemoryPack if available, otherwise Generic).
    /// </summary>
    /// <example>
    /// [assembly: SharedMeta.Core.MetaSerializer(SharedMeta.Core.SerializerType.MemoryPack)]
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class MetaSerializerAttribute : Attribute
    {
        /// <summary>
        /// The serializer type to use for code generation.
        /// </summary>
        public SerializerType Type { get; }

        public MetaSerializerAttribute(SerializerType type)
        {
            Type = type;
        }
    }

    /// <summary>
    /// 0.22.0+ Project-level opt-out for the compatibility-negotiation generator features
    /// (client signature emit, capabilities gate in <c>*ApiClient</c>, force-ServerPatch
    /// routing). Default is <see cref="Enabled"/> = <c>true</c> — the generator emits the
    /// full negotiation surface, the runtime behaviour stays no-op until the consumer
    /// wires up the registry (so an unused negotiation is essentially free).
    /// <para>
    /// Apply this on a project's assembly with <c>Enabled = false</c> when you've decided
    /// not to use compatibility negotiation at all and want to drop the generated code from
    /// your build. The generator emits empty / no-op fallbacks in that mode.
    /// </para>
    /// <para>Example:</para>
    /// <code>
    /// [assembly: SharedMeta.Core.SharedMetaCompatibilityOptions(Enabled = false)]
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class SharedMetaCompatibilityOptionsAttribute : Attribute
    {
        /// <summary>
        /// When <c>true</c> (default), the generator emits client signature + capabilities
        /// gate machinery. When <c>false</c>, those emits are skipped — generated client
        /// code never consults capabilities, and <c>GameServiceDiscoveryBase.ClientSignature</c>
        /// is still emitted but with an empty <see cref="SharedMeta.Core.Transport.MetaClientSignature.KnownMethods"/>
        /// list (so the type is still resolvable for opt-in test scaffolding).
        /// </summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Marks a method generated by SharedMeta.Generator as the counterpart of a specific
    /// <c>[MetaMethod]</c> on a <c>[MetaService]</c> interface.
    /// <para>
    /// Emitted on every method produced by the source generator that mirrors a user-declared
    /// <c>[MetaMethod]</c> — across all execution modes (Async / Sync / Signal / Query) and
    /// all generated client classes (<c>{Service}ApiClient</c>, <c>{Service}EntityQueryApi</c>,
    /// <c>{Service}EntityRecorder</c>, <c>{Service}LocalEntityCaller</c>,
    /// <c>{Service}EntityReplayer</c>, plus the <c>{Interface}EntityCaller</c> proxy
    /// interface).
    /// </para>
    /// <para>
    /// Tooling (e.g. the SharedMeta Rider plugin) reads this attribute to bridge
    /// "Find Usages" between the user-authored interface method and every generated
    /// counterpart, without depending on naming conventions.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class GeneratedFromMetaMethodAttribute : Attribute
    {
        /// <summary>The original <c>[MetaService]</c> interface type.</summary>
        public Type ServiceInterface { get; }

        /// <summary>
        /// The original <c>[MetaMethod]</c> name as declared on the interface
        /// (without any <c>Async</c> / <c>Sync</c> / <c>Signal</c> suffix added by the
        /// generator).
        /// </summary>
        public string MethodName { get; }

        public GeneratedFromMetaMethodAttribute(Type serviceInterface, string methodName)
        {
            ServiceInterface = serviceInterface;
            MethodName = methodName;
        }
    }

    /// <summary>
    /// Declares a state schema migration breakpoint on an <see cref="ISharedState"/> class.
    ///
    /// When all config conditions for a given <see cref="StateVersion"/> are met (AND semantics
    /// across multiple attributes with the same <see cref="StateVersion"/>), the source generator
    /// emits code that calls <c>[MetaInit]</c> with <see cref="IMetaConfigProvider{TConfig}.GetConfig"/>
    /// pinned to the transition version — not the current latest config. Sequential migrations
    /// (profiles that skipped intermediate versions) receive one <c>[MetaInit]</c> call per
    /// unprocessed step in ascending order.
    ///
    /// Only declare breakpoints — points where the schema actually changes.
    /// Config bumps that require no schema change need no entry.
    ///
    /// Example:
    /// <code>
    /// [MetaStateVersion(2, "2.1", typeof(ExpeditionConfig))]   // schema 2 when config >= 2.1
    /// [MetaStateVersion(3, "3.1", typeof(ExpeditionConfig))]   // schema 3 when BOTH reach threshold
    /// [MetaStateVersion(3, "1.4", typeof(SeasonConfig))]
    /// public partial class ProfileState : ISharedState { ... }
    /// </code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public class MetaStateVersionAttribute : Attribute
    {
        /// <summary>The target state schema version this migration step produces.</summary>
        public int StateVersion { get; }

        /// <summary>
        /// Minimum config version (Major.Minor) that triggers this schema transition.
        /// Format: "Major.Minor" — e.g. "2.1".
        /// </summary>
        public string MinConfigVersion { get; }

        /// <summary>
        /// Which config type this threshold applies to.
        /// <c>null</c> means the state's single primary config.
        /// </summary>
        public Type? ConfigType { get; }

        /// <summary>
        /// When <c>true</c> (0.22.0+), this schema step represents a STRUCTURAL change to
        /// the persisted state — old clients cannot deserialize / reason about the new
        /// shape. The framework rejects subscribe attempts from clients whose resolved
        /// config is below this threshold's <see cref="MinConfigVersion"/> with
        /// <see cref="IncompatibleFeatureException"/>; the client UI is expected to surface
        /// a non-blocking "update required for this entity" notification.
        /// <para>
        /// Default <c>false</c>: schema bumps are treated as backward-compatible — the new
        /// shape is a superset (additive fields tolerated by MemoryPack
        /// <c>GenerateType.VersionTolerant</c> / MessagePack key-based deserialization).
        /// Old clients still subscribe, miss any new fields they don't know about, and the
        /// game keeps progressing for them.
        /// </para>
        /// </summary>
        public bool Breaking { get; set; }

        public MetaStateVersionAttribute(int stateVersion, string minConfigVersion, Type? configType = null)
        {
            StateVersion = stateVersion;
            MinConfigVersion = minConfigVersion;
            ConfigType = configType;
        }
    }
}
