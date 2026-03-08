# Changelog

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
