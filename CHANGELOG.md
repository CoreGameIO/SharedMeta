# Changelog

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

- **Natural property assignment for sub-wrappable fields** — `{State}PatchWrapper` now emits setters for nested-object properties. `state.RequiredProfile = new Profile { ... }` works via implicit operator. Assign-then-mutate in the same call is supported (applier unpacks Value first, then applies Children mutations)
- `PatchNode.MarkChildFullReplace(fieldId, packed)` — clears collection state + records `FullReplace` op in one call
- 411-test `SharedMeta.PatchFuzzTests` project — standalone patch roundtrip suite covering all field types, collection ops, nested mutations, and 70 randomized fuzz seeds

### Fixed

- **List field reassignment drops subsequent mutations** — list-field setters now record `FullReplace` structural ops instead of terminal Value, so `state.Heroes = new List(); state.Heroes.Add(...)` works correctly
- **`CollectionPatchApplier` early return on Value** — no longer drops structural ops when Value is present on the same node
- **`PatchTextRenderer` diff hides divergent collection fields** — skip condition now checks StructuralOps/Children, not just Value equality
- **SubWrappable applier `if/else` on terminal vs children** — changed to sequential Value-then-Children so `SetX()` + mutate-via-wrapper works

## [0.9.0] - 2026-04-08

### Added — granular list patches

- **Element-sub-wrappable lists** — services with `[MetaServiceImpl(DeepDesync = true)]` can define state with `List<T>` where `T` has `[MemoryPackOrder]`/`[Id]`/`[Key]` properties. The generator emits a specialized `{Element}PatchableList` nested class whose indexer hands out per-element `{Element}PatchWrapper` bound to per-element subtree nodes. Mutations like `state.Heroes[5].Exp += 100` flow into `Heroes/[5]/Exp`, not a full collection snapshot. Two-level nesting works (`state.Heroes[i].Equipment.Add(item)`)
- **Compile-time tracking guard via type system** — every `{State}PatchWrapper` has a one-way `static implicit operator {Wrapper}({State}?)`. With no reverse operator, the type system enforces that helper methods exposing collection elements **must** return `{Element}PatchWrapper`. Returning raw `{Element}` from a helper compiles in the regular service class but fails the generated `_PatchTracked` copy with `CS0029`. Silent loss of patch tracking is now a compile error
- **Granular structural ops for `List<T>`** — `Add`/`Insert`/`RemoveAt`/`Remove`/`Clear`/`Set` are recorded as individual `PatchListOp` entries on the collection node's `StructuralOps` list. Same for scalar lists (`List<byte>`, `List<int>`). Sort/Reverse/AddRange fall back to `FullReplace` ops
- **Index shifting** — when `Insert`/`RemoveAt` reshape a list with pending element-subtree mutations, the sender shifts the affected child indices via `PatchNode.ShiftElementChildren`. Mixed structural+element mutations in one call work correctly
- `PatchListOp` / `PatchListOpKind` types in `SharedMeta.Core.Patch`
- `CollectionPatchApplier.Apply<T>(...)` runtime helper for generated appliers
- `PatchTextRenderer` updated for collection nodes — renders `ops`/`elements` sections separately
- 10 `ElementSubWrappableTests` + new `PartyState`/`Hero`/`Item` test surface in `SharedMeta.Test.Meta1`

### Changed — wire format break

- **`PatchNode` wire format extended**: new `PatchChildKind Kind` field, `FieldId` widened to `long`, new `StructuralOps: List<PatchListOp>?` field. Patches between 0.8.0 and 0.9.0 are not interoperable. Both client and server must upgrade together
- `IPatchSchema` interface uses `long fieldId` (was `int`)
- `PatchNodeDiffer` keys children by `(Kind, FieldId)` to avoid collisions between field children and element children

### Fixed

- **Nullable type dedupe in `PatchSchemaGenerator`** — `Card?` and `Card` referenced from different parents previously emitted two competing `CardPatchSchema` classes in the same scope. Now nullable annotations are stripped and the dedupe set is shared across all recursion levels

## [0.8.0] - 2026-04-08

### Added

