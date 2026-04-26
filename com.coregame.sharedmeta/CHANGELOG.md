# Changelog

## [0.14.1] - 2026-04-26

### Fixed

- **`*ServiceExtensions.g.cs` no longer fails to compile when the state type and the service interface live in different namespaces.** `ServiceRegistrationGenerator` was emitting the `PatchApplier` lambda with an unqualified `{StateName}PatchApplier` reference. The applier class is generated in the *state's* namespace, while the extensions file only `using`s the *service interface's* namespace — so when the two namespaces differed, the build broke with `CS0103: The name '{State}PatchApplier' does not exist in the current context`. The bundled CardGame example colocates state and services in one namespace, which masked the bug. The reference is now fully qualified as `{stateTypeFullName}PatchApplier` ([ServiceRegistrationGenerator.cs:141](src/SharedMeta.Generator/Generators/ServiceRegistrationGenerator.cs#L141)).

## [0.14.0] - 2026-04-26

### Added — multi-service-on-entity state propagation

- **`EntityStateContainer<TState>` — shared state holder per entity** (`Runtime/Core/Client/EntityStateContainer.cs`). Owns the current state object, the `MutationCount` counter, and the `OnMutated` event. Every API client subscribed to the same entity now points at the same container, so:
  - **Foreign-service broadcasts update local state for ALL execution modes** — `Optimistic`, `Server`, `CrossOptimistic`, `ServerPatch`, `ServerReplace`. The entity-level handler in `MetaServiceResolver` applies state-data when the broadcast carries it; for pure replay broadcasts (Optimistic / Server / CrossOptimistic without state-data) it falls back to a generator-emitted `EntityReplayDispatcher` that spins up the foreign service's impl class on the fly and re-runs the method against the shared state. **No matching ApiClient required on the receiver.** Pre-0.14.0 the per-ApiClient `ServiceName` filter silently dropped foreign broadcasts in every mode; that gate is gone.
  - **No drift after `ServerReplace`.** When one ApiClient's wholesale-replace path swaps the state instance, every other ApiClient on the entity sees the new instance through the shared container — pre-0.14.0 the non-receiving ApiClients kept pointing at the stale instance.
  - **`MutationCount` is now shared per entity**, not per ApiClient. Every API client subscribed to the entity returns the same value; the counter bumps exactly once per mutation regardless of how many ApiClients are subscribed. The 0.13.1 polling semantic is preserved but more useful — `if (api.MutationCount != lastSeen) Invalidate();` now catches mutations from any service on the entity.
  - **`OnStateMutated` fires on every API client on the entity in lock-step** — sourced from `EntityStateContainer.OnMutated`. Fires on the same set of mutations as `MutationCount` bumps (foreign-service broadcasts included).

- **`MetaServiceResolver.GetStateContainer<TState>(entityId)`** — direct access to the shared container. Useful for entity-only consumers (e.g. UI views) that don't need a full ApiClient.

- **`IEntityStateContainer`** — non-generic surface so the resolver can drive the container without knowing `TState` at runtime. Exposes `State`, `MutationCount`, `OnMutated`, `NotifyMutated`, `ReplaceObject`.

- **`MetaServiceConfig.StateContainerFactory` / `PatchApplier` / `EntityReplayDispatcher`** — generator-emitted callbacks. `StateContainerFactory` wraps a deserialized state into a typed container without reflection. `PatchApplier` applies ServerPatch byte payloads to the shared state; the entity-level handler in `MetaServiceResolver` activates `ChangeTracker` around the call so `[Tracked]` field setters touched by the patch fire `Tracked{State}.OnChanged`. `EntityReplayDispatcher` is the foreign-service replay path — it instantiates the service's impl class, sets up `ClientMetaContext` with replay context (random + named randoms + config), activates `ChangeTracker`, dispatches the broadcast's method against the shared state, and `FlushAndNotify`s. Skips Query/Signal methods (they don't broadcast).

### Changed (breaking for generated code only)

- Generated `*ApiClient.g.cs` constructor signature: third positional parameter is now `EntityStateContainer<TState>` instead of `TState`. Source-compatible for callers because nobody constructs API clients directly — `MetaServiceResolver.GetServiceAsync` does it via the generated factory. Rebuild with the new generator DLL and any consumer code keeps working.
- `MetaServiceConfig.ApiClientFactory` third positional parameter now receives an `IEntityStateContainer` (boxed) instead of the raw state object. Generator-emitted lambdas cast inside.
- `MutationCount` is no longer per-ApiClient (`int { get; private set; }`) — it now delegates to the shared container.
- `OnStateMutated` is no longer fired manually from per-method code paths; it sources from `EntityStateContainer.OnMutated`. Firing order is preserved everywhere a setter actually runs (per-method paths, foreign-service replay, ServerPatch application): `Tracked{State}.OnChanged` first via `_tracker.FlushAndNotify()`, `OnStateMutated` after via `container.NotifyMutated()`. The one path that differs is `ServerReplace` wholesale-replace at entity level — the container is replaced with a freshly deserialized instance (no setter calls happen on the old one), so only `OnStateMutated` fires and `Tracked{State}.OnChanged` has nothing to notify (same as pre-0.14.0).

### Notes

- The foreign-service replay path requires that the impl class for the foreign service is reachable from the client assembly (it lives in shared code) and that its constructor is parameterless. Cross-entity calls (`Context.GetI{Service}(otherEntityId)`) inside foreign-service methods are not replayed during entity-level dispatch — that scenario still requires the matching service to be subscribed locally so its ApiClient handles the cascade.

## [0.13.1] - 2026-04-25

### Added

- **`MutationCount` property on generated API clients** — local-only `int` counter incremented on every state mutation: Optimistic / CrossOptimistic local execution, Server / ServerPatch / ServerReplace result application, incoming broadcasts (regular and subscriber-event), and reconnect refresh. Cheap polling alternative to the `OnStateMutated` event for flows that just want to ask "did anything modify my state since I last checked?" — e.g. cache invalidation, idempotent re-renders. Fires in more places than `OnStateMutated` (which intentionally skips Optimistic/CrossOptimistic), so prefer `MutationCount` when you want a unified "state was touched" signal. Not synchronized across clients, not persisted, not coordinated with the network sequence number.

### Internal

- `ClientVersionPolicy` refactor — single public method `Task<ClientVersionValidationResult> ValidateAsync(string?)` encapsulates TTL caching, grain refresh, and version parsing. `IsCacheExpired` and `RefreshFromGrainAsync` are no longer part of the public surface; `ValidateClientVersion` / `TryParseVersion` moved out of `MetaConnectionHandler`. Generator updated to inject `IGrainFactory` into the policy at construction time. No behavior change for consumers — the connect-path version gate still enforces the same rules.

## [0.13.0] - 2026-04-25

### Added

- **Client version checking on `SessionConnect`** — the server can now enforce a minimum client version and reject incompatible clients before they establish a session.
  - `SessionConnectRequest.ClientVersion` — client sends its version string (`"major.minor.patch"`) on every connect.
  - `SessionConnectResponse.ServerVersion` / `.MinClientVersion` — server echoes its version and the minimum it accepts; clients can surface these in upgrade prompts.
  - `ConnectionSessionConnectResult.ServerVersion` / `.MinClientVersion` — surfaced through `IConnection` to the application layer.
  - `MetaTransportOptions.ServerVersion` / `.MinClientVersion` — static startup configuration; `MinClientVersion` can be overridden at runtime without restarting.
  - **Compatibility rules** (applied on every `SessionConnect` when both `ServerVersion` and `MinClientVersion` are set):
    - Client did not send a version → allowed through (backward compatibility with old clients).
    - `clientMajor ≠ serverMajor` → rejected ("incompatible major version").
    - `clientVersion < MinClientVersion` (minor/patch) → rejected ("client too old, please upgrade").
    - Otherwise → accepted.
  - All six client transports updated: Unity `SignalRConnection` (new `clientVersion` constructor param), `UnityHttpConnection`, `BestHttpPollingConnection`, `BestHttpSignalRConnection` (new `ClientVersion` option), .NET `SignalRConnection` (new `clientVersion` param), `HttpPollingConnectionOptions.ClientVersion`.

- **`ClientVersionPolicy` — per-silo cache + validator** (registered automatically by `AddMetaServices()` / `ConfigureMeta()`):
  - Initialized from `MetaTransportOptions` at startup; the cluster-wide override is fetched from `IVersionPolicyGrain` and cached locally with a 60-second TTL (`ClientVersionPolicy.CacheTtl`).
  - Single public method: `Task<ClientVersionValidationResult> ValidateAsync(string? clientVersion)` — encapsulates the cache refresh, grain fetch, and version parsing. Returns `{ ServerVersion, MinClientVersion, Error }`; `Error == null` ⇒ allowed.
  - `MetaConnectionHandler` calls `ValidateAsync` once per connect — no direct grain or `Interlocked` access in handler code.

- **`IVersionPolicyGrain` + `VersionPolicyGrain` — cluster-wide version gate via Orleans grain singleton**:
  - Single activation (key `"global"`) shared across the entire cluster. State persisted to the `"Default"` storage provider.
  - Calls `SetMinClientVersionAsync(version)` from any process in the cluster (e.g. a dedicated admin service) to block old clients on all silos simultaneously.
  - Grain value overrides static config; setting it to `null` clears the override and falls back to `MetaTransportOptions.MinClientVersion`. Admin changes propagate to every silo within one TTL window without hammering the grain.

## [0.12.4] - 2026-04-24

### Removed

- **`MetaContext.GetEntityApi<T>(string id)` — the abstract method and its three overrides in `ClientMetaContext`, `ServerMetaContext` and `CrossOptimisticMetaContext`.** It was a documented-but-dead API: declared `abstract` on `MetaContext`, but every concrete implementation threw `NotImplementedException` ("requires generated recorder/replayer/caller"). The real cross-entity entry point has always been the typed `GetI{Service}(entityId)` accessor that the source generator injects into the service partial based on `[MetaServiceImpl(..., typeof(IService))]` dependency declarations. Leaving the dead method on the public surface actively misled AI-assisted code generation: agents reading `SharedMeta-AI.md` / `GUIDE.md §6` / `ARCHITECTURE.md` copied the `Context.GetEntityApi<IMapService>(mapId).MethodAsync(...)` snippet verbatim, producing code that compiled but threw at runtime. No runtime consumers existed (generator and tests don't reference it), so removal is source-compatible for anyone who was using the actual working pattern

### Fixed

- **Documentation swept for the dead `Context.GetEntityApi` snippet.** Every reference replaced with the declared-dependency + generated-`GetI{Service}` pattern, with an explicit "do not use" note citing this removal:
  - `SharedMeta-AI.md` — "Cross-Entity Calls" section and the `[MetaService]` vs `[ServerMetaService]` comparison table (the file AI agents read first)
  - `docs/GUIDE.md` — §6 "Cross-Entity Calls", §6.5 comparison table, §7 `ResolveSessionResources` example, Signal-method limitation wording
  - `docs/ARCHITECTURE.md` — §8 "Cross-Entity Communication"
  - `examples/Unity/Expedition/ServerSolution/Expedition.Server/SharedMeta-AI.md`
  - Project Wizard generated `SharedMeta-AI.md` snippet (emitted per-project)

## [0.12.3] - 2026-04-23

### Changed

- **Unity editor menus moved under `Tools/SharedMeta/`** — `Tools/SharedMeta/Project Wizard` and `Tools/SharedMeta/Server Runner`. The previous top-level `SharedMeta/` entries are gone. Aligns with Unity convention that third-party tooling lives under `Tools/`
- **Project Wizard default project names aligned with convention** — `Meta.Shared` (was `MyGame.Shared`), `Meta.Client` (was `Meta`), `Meta.Server` (was `MyGame.Server`). Default Unity folders follow suit (`Assets/Scripts/Meta.Shared`, `Assets/Scripts/Meta.Client`). Still user-editable in the wizard
- **Project Wizard generated `MetaGameClient.Start()` now wraps the connect flow in `try/catch`** — logs `Debug.LogError` + `Debug.LogException` on failure instead of letting the async-void exception become an unhandled UnityException, with an inline pointer to the Expedition sample's modal-reconnect UI pattern for production use
- **Project Wizard generated `MetaClientOptions` block now documents optional hooks** — `Diagnostics` (`IDesyncDiagnostics`) and `ConnectionHealth` (`IConnectionHealth`) as commented-out property initializers, plus a hint about `Dispatcher.DiagnosticsLog` for request-lifecycle tracing
- **Project Wizard Expedition template now showcases new 0.11 / 0.12 features** — `[NamedRandom("Map")]` + `[NamedRandom("Loot")]` on `ExpeditionState` (independent per-mechanic scroll), `[MetaMethod(Mode = ExecutionMode.Query)] bool IsActive()` (read-only, no-subscription polling), `[MetaMethod(Mode = ExecutionMode.Signal)] void Ping(string)` (fire-and-forget, state-immutable), and `GenerateMap` / `Move` impls now roll against `MapRandom` / `LootRandom` directly

### Fixed

- **Project Wizard no longer mutates an unrelated parent `Directory.Packages.props` when generating into a subfolder of a repository that already has a root-level CPM.** `FindDirectoryPackagesProps` used to walk up the directory tree without bound; if the wizard was invoked from within a repo that defines centralised package versions at its root (e.g. testing the wizard from inside this SharedMeta repo), it would append `CoreGame.SharedMeta.*` entries to that root file instead of creating a fresh one inside the wizard's target solution. Search is now capped at the declared solution root — no walk above. Existing CPM at solution root is still detected and extended
- **Generated Server `.csproj` no longer fails to compile when SignalR + MessagePack is selected.** `Program.cs` emits `AddMetaMessagePackProtocol()` from the separate `CoreGame.SharedMeta.Transport.SignalR.MessagePack` package; the wizard now includes that `<PackageReference>` (and the matching `<PackageVersion>` in the generated / merged `Directory.Packages.props`) whenever both conditions hold
- **Generated Server `Program.cs` Serilog config now surfaces SharedMeta diagnostic logs by default** — adds `.MinimumLevel.Override("SharedMeta", LogEventLevel.Debug)` to the `UseSerilog` initializer. Without it, `[Desync]`/`[Handler]` entries stayed hidden behind Serilog's default Information threshold
- **Generated Expedition client example pointed at non-existent `ResumeOrStartExpeditionAsync` / `result.EntityId`.** Corrected to `StartExpeditionAsync` / `entityId`, with added inline examples for `PingSignal` and the `ExpeditionServiceQueryApi.IsActiveAsync` flow

## [0.12.2] - 2026-04-23

### Fixed

- **Reconnect regression introduced by the 0.12.1 `OrderedDispatcher` refactor.** `ClientDispatcher.HandleDisconnected` called `_ordering.Reset(1)` on every transient disconnect, which cleared the pending buffer **and** rewound the expected sequence number to 1. On session resume the server continues numbering from `lastAcknowledgedSequence + 1` (e.g. seq 9, 10, 11 …) — those packets landed in the re-initialised buffer waiting for seq 1, which never comes, so nothing was ever delivered. The resent RPC's response flows through the same ordering path, so the retry loop saw no reply and fired again, producing the observable infinite `Re-sending ↔ Disconnected` oscillation. Pre-refactor `MessageBuffer.Clear()` correctly preserved the head. **Symptom was SignalR-specific only because Expedition's `DebugConnectionWrapper` uses `PacketLossMode.ConnectionDrop` for SignalR** (drops the whole connection → reconnect cycle triggers every time) whereas HTTP uses `PacketLossMode.RequestHang` (no reconnect for most cases → the rewind never fires)
- `ConnectSessionAsync` now explicitly `Reset(1)`s ordering and zeroes `_lastAcknowledgedSequence` when the server reports `IsNewSession = true` (resume attempt failed, server minted a fresh session). Without this, stale `_nextExpected` from the prior session would drop the new session's first broadcasts as duplicates

### Changed

- `OrderedDispatcher` now exposes two distinct methods: `Clear()` (drops the pending buffer, keeps `_nextExpected` — correct for transient disconnects that will resume the same session) and `Reset(long)` (drops buffer **and** sets a new `_nextExpected` — for session supersede / restart where the server will renumber from 1). Single-method API from 0.12.1 conflated the two cases

## [0.12.1] - 2026-04-23

### Fixed

- **HTTP transport desync reports silently dropped when invoked from a background continuation.** `UnityWebRequest` construction requires Unity's main thread, but the generated `ApiClient` fires `_network.SendDesyncReportAsync(...)` from a `.ContinueWith(...)` that runs on the thread pool — construction threw `UnityException: Create can only be called from the main thread` and the exception was absorbed by the fire-and-forget `_ = ...` path, so the server never received the report. `UnityHttpConnection` now captures the `SynchronizationContext` of the thread that constructs it (expected to be Unity's main thread) and marshals `UnityWebRequest` construction onto it when `PostRawAsync` is entered from a non-main thread. JSON body serialization stays off the main thread (CPU work). No overhead on the fast path when already on the main thread
- **`SessionManagerGrain.BroadcastToSessionOp` dropped `NamedRandomScrollDeltas` when building an `RpcResponse` from an `EntityBroadcast`**, so clients receiving a broadcast triggered by another client's RPC never saw per-named-stream scroll deltas — named-stream desync detection and `Skip`-catchup on `ServerPatch`/`ServerReplace` were silently broken in multi-client scenarios. Fix copies the field through in both bundling branches

### Changed

- **`ClientDispatcher` rewritten around a new `OrderedDispatcher`** — seq-ordered reassembly, dispatcher ownership, and re-entrant handler bypass (via `AsyncLocal<bool>` propagated through `ExecutionContext`) now live in a single dedicated class. Behavior is preserved (full test suite + 300× broadcast-race loop + re-entrant RPC deadlock regression test all green), but the scattered `lock`/`Volatile`/flag scaffolding is gone; ordering/re-entrancy contract is now readable end-to-end
- `InProcessConnection` simplified — ordering & re-entrancy responsibility lifted out of the transport; it now just delivers batches in the order the server produced them, like the other transports
- **Per-call chatty `[ClientDispatcher]` Debug logs removed** (`SendAndCompleteAsync`, `RPC response`, `HandleBatch`, `Resolved`, `DeliverBroadcast`, `Calling N handlers`). They fired on every RPC and swamped Debug output. Detailed per-request tracing remains available through the existing opt-in `ClientDispatcher.DiagnosticsLog` delegate — assign an `Action<string>` to it (e.g. a file writer) and the internal `LogDiag` channel emits `SEND/RECV/BATCH/CONFIRMED` events for post-mortem analysis

### Added

- **Desync-flow diagnostic logs on the rare desync path** — `[Desync]` entries in `DispatcherNetworkAdapter.SendDesyncReportAsync` (request sent / server response / local failure), `[UnityHttp]` entries in `UnityHttpConnection.SendDesyncReportAsync` (enter / response / failure), `[Handler]` entries in `MetaConnectionHandler.SendDesyncReportAsync` and `SetDebugOptionsAsync`. All at Debug level — zero cost on the happy path, full client → transport → server trace when a desync is being investigated
- `docs/ORDERING.md` and `docs/ORDERING-GUARANTEES.md` — formal documentation of the ordering pipeline: server-side (per-session monotonic sequence, per-entity `EntitySequenceNumber` + `HeldBroadcasts`, RPC response bundling, deferred responses, optional `EnforceRpcOrder`), client-side (seq-ordered reassembly via `OrderedDispatcher`, in-order op dispatch, TCS ↔ RequestId binding), and the transport contract every `IConnection` implementation must honor

### Removed

- `com.coregame.sharedmeta/Runtime/Client/MessageBuffer.cs` (+ `.cs.meta`) — replaced by `OrderedDispatcher`

## [0.12.0] - 2026-04-20

### Added

- **Signal methods — fire-and-forget RPC** — `[MetaMethod(Mode = ExecutionMode.Signal)]` marks a method as a one-way server call. Client generates a synchronous `{Method}Signal(params)` that returns `void` and delegates to `INetwork.SendSignalAsync`; no RequestId tracking, no response on the wire, no auto-retry interaction, no broadcast-suppression side effects. Server routes through a generated `{Service}SignalDispatcher` invoked from the grain's `[OneWay]` `HandleSignalAsync` — read-only execution, no sequence increment, no broadcasts, no persistence. Server-side errors are logged and swallowed (fire-and-forget contract). Primary use cases: heartbeat, telemetry ping, notification via `[ServerMetaService]` bridges
- **`IMetaHub.SignalCall` / `IConnection.SignalCallAsync` / `IMetaProvider.HandleSignalAsync` / `IEntityGrainBase.HandleSignalAsync`** — new transport-to-grain pipeline for signals. All three transports implement it: **InProcess** (direct grain invocation), **SignalR** (`HubConnection.SendAsync` instead of `InvokeAsync` — no wire-level ACK awaited), **HttpPolling** (`POST /meta-http/signal` → `202 Accepted` before execution completes). `ISessionManager.SignalEntityAsync` bridges handler → entity grain
- **`NullPayloadWriter` + `NullServerRecordContext`** — zero-allocation singletons (`Array.Empty<byte>()`, no-op writes) used during signal dispatch. `ServerMetaContext.SignalMode` toggles `Writer` to the null sink so `[ServerMetaService]` bridge Recorders called from inside a signal method body write into /dev/null — real side-effects (HTTP calls, Orleans grain hops) still happen, recording is silently discarded since there is no replay payload to feed back
- **`[OneWay]` on `IEntityGrain.HandleSignalAsync`** — Orleans is told the call is truly one-way; SessionManager grain does not wait for an ACK from the entity grain after handing off the signal
- **Unified `ExecutionMode` enum** — `ExecutionMode.Query` and `ExecutionMode.Signal` joined the existing modes (`Local`/`Optimistic`/`Server`/`CrossOptimistic`/`ServerPatch`/`ServerReplace`). Both Query and Signal change method signature and lifecycle (Query must return a value; Signal must be `void` and never awaits) and cannot coexist with other modes on the same method. Making them first-class enum members eliminates the former bool-flag clash-detection (`Query = true` + `Signal = true` was a `#error`; with the enum it is not expressible). See **Deprecated** below for the migration path from the legacy bool flags

### Changed

- **Runtime override is locked for Query and Signal.** `IExecutionModeProvider.GetMode(...)` short-circuits when the method's declared mode is `Query` or `Signal` — overrides map entries are ignored. `ExecutionModeProvider.SetMode` / `SetServiceMode` additionally throw `ArgumentException` if asked to apply a `Query`/`Signal` override, since these are structural traits that exist only at code-generation time, not routing strategies. Overriding into or out of them would be a silent no-op; the throw makes the misuse explicit
- Signal signature is validated at compile time via `#error` in the generated ApiClient when misused: non-`void` return type, combined with `Mode = ExecutionMode.Query` (same-field impossible; cross-mode collision still checked), explicit non-default `Mode = ...`, or `Sync = ...` each produce a named diagnostic
- `SignalCallRequest` DTO added alongside `RpcCallRequest` / `QueryCallRequest` — wire shape: `EntityId + ServiceName + MethodName + Payload` (no RequestId, no RandomScrollDelta, no response counterpart)

### Deprecated

- **`MetaMethodAttribute.Query` (bool)** and **`MetaMethodAttribute.Signal` (bool)** — marked `[Obsolete]`. Use `Mode = ExecutionMode.Query` or `Mode = ExecutionMode.Signal` respectively. The generator accepts either form for backward compatibility; setting both the legacy bool AND a conflicting explicit `Mode` on the same method produces a `#error` diagnostic. The legacy bool properties will be removed in a future major version

### Migration

- **From `[MetaMethod(Query = true)]`** → `[MetaMethod(Mode = ExecutionMode.Query)]`. Behavior is identical
- **From `[MetaMethod(Signal = true)]`** → `[MetaMethod(Mode = ExecutionMode.Signal)]`. Behavior is identical
- No runtime changes required for consumers; any existing source that uses the bool flags continues to compile with a `CS0618` warning until migrated
- Consumers of `ExecutionModeProvider.SetMode` must not pass `ExecutionMode.Query` or `ExecutionMode.Signal` — the method now throws `ArgumentException`. If you previously had code that tried to apply such an override, it was a silent no-op; remove the call

### Known limitations / TODO

- Compile-time syntactic check for **state mutations inside signal method bodies** is deferred. The runtime contract is "signal methods must not mutate state" — today this is documented but not enforced by the generator (convention-only, same as Query). Planned for a follow-up release as a Roslyn walker over the impl method body
- **Cross-entity calls** from inside a signal body throw `NotSupportedException` (via `NullServerRecordContext.CallEntityAsync`). If you need to chain into another entity, use a regular `Mode = Server` method instead

## [0.11.0] - 2026-04-17

### Added

- **Synchronous client API for Optimistic methods** — new `[MetaMethod(Sync = SyncApi.Generate)]` opt-in generates a sibling `{Method}Sync` overload on the generated `ApiClient` alongside `{Method}Async`. The sync overload returns `T` (or `void`) directly, completes the local mutation in the calling frame, and fires the server round-trip in the background via `ContinueWith`. Target use case: DOTS / main-thread game loops where a profile mutation must be visible in the *very next frame* with no `await` boundary in between
- **`SyncApi` enum** — `None` (default, async only), `Generate` (emit both async and sync overloads), `OnlySync` (emit only the sync overload; per-mode private dispatchers are omitted — the service is effectively locked to Optimistic/Local)
- **`SyncPolicy` enum** — controls runtime behavior when `IExecutionModeProvider` has overridden the effective mode away from Optimistic/Local (e.g. a downloaded config promoted the method to `Server`). `Throw` (default, raises `InvalidOperationException`), `Warn` (logs via `MetaLog.Warning` + `IDesyncDiagnostics.OnSyncPolicyViolation` and executes locally anyway), `Silent` (executes locally, diagnostics callback only)
- **`IDesyncDiagnostics.OnSyncPolicyViolation(serviceName, methodName, effectiveMode)`** — new default-implemented hook fired when `SyncPolicy.Warn` or `Silent` swallows a runtime mode override
- **`[NamedRandom]` — independent deterministic random streams per state** — `[NamedRandom("Combat")]` on a `[SharedState]` class declares a separately-seeded `IMetaRandom` stream; the generator emits a typed `{Name}Random` property on every service `Context` partial for that state. Lets `Combat`, `Loot`, `MapGen` advance independently so adding a wall-placement call does not shift the loot roll. Semantics mirror `Context.Random` (same algorithm and seed on both sides). Optional `Seed = "literal"` pins the stream to a fixed seed across entities
- **`NamedRandomAttribute`** — `AttributeUsage = Class, AllowMultiple = true`. Positional: the index into the generated `Context.NamedRandoms` list follows attribute declaration order on the state. Reordering / adding / removing attributes reseeds the affected slots from the derived seed — acceptable because the value being lost is random state
- **Per-index scroll-delta desync detection** — `RpcResponse.NamedRandomScrollDeltas` and `EntityBroadcast.NamedRandomScrollDeltas` carry a `long[]?` (null when nothing advanced). Client compares per-index and fires `OnRandomDesync` with method name suffixed `[NamedRandom:{i}]`. On `ServerPatch` / `ServerReplace` / broadcast catch-up, client calls `Skip(delta)` per-index to stay in sync
- **`EntityGrainState.NamedRandomsBytes` (Id 7)** — packed positional `MetaRandom[]` persisted alongside `ServerRandomBytes` / `OptimisticRandomBytes`. Transmitted on subscribe via new `NamedRandomsBytes` field on `SubscribeResponse`, `ConnectResponse`, `ResubscribedEntityInfo`, `EntitySnapshot`

### Changed

- `[MetaMethod(Sync = ...)]` with `Mode` other than `Optimistic`/`Local` produces a `#error` in the generated `ApiClient.g.cs` — Roslyn surfaces it as a compile error naming the method
- `[MetaMethod(Sync = ...)]` on a service method whose return type is `Task`/`Task<T>` produces a `#error` — sync generation requires a non-async signature because the local body cannot legitimately `await` and still complete in-frame

### Fixed

- **`IDesyncDiagnostics.OnRandomDesync` could fire false positives when two or more Optimistic methods that advance `Context.Random` (or a `[NamedRandom]` stream) were issued in close succession on the same `ApiClient`**. The generated Optimistic path captured `scrollIdBefore` before local exec but computed `localScrollDelta = _optimisticRandom.ScrollId - scrollIdBefore` **inside** the fire-and-forget `ContinueWith` — by the time that continuation ran, subsequent calls on the same client may have already advanced the shared `_optimisticRandom.ScrollId`, yielding a phantom local delta that didn't match the server's per-call delta. Generator now snapshots `localScrollDelta` (and `localNamedScrollDeltas` for named streams) synchronously right after local exec, so the continuation compares stable captured values against the server response. Affects `{Method}Async_Optimistic`, `{Method}Sync_Optimistic`, and `{Method}Async_CrossOptimistic` paths. No runtime correctness impact — the random state and replay were always consistent; only the diagnostic callback was noisy

### Migration

- No migration required. `Sync` defaults to `SyncApi.None`; existing services continue to generate only the async API, unchanged

### Known limitations / TODO

- On `SyncPolicy.Warn`/`Silent`, the sync body still runs locally even when the effective mode has been overridden to `Server`/`ServerPatch`/`ServerReplace`. A future opt-in may route such calls through a real server round-trip (fire-and-discard local result) for callers that prefer correctness over immediacy when config promotes the method — see `TODO(sync-mode-override)` in `SimplifiedApiClientGenerator.cs`

## [0.10.2] - 2026-04-15

### Fixed

- **`ServerMetaConfiguration.g.cs` referenced `PatchSchema` classes without namespace qualification**, causing `CS0103: The name '{State}PatchSchema' does not exist in the current context` in server projects whose namespace differs from the state type's namespace. Generator now emits fully-qualified `{StateFullName}PatchSchema.Instance` for both `byState` and `byService` registry entries

## [0.10.1] - 2026-04-14

### Added

- **Device reset endpoint** — `POST /meta/auth/reset-device` (`[Authorize]`) force-unlinks a `deviceId` from the current player. After reset, the next login with that device creates a new player profile. Unlike `/unlink`, this works even when the device is the player's only auth key
- **`IAuthGrain.ForceUnlinkAsync()`** — unconditional unlink that clears the auth key→PlayerId mapping and removes the key from the player's `AuthIndexGrain`, without the "last key" safety check
- **`MetaAuth.ResetDeviceAsync()`** — client-side helper. Calls the reset endpoint with the current JWT, optionally clears `ITokenStorage` on success. Works across all platforms (Unity via `UnityMetaAuth`, .NET via `HttpMetaAuthProvider`, local via `LocalMetaAuthProvider`)
- **`IMetaAuthProvider.ResetDeviceAsync()`** — new interface method implemented by all built-in providers

## [0.10.0] - 2026-04-13

### Added

- **Client-side connection health monitoring** — new `IConnectionHealthListener` interface and `ConnectionHealthOptions` notify the game when pending RPC requests exceed configurable timeout thresholds. Two stages: `Slow` (default 1s, show spinner) and `Unresponsive` (default 5s, show modal dialog). Wired via `MetaClientOptions.ConnectionHealth`. Callbacks fire on the game-loop thread from `ProcessPendingBroadcasts()`
- **Client-side auto-retry for lost requests** — `ClientDispatcher` automatically resends all pending requests every `ConnectionHealthOptions.RetryIntervalMs` (default 2s) when the oldest pending request exceeds `SoftTimeoutMs`. This is the primary recovery mechanism for packet loss — fully client-side, no server dependency
- **`MetaClient.ResumeSessionAsync()`** — attempts to restore the current session (same `sessionId`, missed packet recovery) without restarting. Use for "try again" after connection issues. Falls back to `RestartSessionAsync()` if resume fails
- **`ClientDispatcher.LastCompletedRequestId`** — tracks the last request ID that received a real entity response, useful for UI diagnostics (sent vs confirmed)
- **`ClientDispatcher.DiagnosticsLog`** — optional `Action<string>` delegate for request lifecycle tracing. When set, logs all SEND/RECV/CONFIRMED/TRANSPORT_ERROR/AUTO_RETRY/STALL/RESEND events with timestamps. Write to file for post-mortem analysis
- **`DebugConnectionWrapper`** — wraps any `IConnection` to simulate network problems for testing. Configurable added latency (`MinLatencyMs`/`MaxLatencyMs`), packet loss (`PacketLossPercent`), and manual disconnect (`SimulateDisconnect()`/`SimulateTemporaryDisconnectAsync()`). Lives in `Runtime/Core/Transport/`, available to all clients (Unity, Godot, console)
- **`PacketLossMode` enum** — `ConnectionDrop` (full disconnect, realistic for SignalR/TCP) vs `RequestHang` (individual request fails with `HttpRequestException`, realistic for HTTP polling). Set via `DebugConnectionSettings.LossMode`
- **Transport selection in Expedition Unity example** — startup panel lets user choose SignalR or HTTP Polling transport. HTTP polling auto-enables `EnforceRpcOrder` and sets `PacketLossMode.RequestHang`
- **Connection health UI in Expedition example** — red overlay with "Reconnect" button, modal blocker when `Unresponsive`. Request tracking strip shows `Sent: #N  Confirmed: #M  Pending: K`
- **Debug network panel in Expedition example** — Sim ON/OFF, Drop, Metro 3s (temporary disconnect with real server-side session recovery), Latency +/-, Loss +/- controls. Editor/Development builds only

### Changed

- **`ProcessPendingBroadcasts()` runs `CheckConnectionHealth()` first** — health checks and auto-retry now execute every frame regardless of broadcast suppression (`_broadcastSuppressCount`). Previously, pending optimistic RPCs blocked health checks entirely
- **Server-side stall notifications are now lazy** — `SessionManagerGrain` no longer creates periodic grain timers for stall detection. Instead, stall diagnostics are pushed on the next incoming request (if a gap exists) and logged on grain deactivation. Eliminates timer overhead for HTTP polling games that regularly trigger brief out-of-order arrival
- **HTTP polling clients deliver `StallNotification` broadcasts** — poll loop filter in `UnityHttpConnection` and `HttpPollingConnection` now passes through responses with `StallNotification != null` even when `Operations.Count == 0`. Previously, stall notifications were silently dropped as "empty broadcasts"
- **Expedition server enables `EnforceRpcOrder`** — required for HTTP polling transport where wire-level FIFO is not guaranteed. Safe for SignalR (one extra int comparison per in-order call)
- **Expedition server registers HTTP polling endpoint** — `app.MapMetaHttpPolling("/meta-http")` with `HttpPollingConnectionManager` in DI

### Fixed

- **HTTP poll loop dropped `StallNotification` broadcasts** — `UnityHttpConnection.RunPollLoop` and `HttpPollingConnection` filtered broadcasts with `Operations.Count > 0`, which excluded `StallNotification` (out-of-band, zero ops). Server-side stall detection worked but client never received the notifications. Fixed by adding `|| broadcast.StallNotification != null` to the filter
- **`CheckConnectionHealth` never ran during broadcast suppression** — when an optimistic RPC was awaiting replay (`_broadcastSuppressCount > 0`), `ProcessPendingBroadcasts()` returned early before reaching `CheckConnectionHealth()`. Auto-retry and health status updates stopped entirely. Moved health check to run first, before the suppression guard
- **Server stall timer never re-notified after partial drain** — when `EnforceRpcOrder` drained some stashed requests but a gap remained (e.g., #5,#6,#7 drained but #8 still missing), `_lastStallStage` stayed at `TimeoutPending` and the tick callback skipped notifications for the new gap. Replaced timer with lazy diagnostics on next request arrival

## [0.9.4] - 2026-04-11

### Changed

- **`MetaLoginResult` moved from `SharedMeta.Client` to `SharedMeta.Core.Auth` namespace.** The type itself and all its members are unchanged — only its namespace moved. In Unity (single `SharedMeta.Runtime` asmdef) the move is transparent. In the .NET NuGet build (`SharedMeta.Core` + `SharedMeta.Client` as separate assemblies) this fixes a circular reference that was introduced in 0.9.3: `IMetaAuthProvider` / `HttpMetaAuthProvider` in `SharedMeta.Core` could not legally reference `SharedMeta.Client.MetaLoginResult`, so 0.9.3 failed to build on the NuGet side

### Migration

- Code that uses `MetaLoginResult` **unqualified** (via `using SharedMeta.Client;` alone) must **add `using SharedMeta.Core.Auth;`**. Fully qualified references (`SharedMeta.Client.MetaLoginResult`) must be rewritten to `SharedMeta.Core.Auth.MetaLoginResult`. All first-party code in this repo has been updated — the grep for migration is `rg "MetaLoginResult" --type cs`

## [0.9.3] - 2026-04-11

### Added

- **`ICrossEntityResolver.UpdateCachedState(entityId, newState)`** — new interface method that lets generated `ApiClient` code push a fresh state reference into the resolver after `ServerReplace` mode replaces the local `_state`. Implementations should update whatever backing cache maps `entityId → state`. See the fix under **Fixed** below for context
- **`IMetaAuthProvider` interface** — unified extension point for replacing the default HTTP auth flow. Implement `LoginAsync` / `LoginWithPlatformAsync` / `LinkAsync` / `UnlinkAsync` and assign the instance to `MetaAuth.Provider` to route all auth calls through a custom transport (Unity, local backend, Firebase, PlayFab, etc.)
- **`HttpMetaAuthProvider`** — default `IMetaAuthProvider` implementation using `HttpClient`. Used automatically on non-Unity .NET targets when no custom provider is set
- **`MetaAuth.Provider` property** — takes precedence over the legacy Func-based hooks and the built-in HttpClient fallback

### Changed

- **`MetaAuth` priority order**: `Provider` → legacy `LoginFunc`/`PlatformLoginFunc`/`AuthActionFunc`/`UnlinkFunc` → default `HttpMetaAuthProvider` (non-Unity only). Existing `UnityMetaAuth.Register()` callers continue to work unchanged via the legacy-Func fallback
- Legacy `LoginFunc` / `PlatformLoginFunc` / `AuthActionFunc` / `UnlinkFunc` properties documented as legacy — prefer `MetaAuth.Provider` for new code

### Fixed

- **`Client.GetState<T>()` returned stale state after `ServerReplace` mode** — when a `[MetaMethod(Mode = ServerReplace)]` call completed, the generated `ApiClient` replaced its private `_state` field with the server-provided snapshot, but `MetaServiceResolver.EntityConnection.State` still pointed at the original subscribe-time instance. Any code reading via `Client.GetState<T>()` (including UI render paths and cross-entity `GetEntityState` calls from other services) saw the stale state, while `ApiClient.State` saw the fresh one. Fixed by adding `ICrossEntityResolver.UpdateCachedState(entityId, newState)` — the generated ApiClient now pushes the fresh reference into the resolver whenever `_state` is replaced (both in direct RPC responses and in broadcast `StateBytes` / trigger `StateBytes` paths). The resolver's cached `EntityConnection.State` stays in sync, and all read paths see the same live reference

## [0.9.2] - 2026-04-10

### Added

- **`Context.SaveStateAsync()`** — force-persist entity state mid-method for pseudo-transactional patterns. On server: persists state + random bytes to Orleans storage immediately. On client: no-op. Enables safe cross-entity resource transfer where state must be checkpointed before sending acknowledgements

## [0.9.1] - 2026-04-09

### Added

- **Natural property assignment for sub-wrappable fields** — generated `{State}PatchWrapper` now emits a setter alongside the getter for nested-object properties (`Profile`, `Stats`, etc.). Writing `state.RequiredProfile = new Profile { ... }` works directly through the implicit operator instead of requiring the `SetRequiredProfile(...)` helper. Assign-then-mutate in the same call (`state.RequiredProfile = new Profile { ... }; state.RequiredProfile.Level += 5`) is fully supported — the applier unpacks the Value snapshot first, then layers the subsequent field mutations on top
- `PatchNode.MarkChildFullReplace(fieldId, packed)` — convenience method that clears collection state and records a `FullReplace` structural op in one call, used by generated list-field setters
- 411-test `SharedMeta.PatchFuzzTests` project — standalone patch roundtrip suite (no Orleans, no network) covering scalars, sub-objects, nullable transitions, all scalar list ops, element-sub-wrappable list ops with nested Equipment/SkillIds/Tags, dict/hashset, reassign-then-mutate across every collection type, edge cases, and 70 randomized fuzz seeds

### Fixed

- **List field reassignment drops subsequent mutations** — generated list-field setters (`state.Heroes = newList`) wrote a terminal `Value` on the patch node; any `Add`/`Set`/`Insert` in the same call appended structural ops to the same node, but the receiver's early-return on `Value` silently dropped them. Setter now records a `FullReplace` structural op instead, keeping the invariant "list nodes never carry terminal Value". Subsequent mutations chain in submission order
- **`CollectionPatchApplier` early return on Value** — when a collection node had both `Value` and `StructuralOps` (which could happen with legacy patches or edge cases), the applier returned after unpacking Value without processing the ops. Now applies Value first, then continues with structural ops and element children
- **`PatchTextRenderer` diff hides divergent collection fields** — the skip condition `both terminals equal → skip` didn't check for `StructuralOps` or `Children` layered on top of the matching Value. Fields with identical Value snapshots but divergent ops were silently omitted from desync reports. Now requires empty Children and StructuralOps on both sides to skip
- **SubWrappable applier doesn't handle Value+Children** — `SetX(newObj)` followed by `state.X.Field = Y` in the same call produced a node with both Value and Children; the generated applier used `if/else` (terminal vs recurse). Changed to sequential: unpack Value first, then recurse into Children if present

## [0.9.0] - 2026-04-08

### Added — granular list patches

- **Element-sub-wrappable lists** — `[MetaServiceImpl(DeepDesync = true)]` services can now define state with `List<T>` where `T` itself has `[MemoryPackOrder]`/`[Id]`/`[Key]` properties. The generator emits a specialized `{Element}PatchableList` nested class that hands out per-element `{Element}PatchWrapper` instances bound to per-element subtree nodes in the patch tree. Mutations like `state.Heroes[5].Exp += 100` now flow into `Heroes/[5]/Exp` instead of dumping the whole list. Two-level nesting works end-to-end (`state.Heroes[i].Equipment.Add(item)`)
- **Compile-time tracking guard via type system** — every generated `{State}PatchWrapper` exposes a one-way `static implicit operator {Wrapper}({State}?)` that produces an untracked-proxy wrapper from raw state. Combined with the absence of any reverse operator, the C# type system enforces that helper methods exposing collection elements **must** be typed as `{Element}PatchWrapper`. Returning raw `{Element}` from a helper compiles fine in the regular service class but fails the generated `_PatchTracked` copy with a clear `CS0029` error pointing at the offending line. Silent loss of patch tracking is now a compile error
- **Granular structural ops for `List<T>`** — `Add`/`Insert`/`RemoveAt`/`Remove`/`Clear`/`Set` are recorded as individual `PatchListOp` entries on the collection node's new `StructuralOps` list, not as full snapshots. Same applies to scalar lists (`List<byte> Cells`, `List<int>`, etc.) — they now use the same op-based representation. `Sort`/`Reverse`/`AddRange`/`InsertRange`/`RemoveRange`/`RemoveAll` fall back to a `FullReplace` op
- **Index shifting for sub-wrappable element children** — when `Insert` or `RemoveAt` reshape a list that has pending element-subtree mutations, the sender automatically shifts the affected child indices via `PatchNode.ShiftElementChildren`. The receiver applies structural ops in order and looks up element children at their canonical post-op indices, so mixed structural+element mutations in one call (e.g. `MixedShift`: `hero.Exp += 500; heroes.RemoveAt(otherIdx);`) work correctly
- `PatchListOp` / `PatchListOpKind` types in `SharedMeta.Core.Patch` (`Insert`, `RemoveAt`, `Set`, `Clear`, `FullReplace`)
- `CollectionPatchApplier.Apply<T>(...)` runtime helper used by generated `{State}PatchApplier` for collection fields. Handles all three apply paths (terminal full-replace, structural ops, element subtrees) in one call
- `PatchTextRenderer` updated for collection nodes — renders `ops`/`elements` sections separately so diagnostic JSON shows the actual structural ops alongside per-element changes
- 10 `ElementSubWrappableTests` + new `PartyState`/`Hero`/`Item` test surface in `SharedMeta.Test.Meta1` covering AddHero, AwardExp via wrapper helper, BatchUpdate (two element subtrees), Insert/RemoveAt/Set/Clear ops, two-level `AddItemToHero`, two-level element mutation `UpgradeItem`, and the mixed-shift case

### Changed — wire format break

- **`PatchNode` wire format extended**: new `PatchChildKind Kind` field (`Field` | `ElementByIndex`), `FieldId` widened from `int` to `long`, new `StructuralOps: List<PatchListOp>?` field. Patches between 0.8.0 and 0.9.0 are not interoperable. Both client and server must upgrade together
- `IPatchSchema.GetFieldName` / `DecodeLeaf` / `GetNestedSchema` now take `long fieldId` (was `int`)
- `IPatchSchemaRegistry` and generated `{State}PatchSchema` updated to match
- `PatchNodeDiffer` keys child entries by `(Kind, FieldId)` so element children and field children no longer collide on the same dictionary key
- Generated `{State}PatchSchema` for state types containing element-sub-wrappable list fields now also emits the element type's schema as a sibling class, and `GetNestedSchema` resolves the collection field id to that element schema so the renderer can descend into element subtrees

### Fixed

- **Nullable type dedupe in `PatchSchemaGenerator`** — `Card?` and `Card` referenced from different parents previously emitted two competing `CardPatchSchema` classes in the same nested namespace. Now nullable annotations are stripped during sub-type collection, and the dedupe set is shared across all recursion levels

## [0.8.0] - 2026-04-08

### Added

- **Server-side RPC reordering** — `SessionManagerGrain` now optionally guarantees that RPC calls reach entity grains in monotonically increasing `RequestId` order, regardless of the threadpool / transport delivery order. New `SessionManagerOptions.EnforceRpcOrder` (default `false`) opts the gate in. The session manager parks out-of-order calls in a fixed-capacity ring buffer (`SessionManagerOptions.StashCapacity`, default 256), drains them in order when the missing predecessor arrives, and bundles all results into one `SessionResponse`. The client side requires no changes — `ClientDispatcher` already matches operations to pending TCS by `RequestId`. Required defense-in-depth for transports that don't preserve submission order at the wire level (HTTP polling, custom UDP, any pipeline with intermediate `Task.Run`); SignalR over a single hub connection works fine without it
- `RpcOrderingBuffer<T>` — generic, allocation-free ring buffer with `Classify` / `TryStash` / `TryDequeueNext` / `MarkDispatchedInOrder` / `Reset` API. Lives in `SharedMeta.Server.Core.Session` and is the only ordering primitive `SessionManagerGrain` knows about. 13 focused unit tests cover overflow, head wraparound, duplicate / stale handling, and reset
- **Session health stall notifications** — when an RPC ordering gap stays open beyond `SessionManagerOptions.SoftStallNotifyTimeout` (default 500 ms), the server pushes a `StallNotification` through the existing observer channel as a new `SessionResponse.StallNotification` field (with empty `Operations`). At `HardStallNotifyTimeout` (default 10 s) it pushes a second notification with `StallStage.TimeoutPending`. When the gap closes a `Recovered` notification follows. The hard upper bound is `MaxStallDuration` (default 5 minutes), after which the session is terminated and the client must reconnect
- `ISessionHealthListener` (in `SharedMeta.Core.Diagnostics`) — client-side interface with `OnSessionStalled(StallNotification)` and `OnSessionRecovered(StallNotification)`. Wired through `MetaClientOptions.SessionHealth`. `ClientDispatcher.ProcessServerResponse` short-circuits stall-only batches into the listener without touching the broadcast buffer or pending requests
- `SessionManagerOptions` — new DI-configurable options class controlling reordering, stall thresholds, stash capacity, duplicate-stash log level, and the stall timer tick interval
- Stash duplicate logging level (`SessionManagerOptions.DuplicateStashLogLevel`) — `Debug` / `Information` / `Warning` / `None`
- 4 new `SessionOrderingTests` — direct `ISessionManager` integration tests for in-order drain, stalled→recovered notification cycle, stash overflow → terminate session, and full stall stage progression (`Stalled` → `TimeoutPending` → `Recovered`)
- 1 new `DeepDesyncTests.ConcurrentDeterministicCalls_NoPatchDesync_StressGate` — fires 50 sequential Optimistic RPCs through a real client to verify the reordering gate prevents the threadpool-induced reordering that previously caused phantom patch desyncs

### Fixed

- **Concurrent Optimistic RPCs no longer trigger phantom patch desyncs** — when a client made several Optimistic RPCs in quick succession (`await api.AddAsync(10); await api.AddAsync(20);`), the threadpool could deliver them to the entity grain in the wrong order, causing the deep-desync CRC comparison to fail even though the local execution was correct. Fixed at the architectural level by adding the optional server-side RPC reordering gate described above. Production transports with native FIFO guarantees (single SignalR hub connection) are unaffected; HTTP polling and similar transports must opt in via `SessionManagerOptions.EnforceRpcOrder = true`

## [0.7.0] - 2026-04-04

### Added
- **Deep desync detection** — `[MetaServiceImpl(DeepDesync = true)]` enables field-level state mutation tracking. Generator produces a `_PatchTracked` service copy where `State` routes through `PatchWrapper`, recording all mutations into a PatchNode tree. Server computes FNV-1a CRC after each call; client compares local CRC. Catches state divergence even when return values match
- `PatchTrackedClassGenerator` — copies full service class via Roslyn SyntaxTree with `State` → `PatchWrapper` substitution
- `PatchCrc` — FNV-1a hash for patch comparison
- `PatchNodeDiffer` — field-by-field diff of two PatchNode trees
- `IPatchWrapper` interface, `DeepDesyncReport`, `IDesyncReportSink`
- `IDesyncDiagnostics.OnPatchDesync` — fires on patch CRC mismatch
- `RpcResponse.DeepDesyncCrc` — server → client CRC field
- `EntityGrainOptions.DeepDesyncEnabled` — global runtime override (null = per-service, true = force on, false = force off)
- `PatchableList<T>`, `PatchableDictionary<K,V>`, `PatchableHashSet<T>` — full API parity with base collections + implicit conversion
- **Debug API** — `MetaClient.SetDeepDesyncAsync()` toggles deep desync per-session from client. `MetaTransportOptions.AllowDebugApi` gates on server (default off)
- **Server-side desync logging for all mismatch kinds** — Result mismatches (Server execution mode) and Random scroll mismatches (Optimistic / CrossOptimistic modes) now also flow to the server through `SendDesyncReportAsync`. Gated by the same `MetaTransportOptions.DesyncReportingEnabled` option as patch reports. New `DesyncMismatchKind` flags enum (Patch | Result | Random); `DesyncReportRequest` and `DeepDesyncReport` carry the kind plus per-kind payload (server/client result bytes or scroll deltas). No server-side cache is needed for Result/Random — both values are sent inside the request
- **Patch schema + JSON renderer for desync diagnostics** — `IPatchSchema` interface generated alongside each `PatchWrapper` (`{State}PatchSchema.g.cs`) maps field ids to property names and decoder types. `PatchTextRenderer.ToJson` visualizes a single patch and `PatchTextRenderer.DiffToJson` produces a side-by-side `{ "server": ..., "client": ... }` JSON of two diverged patches. `IPatchSchemaRegistry` is registered automatically by `ConfigureMeta()` and looked up by service name in `MetaConnectionHandler.SendDesyncReportAsync`. Zero overhead in normal RPC flow — schemas are only consulted when a desync report is being formatted. Falls back to hex if no registry is wired up
- Expedition example: `GenerateNewMapBroken` is now mostly deterministic — it generates the full map via `Context.Random` and then corrupts only 5–15 cells with `System.Random`, so the patch diff JSON shows a small focused divergence instead of two completely different maps
- Expedition example: `GenerateNewMapBroken()` with `System.Random`, 3-button generation UI, server `--desync` flag, client-side toggle button

### Fixed
- `ServerMetaConfigurationGenerator`: assembly scan no longer skips user assemblies with `SharedMeta` prefix
- Test infrastructure: `Test.Server` uses project reference + fully generated server configuration
- **Deep desync runtime toggle now actually works** — `[MetaServiceImpl(DeepDesync = true)]` previously hardcoded `provider.DeepDesyncEnabled = true` in the generated factory, so the per-session `SetDebugOptions` toggle and `EntityGrainOptions.DeepDesyncEnabled = false` were ignored (server always computed CRCs). The compile-time flag now only generates the supporting infrastructure (PatchTracked service copy, PatchSchema). Runtime activation is fully opt-in via `EntityGrainOptions.DeepDesyncEnabled` (global) or client-side `SetDebugOptions(true)` (per session)

## [0.6.0] - 2026-04-03

### Added
- **Platform authentication** — `IExternalAuthValidator` interface for pluggable platform token validation. Server validates tokens via platform APIs, returns stable platform user ID. Three validator packages:
  - `CoreGame.SharedMeta.Auth.Google` — Google Play Games (server auth code → OAuth2 token exchange)
  - `CoreGame.SharedMeta.Auth.Apple` — Sign in with Apple (JWT identity token → JWKS verification)
  - `CoreGame.SharedMeta.Auth.Steam` — Steam (session ticket → Steam Web API validation)
- **Account linking** — `POST /meta/auth/link` [Authorize] associates a platform with the current player. `POST /meta/auth/unlink` [Authorize] removes an auth key (device or platform). Safety: cannot unlink the last key. Conflict detection: link fails if platform already bound to a different player
- **Platform login** — `POST /meta/auth/login-platform` authenticates via platform token without device ID. Same flow as device login but keyed on `{platform}:{platformUserId}`
- `GET /meta/auth/keys` [Authorize] — list all auth keys linked to the current player
- `IAuthIndexGrain` — per-player index of linked auth keys (device IDs + platform keys)
- `AuthGrain.LinkAsync`, `UnlinkAsync`, `GetPlayerIdAsync` — grain-level link/unlink operations
- Client: `MetaAuth.LoginWithPlatformAsync`, `MetaAuth.LinkAccountAsync`, `MetaAuth.UnlinkAsync` — cross-platform client methods (Unity + .NET)
- DI extensions: `AddMetaAuthGoogle()`, `AddMetaAuthApple()`, `AddMetaAuthSteam()`
- Integration tests: 12 auth tests covering device login, platform login, link, unlink, conflict detection, index tracking, full migration flow

## [0.5.2] - 2026-03-28

### Fixed
- BestHTTP transport asmdef: `HAS_BESTHTTP` now auto-defined via `versionDefines` when `com.tivadar.besthttp` package is installed — no longer requires manual scripting define or Wizard re-generation

## [0.5.1] - 2026-03-26

### Fixed
- Generator: Query methods (`[MetaMethod(Query = true)]`) no longer emit unused `On{Method}_Replayed` events in API client
- Unity transport: `QueryCallAsync` implemented in `BestHttpSignalRConnection`, `BestHttpPollingConnection`, `SignalRConnection`, and `UnityHttpConnection`

### Added
- Query methods available in `ApiClient` as local-only calls — when entity is already subscribed, `ApiClient.IsActive()` executes synchronously on client state without network call; `QueryApi` remains for unsubscribed entities

### Changed
- Expedition example: `ResumeOrStartExpedition` replaced with client-side Query flow — checks expedition status via `QueryApi.IsActiveAsync()` before subscribing; new expedition offers generation mode choice (ServerReplace vs Optimistic)

## [0.5.0] - 2026-03-22

### Added
- **Query Calls** — call any entity method without subscribing. Mark methods with `[MetaMethod(Query = true)]` for read-only access, add `OpenAccess = true` to bypass EntityAccessPolicy. Generated `{Service}QueryApi` class provides typed query methods bound to an entityId. Routes through `SessionManager.QueryEntityAsync` for consistent request flow. No state sync, no broadcasts, no persistence.
- `QueryCallRequest` / `QueryCallResponse` — new transport packets for query calls (SignalR, HttpPolling, InProcess)
- `QueryClientGenerator` — source generator for `{Service}QueryApi` client classes
- **`ExecutionMode.ServerReplace`** — new execution mode where server executes the method and sends full serialized state to the client. Client replaces state wholesale instead of replaying or patching. Efficient when state is fully regenerated (e.g., map generation) and full state is smaller than a patch diff. Broadcasts carry full state to all subscribers.
- **`Context.GetState<T>(entityId)`** — read-only cross-entity state access from shared code. System-level method on `MetaContext`, no explicit dependency injection required. Server calls target grain via `[AlwaysInterleave]` method (deadlock-safe), records bytes for deterministic client replay. Returns `null` if entity type is unknown.
- `IEntityGrainBase.GetEntityStateAsync()` — `[AlwaysInterleave]` grain method for read-only state serialization (no sequence increment, no broadcasts)
- `RpcResponse.StateBytes` / `EntityBroadcast.StateBytes` — full state delivery through the transport pipeline
- Integration tests for ServerReplace (3 tests) and GetState (3 tests)
- Documentation: ServerReplace mode section and GetState section in GUIDE.md

## [0.4.5] - 2026-03-15

### Fixed
- `EnsureSessionConnected` — guard now uses `IsSessionConnected` (checks `PlayerId.Length > 0`) instead of null check, preventing `ArgumentException` when Orleans receives empty string as grain key
- **SignalR connection stability** — removed `IsDevelopment()` guard on server-side SignalR timeouts; `ClientTimeoutInterval` and `KeepAliveInterval` now apply in all environments (Server Runner launches in Production mode by default, causing 30-second disconnects)

### Added
- **BestHTTP SignalR reconnect and keep-alive** — `BestHttpSignalRConnectionOptions` gains `PingInterval` (default 15s), `ReconnectDelays`, and `MaxReconnectAttempts`; adapter now configures `HubOptions.PingInterval` and `DefaultRetryPolicy` for automatic reconnect
- **Server Runner improvements** — explicit light text color in dark theme for readability; Copy button copies visible (filtered) log to clipboard
- **Documentation** — Quick Start (5 Minutes) section at the top of GUIDE.md; Expedition Unity client example (section 25); Common Pitfalls section (static state, non-deterministic collections, DateTime.Now, missing `partial`); Asset Store publication data

## [0.4.3] - 2026-03-15

### Added
- **Service error state** — generated API clients catch exceptions during shared method execution at the framework level:
  - `HasError` / `ErrorException` — check if the service is in error state
  - `OnServiceError` event — fires with `(serviceName, exception)` on any service method failure
  - `ClearError()` — manually clear error state to resume normal operation
  - `ServiceErrorStateException` — thrown when calling methods on an error-state service
  - Errors auto-logged via `MetaLog.Error` before re-throwing
  - Error state auto-cleared on reconnect (`RefreshState`)

## [0.4.2] - 2026-03-14

### Fixed
- CrossOptimistic cross-entity calls: `LocalEntityCaller` now propagates entity `Config` to the `CrossOptimisticMetaContext`, fixing `NullReferenceException` when cross-entity service methods access `Config` properties (e.g. energy regen, limits)

### Added
- `ICrossEntityResolver.GetEntityConfig(entityId)` — config resolution for cross-entity calls during client-side optimistic execution

## [0.4.1] - 2026-03-14

### Changed
- `PlayerPrefsTokenStorage` — keys now isolated by `Application.identifier` (bundle ID), preventing token conflicts between multiple SharedMeta projects on the same device

## [0.4.0] - 2026-03-14

### Added
- **Push-based change tracking** — `[Tracked]` attribute on private backing fields generates public properties with tracking setters
  - `ChangeTracker` — `AsyncLocal` change buffer, activated per method call, pooled for zero allocation
  - `ChangeNode` struct — flat-list tree of field changes with parent/child indices
  - `ChangeValue` — discriminated union avoiding boxing for int/long/float/double/bool/string
  - `ListPool<T>` and `ObjectPool<T>` — simple pools for change node lists and wrapper views
  - Client-only: `ChangeTracker.Current` is null on server (zero overhead)
- `ReactiveStateGenerator` — generates `TrackingProperty` enum, tracking property setters, and `Tracked{State}` static subscription classes per project
- Generated API clients now wrap method execution in `ChangeTracker.Activate()`/`FlushAndNotify()` — broadcasts, optimistic replay, and ServerPatch replay all fire tracked field subscriptions
- `OnStateMutated` event on generated API clients — fires after any state mutation (broadcast replay, subscriber event, reconnect)
- `GetEntityConfig<TConfig>(entityId)` on `MetaClient` and `IMetaServiceResolver` — access resolved server config from client code
- Push-Based Change Tracking section in GUIDE.md and SharedMeta-AI.md

### Changed
- Generated broadcast deserialization always uses `CreateReader` for correct length-prefixed format (fixes edge case with single-param broadcasts)

## [0.3.5] - 2026-03-12

### Added
- `MetaAuth` — cross-platform authentication helper with `LoginAsync` and `EnsureAuthenticatedAsync`
- `ITokenStorage` interface for persisting auth tokens across sessions
- `PlayerPrefsTokenStorage` (Unity) — `ITokenStorage` implementation using PlayerPrefs
- `CachedToken` — token data with automatic expiry validation (5-minute safety margin)
- `UnityMetaAuth` — Unity login via `UnityWebRequest`, auto-registered via `[RuntimeInitializeOnLoadMethod]`
- `SharedMeta.Auth.Client` asmdef — separate assembly for Unity-dependent auth code (`noEngineReferences: false`)
- Project Wizard: generated client uses `MetaAuth.EnsureAuthenticatedAsync` with `PlayerPrefsTokenStorage` when auth is enabled

## [0.3.4] - 2026-03-12

### Security

- SessionConnect claim resolution: now checks both `sub` and `ClaimTypes.NameIdentifier` (covers ASP.NET Identity and OIDC providers that remap standard JWT claims)
- SessionConnect no longer falls back to client-supplied PlayerId when user is authenticated but no identity claim found — returns an error instead
- `MetaTransportOptions.RequireAuthentication` — rejects anonymous connections at SessionConnect for both SignalR and HTTP Polling transports
- Project Wizard now registers `MetaTransportOptions { RequireAuthentication = true }` when auth is enabled

## [0.3.3] - 2026-03-11

### Fixed
- MetaInit version migration: all services now receive the original entity version instead of cascading updates; the entity version is set to the maximum across all services

## [0.3.2] - 2026-03-08

### Changed
- `MetaHub.SessionConnect` — now `virtual` for custom authentication/session logic in subclasses
- `MetaHub.GetOrCreateHandler()` — now `protected` (was `private`)
- `MetaHub.GetHandler()` — now `protected` (was `private`)
- `BestHttpPollingConnection.PostAsync<T>()` and `PostRawAsync()` — now `protected` (was `private`)

### Added
- `BestHttpSignalRConnection.Hub` — protected getter for the underlying BestHTTP `HubConnection` (ext-service adapters)
- `BestHttpPollingConnection.Options` — protected getter for connection options (ext-service adapters)

## [0.3.1] - 2026-03-06

### Added
- `[MetaConfig]` attribute for static game configuration
- `IMetaConfigProvider<TConfig>` — server-side versioned config provider
- `MetaConfigVersion` struct (Major.Minor) with full serialization support (MemoryPack, MessagePack, Orleans)
- `IConfigVersionResolver` — optional DI service for A/B tests and gradual config rollouts
- `IConfigDownloadUrlResolver` — generated resolver for config download URLs
- Config version pinning per entity in `EntityGrainState.ConfigVersion`
- `IMetaConfigCache` and `IMetaConfigDownloader` client-side interfaces for config caching/downloading
- `GetConfigDownloadUrlAsync` RPC for on-demand config URL resolution
- Static Game Configuration section in GUIDE.md and all user-facing docs

### Changed
- `IMetaConfigProvider.GetConfig` now takes `MetaConfigVersion` instead of `string entityId`
- `IMetaConfigProvider.Version` renamed to `CurrentVersion`
- Config download URL API changed from `(entityId, stateTypeName)` to `(stateTypeName, MetaConfigVersion)`
- `EntityGrainState` and `PersistedSubscriberInfo` now use `[MemoryPackable(GenerateType.VersionTolerant)]` for backward-compatible persistence
- Wizard generates `[MemoryPackable(GenerateType.VersionTolerant)]` for state classes
- All documentation updated: state classes should use `GenerateType.VersionTolerant` with MemoryPack
- `[MetaInit]` docs corrected: `Context.Random`, `Context.ServerRandom`, and `Config` are all available during init

## [0.3.0] - 2026-03-06

### Added
- `[MetaInit]` attribute for state initialization/migration on grain activation
- `EntityGrainState.Version` for tracking state migration version
- `_isDirty` persistence guard — grains not persisted unless players interact

### Changed
- Unified persistence: `PersistIfNeeded` in `finally` blocks across all `EntityGrain.Handle*` methods
- Removed force-persist from error catch blocks

## [0.2.0] - 2026-03-03

### Added
- Client-only NuGet transport packages for Godot / .NET clients:
  - `CoreGame.SharedMeta.Transport.SignalR.Client` — SignalR with JSON protocol (no server deps)
  - `CoreGame.SharedMeta.Transport.HttpPolling.Client` — HTTP polling with HttpClient (no server deps)
  - `CoreGame.SharedMeta.Transport.SignalR.MessagePack` — optional MessagePack protocol extension
- `SignalRConnection` `configureBuilder` callback for custom protocol configuration
- Unity BestHTTP transport adapters:
  - `BestHttpSignalRConnection` — SignalR transport via BestHTTP plugin
  - `BestHttpPollingConnection` — HTTP polling transport via BestHTTP plugin

### Fixed
- Subscriber broadcast dispatchers now call service methods to update client state (not just fire events)

### Changed
- Server-side `SharedMeta.Transport.SignalR` refactored to use `SharedMeta.Transport.SignalR.MessagePack` for protocol extensions

## [0.1.0] - 2026-03-03

### Added
- Core framework with `[MetaService]`, `[MetaMethod]`, `[MetaServiceImpl]` attributes
- Source generator for dispatchers, API clients, context injection, and service discovery
- Execution modes: Optimistic, Server, Local, CrossOptimistic, ServerPatch
- `MetaClient` — client-side connection manager with state subscription and RPC
- Deterministic random: `Context.Random` (optimistic xoshiro128**) and `Context.ServerRandom` (server-recorded)
- `ServerTimeTicks` — synchronized server time for deterministic time-based mechanics
- State patching with `PatchNode`, `PatchableList<T>`, `PatchableDictionary<K,V>`
- Cross-entity calls via `Context.CallEntityAsync`
- Triggers and subscribers for reactive event handling
- MemoryPack serializer (`SharedMeta.Serialization.MemoryPack`)
- MessagePack serializer (`SharedMeta.Serialization.MessagePack`)
- SignalR transport (`SharedMeta.Transport.SignalR`)
- HTTP polling transport (`SharedMeta.Transport.Http`)
- InProcess transport for testing
- Orleans stubs for Unity compatibility
- Unity Editor tools
