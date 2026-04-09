# Changelog

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