- **Server-side RPC reordering** — `SessionManagerGrain` optionally guarantees that RPC calls reach entity grains in monotonically increasing `RequestId` order, regardless of threadpool / transport delivery scheduling. New `SessionManagerOptions.EnforceRpcOrder` (default `false`) opts in. The session manager parks out-of-order calls in a fixed-capacity ring buffer and drains them inline when the missing predecessor arrives, bundling all results into one `SessionResponse`. Required for transports that don't preserve submission order at the wire level (HTTP polling, custom UDP, anything with intermediate `Task.Run`); SignalR over a single hub connection works fine without it. Client side is unchanged — it already matches operations to pending TCS by `RequestId`
- `RpcOrderingBuffer<T>` — generic, allocation-free ring buffer with `Classify` / `TryStash` / `TryDequeueNext` / `MarkDispatchedInOrder` / `Reset` API in `SharedMeta.Server.Core.Session`. Used by `SessionManagerGrain` as the only ordering primitive. Covered by 13 focused unit tests (overflow, head wraparound, duplicate / stale, reset, mixed in-order/out-of-order)
- **Session health stall notifications** — when an RPC ordering gap stays open beyond `SoftStallNotifyTimeout` (default 500 ms), the server pushes a `StallNotification` through the observer channel as a new `SessionResponse.StallNotification` field. At `HardStallNotifyTimeout` (default 10 s) a second `StallStage.TimeoutPending` notification is pushed. Recovery emits a final `StallStage.Recovered` notification when the gap fills. After `MaxStallDuration` (default 5 minutes) the session is terminated and the client must reconnect
- `ISessionHealthListener` (in `SharedMeta.Core.Diagnostics`) — client-side interface with `OnSessionStalled` / `OnSessionRecovered`, wired through `MetaClientOptions.SessionHealth`. `ClientDispatcher.ProcessServerResponse` routes stall-only batches directly to the listener without touching the broadcast buffer or pending requests
- `SessionManagerOptions` — new DI-configurable options class. Fields: `EnforceRpcOrder`, `StashCapacity`, `SoftStallNotifyTimeout`, `HardStallNotifyTimeout`, `MaxStallDuration`, `StallTickInterval`, `DuplicateStashLogLevel`
- 4 new `SessionOrderingTests` (direct `ISessionManager` integration tests): in-order stash drain, stalled → recovered notification cycle, stash overflow → terminate session, full stall stage progression
- 1 new `DeepDesyncTests.ConcurrentDeterministicCalls_NoPatchDesync_StressGate` — fires 50 sequential Optimistic RPCs through a real client to verify the reordering gate prevents the threadpool-induced reordering that previously caused phantom patch desyncs

### Fixed

- **Concurrent Optimistic RPCs no longer trigger phantom patch desyncs** — two consecutive Optimistic calls (`await api.AddAsync(10); await api.AddAsync(20);`) could race on the threadpool and reach the entity grain in the wrong order, causing the deep-desync CRC comparison to fail even though local execution was correct. Fixed at the architectural level by the optional server-side RPC reordering gate. Production SignalR transports are unaffected; HTTP polling and similar transports should set `SessionManagerOptions.EnforceRpcOrder = true`

## [0.7.0] - 2026-04-04

### Added

