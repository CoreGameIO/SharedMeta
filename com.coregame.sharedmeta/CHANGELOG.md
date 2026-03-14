# Changelog

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
