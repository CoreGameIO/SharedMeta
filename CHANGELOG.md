# Changelog

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