- **Deep desync detection** — field-level state mutation tracking via patch CRC comparison. `[MetaServiceImpl(DeepDesync = true)]` generates a `_PatchTracked` copy of the service class where `State` routes through `PatchWrapper`. Server computes FNV-1a CRC of serialized PatchNode after each call; client compares its local CRC. Detects state divergence even when return values match. `IDesyncDiagnostics.OnPatchDesync` fires on mismatch
- `PatchTrackedClassGenerator` — Roslyn generator that copies full service class with `State` → `PatchWrapper` substitution. Skips `[MetaInit]` and `GenerateClientApi = false` methods
- `PatchCrc` — FNV-1a hash utility for patch byte comparison
- `PatchNodeDiffer` — field-by-field diff of two PatchNode trees for detailed desync reports
- `IPatchWrapper` — interface for accessing PatchNode from generated PatchWrapper classes
- `DeepDesyncReport` + `IDesyncReportSink` — desync report model and storage interface
- `RpcResponse.DeepDesyncCrc` — nullable uint CRC in server response (null = disabled)
- `EntityGrainOptions.DeepDesyncEnabled` — global runtime override (null/true/false) applied at grain activation
- Server `ServerMetaConfigurationGenerator`: generated provider dispatches to `_PatchTracked` service when PatchWrapper active; generated factory sets `DeepDesyncEnabled = true` for `[DeepDesync]` services
- Client `SimplifiedApiClientGenerator`: generated ApiClient creates `_patchTrackedService`, activates PatchNode before execution, computes local CRC, compares in Server and Optimistic modes
- `PatchableList<T>`, `PatchableDictionary<K,V>`, `PatchableHashSet<T>` — full API parity with base collection types + implicit conversion from base types
- Integration tests: 3 deep desync tests (deterministic OK, non-deterministic detected, mixed)
- Test infrastructure: `Test.Server` now uses project reference + fully generated `ServerMetaConfiguration` (no manual providers)
- **Debug API** — `IConnection.SetDebugOptionsAsync` / `MetaClient.SetDeepDesyncAsync` — client toggles deep desync per-session at runtime. Server gates via `MetaTransportOptions.AllowDebugApi` (default false, safe for production)
- `DebugOptionsRequest` / `DebugOptionsResponse` transport packets; `RpcCall.DeepDesyncRequested` per-call flag
- Expedition example: `GenerateNewMapBroken()` using `System.Random` (intentional desync), 3-button UI (ServerReplace / Optimistic / Broken), server `--desync` flag, client toggle button

### Fixed

- `ServerMetaConfigurationGenerator` assembly scan: no longer skips user assemblies with `SharedMeta` prefix — only skips framework packages

## [0.6.0] - 2026-04-03

### Added

- **Platform authentication** — pluggable `IExternalAuthValidator` for Google Play Games, Sign in with Apple, and Steam. Separate NuGet packages: `Auth.Google`, `Auth.Apple`, `Auth.Steam`
- **Account linking** — `POST /link` [Authorize] binds platform to current player; `POST /unlink` [Authorize] removes any auth key (cannot unlink last key)
- **Platform login** — `POST /login-platform` authenticates via platform token, keyed as `{platform}:{platformUserId}`
- `GET /keys` [Authorize] — list linked auth keys for current player
- `IAuthIndexGrain` — per-player auth key index
- `AuthGrain.LinkAsync`, `UnlinkAsync`, `GetPlayerIdAsync`
- Client: `MetaAuth.LoginWithPlatformAsync`, `LinkAccountAsync`, `UnlinkAsync`
- DI: `AddMetaAuthGoogle()`, `AddMetaAuthApple()`, `AddMetaAuthSteam()`
- 12 auth integration tests

## [0.5.2] - 2026-03-28

### Fixed

- BestHTTP transport asmdef: `HAS_BESTHTTP` auto-defined via `versionDefines` when `com.tivadar.besthttp` is installed — no manual scripting define needed

## [0.5.1] - 2026-03-26

### Fixed

- Generator: Query methods no longer emit unused `On{Method}_Replayed` events in API client
- Unity transport: `QueryCallAsync` implemented in `BestHttpSignalRConnection`, `BestHttpPollingConnection`, `SignalRConnection`, `UnityHttpConnection`

### Added

- Query methods in `ApiClient` as synchronous local calls — executes on client state without network roundtrip when entity is already subscribed
- Expedition example: client-side Query flow with generation mode choice (ServerReplace vs Optimistic)

## [0.5.0] - 2026-03-22

### Added

