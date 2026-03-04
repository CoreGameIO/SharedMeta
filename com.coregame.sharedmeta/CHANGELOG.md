# Changelog

## [0.2.0] - 2026-03-03

### Added
- Client-only NuGet transport packages for Godot / .NET clients:
  - `CoreGame.SharedMeta.Transport.SignalR.Client` — SignalR with JSON protocol (no server deps)
  - `CoreGame.SharedMeta.Transport.HttpPolling.Client` — HTTP polling with HttpClient (no server deps)
  - `CoreGame.SharedMeta.Transport.SignalR.MessagePack` — optional MessagePack protocol extension
- `SignalRConnection` `configureBuilder` callback for custom protocol configuration

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