- **Query Calls** — lightweight read-only RPC to any entity without subscribing. `[MetaMethod(Query = true)]` marks methods as queryable; `[MetaMethod(Query = true, OpenAccess = true)]` bypasses EntityAccessPolicy. Client uses generated `{Service}QueryApi` with `entityId` per-proxy. Server routes through `SessionManager.QueryEntityAsync` → `EntityGrain.HandleQueryAsync`. No broadcasts, no replay, no persistence, no sequence numbers.
- `QueryCallRequest` / `QueryCallResponse` transport packets — supported across SignalR, HttpPolling, and InProcess transports
- `MetaProviderBase.HandleQueryAsync` — dispatches query call without replay/random/broadcast machinery
- `QueryClientGenerator` — generates `{Service}QueryApi` classes with typed query methods
- **`ExecutionMode.ServerReplace`** — server executes method and sends full serialized state; client replaces state wholesale. Use when full state < patch diff (map generation, full reset). `OnStateRefreshed` event fires on client.
- **`Context.GetState<TEntityState>(entityId)`** — read-only cross-entity state access from shared code via `MetaContext`. `[AlwaysInterleave]` grain method prevents deadlocks. Recorded to replay payload for deterministic client replay. Returns `null` if unknown entity type.
- `IEntityGrainBase.GetEntityStateAsync()` — read-only state serialization at grain level
- `RpcResponse.StateBytes` / `EntityBroadcast.StateBytes` — full state transport pipeline
- Generator: `ServerReplace` mode dispatch and `GenerateServerReplaceMethod` in API client
- StateBytes handling in broadcast, subscriber, and trigger replay handlers
- Integration tests: `ServerReplaceTests` (3) and `GetStateTests` (3)
- GUIDE.md: ServerReplace mode section (Section 4), GetState section (Section 6)

## [0.4.5] - 2026-03-15

### Fixed

- `EnsureSessionConnected` guard: now checks `IsSessionConnected` instead of null, preventing `ArgumentException` on empty grain key
- SignalR connection stability: server-side timeouts (`ClientTimeoutInterval=30min`, `KeepAliveInterval=15min`) now apply in all environments, not just Development mode

### Added

- BestHTTP SignalR: `PingInterval`, `ReconnectDelays`, `MaxReconnectAttempts` options; automatic reconnect via `DefaultRetryPolicy`
- Server Runner: explicit text color for dark theme readability, Copy button for log
- GUIDE.md: Quick Start section, Expedition Unity example, Common Pitfalls

## [0.4.3] - 2026-03-15

### Added

- **Service error state** — generated API clients catch exceptions during shared method execution at the framework level: log via `MetaLog.Error`, set error state (`HasError`/`ErrorException`), fire `OnServiceError` event, block further calls until `ClearError()` or reconnect
- `ServiceErrorStateException` — thrown when calling methods on an error-state service
- Service Error Handling section in GUIDE.md and SharedMeta-AI.md

## [0.4.2] - 2026-03-14

### Fixed

- CrossOptimistic cross-entity calls: `LocalEntityCaller` now propagates entity `Config` to `CrossOptimisticMetaContext`, fixing `NullReferenceException` when cross-entity service methods access `Config` properties (e.g. energy regen, game limits)
- Added `ICrossEntityResolver.GetEntityConfig(entityId)` for config resolution during client-side cross-entity execution

## [0.4.1] - 2026-03-14

### Changed

- `PlayerPrefsTokenStorage` — keys now isolated by `Application.identifier` (bundle ID), preventing token conflicts between multiple SharedMeta projects on the same device

## [0.4.0] - 2026-03-14

### Push-Based Change Tracking

- `[Tracked]` attribute on private backing fields — generates public properties with tracking setters
- `ChangeTracker` — `AsyncLocal` change buffer, activated per method call, pooled for zero allocation
- `ChangeNode` struct — flat-list tree of field changes with parent/child indices
- `ChangeValue` — discriminated union avoiding boxing for int/long/float/double/bool/string
- `ListPool<T>` and `ObjectPool<T>` — simple pools for change node lists and wrapper views
- Client-only: `ChangeTracker.Current` is null on server (zero overhead)
- `ReactiveStateGenerator` — generates `TrackingProperty` enum, tracking property setters, and `Tracked{State}` static subscription classes
- Generated API clients wrap method execution in `ChangeTracker.Activate()`/`FlushAndNotify()` — broadcasts, optimistic replay, and ServerPatch replay all fire tracked field subscriptions

### Added

- `OnStateMutated` event on generated API clients — fires after any state mutation (broadcast replay, subscriber event, reconnect)
- `GetEntityConfig<TConfig>(entityId)` on `MetaClient` and `IMetaServiceResolver` — access resolved server config from client code

### Changed

- Generated broadcast deserialization always uses `CreateReader` for correct length-prefixed format (fixes edge case with single-param broadcasts)

## [0.3.5] - 2026-03-12

### Added

- `MetaAuth` — cross-platform authentication helper with `LoginAsync` and `EnsureAuthenticatedAsync` (works on both Unity and .NET)
- `ITokenStorage` interface for persisting auth tokens across app sessions
- `PlayerPrefsTokenStorage` (Unity) — `ITokenStorage` implementation using PlayerPrefs
- `CachedToken` — token wrapper with automatic expiry validation (5-minute safety margin)
- `UnityMetaAuth` — Unity login via `UnityWebRequest`, auto-registered at startup via `[RuntimeInitializeOnLoadMethod]`
- `SharedMeta.Auth.Client` asmdef — separate assembly for Unity-dependent auth code (`noEngineReferences: false`)
- Project Wizard: generated client now uses `MetaAuth.EnsureAuthenticatedAsync` with `PlayerPrefsTokenStorage` when auth is enabled, passes `accessToken` to all transport types

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

### Extensibility

- `MetaHub.SessionConnect` — now `virtual` for custom authentication/session logic in subclasses
- `MetaHub.GetOrCreateHandler()` and `GetHandler()` — now `protected` (was `private`)
- `BestHttpSignalRConnection.Hub` — protected getter for the underlying BestHTTP `HubConnection` (ext-service adapters)
- `BestHttpPollingConnection.Options` — protected getter for connection options (ext-service adapters)
- `BestHttpPollingConnection.PostAsync<T>()` and `PostRawAsync()` — now `protected` for subclass HTTP calls

## [0.3.1] - 2026-03-06

### Static Game Configuration (`[MetaConfig]`)

- `[MetaConfig]` attribute for static game configuration classes
- `IMetaConfigProvider<TConfig>` — server-side versioned config provider
- `MetaConfigVersion` struct (Major.Minor) with full serialization support (MemoryPack, MessagePack, Orleans)
- `IConfigVersionResolver` — optional DI service for A/B tests and gradual config rollouts
- `IConfigDownloadUrlResolver` — generated resolver for config download URLs
- Config version pinning per entity in `EntityGrainState.ConfigVersion`
- `IMetaConfigCache` and `IMetaConfigDownloader` client-side interfaces for config caching/downloading
- `GetConfigDownloadUrlAsync` RPC for on-demand config URL resolution
- `EntityGrainState` and `PersistedSubscriberInfo` now use `[MemoryPackable(GenerateType.VersionTolerant)]` for backward-compatible persistence

## [0.3.0] - 2026-03-06

### State Initialization (`[MetaInit]`)

- `[MetaInit]` attribute for automatic state initialization and migration during grain activation
- Signature: `Task<int> Init(int version)` — takes current version, returns new version
- `EntityGrainState.Version` persisted alongside entity state
- Server-only: not broadcast to clients (clients receive initialized state via snapshot)
- Grain not persisted after init alone — only when players interact (`_isDirty` guard)

### Persistence

- `_isDirty` flag in `EntityGrain` — skip persistence for grains activated but never interacted with
- Unified persistence pattern: `PersistIfNeeded` moved to `finally` blocks in all `Handle*` methods
- Removed force-persist from error catch blocks — errors set dirty flag naturally via sequence number increment

## [0.2.0] - 2026-03-05

### BestHTTP Transports (Unity)

New Unity transports using BestHTTP asset, included in the UPM package:

- **`BestHttpSignalRConnection`** — SignalR transport via BestHTTP. Works on all Unity platforms including WebGL. Supports JSON (LitJson) and MessagePack protocols. Wraps IFuture-based API with TaskCompletionSource for async/await.
- **`BestHttpPollingConnection`** — HTTP long-polling transport via BestHTTP. Universal compatibility.
- LitJson custom type registrations: `Guid` (string) and `byte[]` (base64) for compatibility with server-side `System.Text.Json`.
- MessagePack SignalR protocol support via `MessagePackCSharpProtocol` (requires `BESTHTTP_SIGNALR_CORE_ENABLE_MESSAGEPACK_CSHARP` scripting define).

### Client-Only Transport Packages (.NET)

New NuGet packages for .NET clients (Godot, console apps, etc.) without server dependencies:

- **`CoreGame.SharedMeta.Transport.SignalR.Client`** — `SignalRConnection` + `MetaHubProxy` implementing `IConnection`. Uses JSON protocol by default (zero extra dependencies). Supports optional `configureBuilder` callback for MessagePack or other protocols. Dependencies: `SharedMeta.Core` + `Microsoft.AspNetCore.SignalR.Client`.
- **`CoreGame.SharedMeta.Transport.HttpPolling.Client`** — `HttpPollingConnection` implementing `IConnection` using `System.Net.Http.HttpClient` and `System.Text.Json`. Drop-in replacement for Unity's `UnityHttpConnection`. Dependencies: `SharedMeta.Core` only.
- **`CoreGame.SharedMeta.Transport.SignalR.MessagePack`** — Bridge package providing `AddMetaMessagePackProtocol()` extension for `IHubConnectionBuilder`. Dependencies: `SharedMeta.Serialization.MessagePack` + `Microsoft.AspNetCore.SignalR.Protocols.MessagePack`.

All client NuGet packages target `net8.0` and `net10.0` with zero server dependencies (no Orleans, no ASP.NET FrameworkReference).

### Transport Refactoring

- **`SharedMeta.Transport.SignalR`** (server) now references `SharedMeta.Transport.SignalR.MessagePack` for the MessagePack protocol extension
- Client-side `AddMetaMessagePackProtocol(IHubConnectionBuilder)` moved to `SharedMeta.Transport.SignalR.MessagePack`
- JSON and MessagePack protocols work simultaneously — JSON clients connect to MessagePack servers via SignalR protocol negotiation

### Unity Project Wizard

- Add transport selection: SignalR (WebSocket), HTTP Polling, BestHTTP SignalR, BestHTTP HTTP
- Add serializer selection: MemoryPack or MessagePack
- Add dependency management UI with auto-detection and install buttons
- Generate complete Shared, Server, and Client projects from wizard
- BestHTTP SignalR + MessagePack: generates `MessagePackCSharpProtocol()` connection with proper scripting defines
- Fix `Directory.Packages.props` generation: update existing SharedMeta package versions, add raw serializer NuGet packages
- Fix `ResolveSharedMetaPackageVersion` returning stale version from nupkg files instead of wizard UI field
- Conditional transport assemblies via `defineConstraints` — no hard dependencies

### ServerPatch

- Fix optimistic random not advancing on client after patch application
- Fix `PatchBytes` not forwarded to broadcast subscribers via `SessionManagerGrain`
- Add `MetaRandom.Skip(count)` for advancing PRNG state without producing values

### Generator

- Ship pre-built generator DLL in UPM package (`Runtime/Analyzers/`)
- Generated client code now supports transport selection in `MetaGameClient.cs`
- MessagePack serializer support in generated code (`[MetaSerializer(SerializerType.MessagePack)]`)
- Fix subscriber broadcast dispatchers not calling service methods

### MessagePack

- Cross-assembly serialization via `CompositeResolver` — resolvers from all referenced assemblies are composed at startup
- `MetaMessagePackOptions.Configure(params Assembly[])` discovers per-assembly source-generated resolvers via reflection
- Auto-generated `GeneratedMetaMessagePackConfiguration.Configure()`

### Other

- Fix `pack-nugets.sh` MSYS path conversion (`/p:` → `-p:`)
- Remove hard `com.unity.nuget.newtonsoft-json` dependency from `package.json`
- Fix `ConnectResponse` properties: `init` → `set` to resolve MsgPack017 warning
- Add `IsAsync` property to `SubscriberMethodInfo` for correct async/sync dispatch

## [0.1.0] - 2026-02-26

- Initial public release
