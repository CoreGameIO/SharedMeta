# SharedMeta Architecture & Decision Record

> Single source of truth for architectural decisions, chosen technologies, and their rationale.
> Maintained alongside the codebase. Check this document when evaluating consistency of new changes.

**Current version:** 0.10.0  
**Last updated:** 2026-04-13

---

## 1. Mission & Constraints

SharedMeta is a framework for **shared game meta-logic** between Client and Server with **deterministic replay**. Game logic is written once in C# and executes on both sides.

**Core constraints that drive all decisions:**

| Constraint | Impact |
|---|---|
| Unity 6 as primary client | Must target `netstandard2.1`, no server-only APIs in shared code |
| Deterministic replay | No `System.Random`, no `DateTime.Now`, no floating-point math in shared logic |
| Optimistic execution | Client runs logic before server confirms — requires identical algorithms |
| Multiple transports | Transport layer must be swappable without touching business logic |
| Multiple serializers | Serialization must be swappable; states travel as `byte[]` |
| Horizontal scaling | Server must support distributed entities (Orleans grains) |

---

## 2. Layer Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Meta Layer          (SharedMeta.Core, *.Shared projects)       │
│  Business logic, [MetaService] interfaces, [MetaServiceImpl]    │
│  Code generation for dispatchers, API clients, patch wrappers   │
└────────────────────────────────────────────────────────────────-─┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Middleware Layer     (SharedMeta.Client, SharedMeta.Server)     │
│  MetaContext, execution modes, replay, change tracking          │
└─────────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Serialization Layer  (SharedMeta.Serialization.*)              │
│  IMetaSerializer / IPayload / IPayloadReader / IPayloadWriter   │
│  MemoryPack, MessagePack implementations                       │
└─────────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Transport Layer      (SharedMeta.Transport.*)                  │
│  IConnection (client), IMetaHub (contract), IBroadcastSender    │
│  SignalR, HTTP long-polling, InProcess (testing)                │
└─────────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Server Backend Layer (SharedMeta.Server.Core, Orleans)          │
│  IMetaProvider, EntityGrain, SessionManagerGrain                │
│  Persistence, cross-entity calls, RPC ordering                  │
└─────────────────────────────────────────────────────────────────┘
```

**Why layers?** Each layer depends only on layers above it. Cross-layer communication is through interfaces. This makes it possible to swap transport (SignalR ↔ HTTP polling), serializer (MemoryPack ↔ MessagePack), or backend (Orleans ↔ something else) independently.

---

## 3. Technology Choices

### 3.1 Server Runtime: Microsoft Orleans 10.0

**Choice:** Orleans virtual actor (grain) model for entity management.

**Why:**
- Each game entity (player profile, game session, lobby) maps to a grain — natural fit for per-entity state isolation
- Single-threaded grain execution eliminates concurrency bugs in game logic
- Built-in persistence abstraction (IPersistentState) with pluggable storage providers
- Transparent location and lifecycle management — grains activate on demand, deactivate on idle
- Cross-entity calls via grain references (`IGrainFactory`) without manual service discovery
- Orleans handles distribution across silo cluster; game code doesn't know about deployment topology

**Key grains:**
- `EntityGrain<TState>` — generic wrapper around `IMetaProvider<TState>`, handles RPC, persistence, subscriptions
- `SessionManagerGrain` — per-player session, manages subscriptions and broadcast routing, optional RPC reordering
- `AuthGrain` / `AuthIndexGrain` — auth key → player ID mapping
- `LobbyGrain` — matchmaking queue per game mode

**Important:** Orleans `[GenerateSerializer]` / `[Id(n)]` are only for framework-internal grain state wrappers. Game state and DTOs use transport serializer attributes (MemoryPack/MessagePack).

### 3.2 Serialization: MemoryPack (primary) + MessagePack (alternative)

**Choice:** Dual serializer support via `IMetaSerializer` abstraction.

| | MemoryPack 1.21 | MessagePack 3.1 |
|---|---|---|
| Speed | Fastest (.NET), zero-copy | Fast, slightly slower |
| Unity support | Source-generated, netstandard2.1 | Source-generated, netstandard2.1 |
| Version tolerance | `GenerateType.VersionTolerant` + `[MemoryPackOrder(n)]` | Integer `[Key(n)]` (inherently tolerant) |
| SignalR protocol | JSON (default SignalR) | Native MessagePack protocol |
| Partial class requirement | Yes (source generator) | No |

**Why MemoryPack as default:**
- Highest serialization throughput in .NET benchmarks
- Source-generated: no runtime reflection, AOT-compatible
- `VersionTolerant` mode allows safe field addition/removal for persisted state

**Why keep MessagePack:**
- SignalR's native MessagePack protocol reduces wire overhead vs JSON
- Some teams already use MessagePack ecosystem
- Second serializer proves the abstraction layer works

**Payload format:** Length-prefixed multi-value sequences (`int32 length + bytes` per value). Both serializers use the same `IPayloadWriter` / `IPayloadReader` contract for recording non-deterministic values during replay.

### 3.3 Transport: SignalR (primary) + HTTP Long-Polling (universal)

**Choice:** Two transport implementations behind `IConnection` interface.

| | SignalR (WebSocket) | HTTP Long-Polling |
|---|---|---|
| Latency | Low (persistent connection) | Higher (poll interval) |
| Ordering | FIFO guaranteed (single connection) | Not guaranteed (needs `EnforceRpcOrder`) |
| Compatibility | Requires WebSocket support | Works everywhere (HTTP only) |
| Reconnection | Built-in with exponential backoff | Client-side with connectionId |
| Server | ASP.NET Core Hub | ASP.NET Core Minimal API endpoints |

**Why SignalR as primary:**
- WebSocket provides real-time bidirectional communication with minimal overhead
- ASP.NET Core SignalR handles connection management, reconnection, keepalive
- Hub pattern maps well to RPC + broadcast model

**Why HTTP polling:**
- Universal compatibility (environments without WebSocket support)
- Simpler infrastructure (no persistent connections, works behind restrictive firewalls/proxies)
- Demonstrates transport abstraction works in practice

**InProcess transport** (`SharedMeta.Debug`) exists for testing — no network, direct method calls.

### 3.4 Code Generation: Roslyn Incremental Source Generator

**Choice:** Compile-time source generation over runtime reflection.

**Why:**
- No runtime delegate dictionaries or `Activator.CreateInstance` — direct method routing via `switch`
- AOT-compatible (critical for Unity IL2CPP and .NET NativeAOT)
- Compile-time validation: typos in service/method names are build errors, not runtime surprises
- Zero startup cost: no assembly scanning, no delegate registration
- Generated code is inspectable and debuggable

**What gets generated:**

| Source | Generated | Purpose |
|---|---|---|
| `[MetaService]` interface | `*Dispatcher.g.cs` | Server-side method routing |
| `[MetaService]` interface | `*ApiClient.g.cs` | Typed client API (async, execution modes) |
| `[MetaService]` interface | `*QueryApi.g.cs` | Query-only client (no subscription needed) |
| `[MetaService]` interface | `*ServiceExtensions.g.cs` | DI registration |
| `[MetaServiceImpl]` class | `*.Context.g.cs` | Context/State/dependency injection |
| `[MetaServiceImpl(DeepDesync=true)]` | `*_PatchTracked.g.cs` | Deep desync tracking copy |
| `ISharedState` class | `*PatchWrapper.g.cs` | Change tracking for ServerPatch |
| `ISharedState` class | `*PatchApplier.g.cs` | Client-side patch application |
| `ISharedState` class | `*PatchSchema.g.cs` | Diagnostic field name mapping |
| `[Tracked]` fields | `ChangeTracking.g.cs` | Push-based reactive tracking |
| Assembly-level | `ServerMetaConfiguration.g.cs` | MetaProvider + service wiring |

**Method signature validation:** FNV-1a 64-bit hash of service method signatures computed at connection time. Mismatch between client and server rejects connection — prevents silent protocol divergence.

### 3.5 Deterministic Random: xoshiro128**

**Choice:** Custom `MetaRandom` using xoshiro128** algorithm.

**Why xoshiro128\*\*:**
- Platform-independent: identical results on x86, ARM, Mono, CoreCLR
- Fast: 4 x uint32 state, minimal operations per sample
- Statistically strong: passes BigCrush, no observable bias
- Small state: serializable for persistence and wire transfer
- Unbiased integer sampling via rejection method (no modulo bias)

**Two random streams:**
- `Context.Random` — optimistic random. Same seed on client and server, same algorithm → identical sequences. `ScrollId` tracks call count for desync detection
- `Context.ServerRandom` — server-only. Real random on server (recorded into replay payload), replayed from payload on client. Used for hidden state (loot drops, etc.)

**Banned in shared code:** `System.Random` (platform-dependent implementation), `DateTime.Now`/`DateTime.UtcNow` (use `Context.ServerTimeTicks`).

### 3.6 Authentication: JWT + Platform Providers

**Choice:** HS256 JWT tokens with pluggable platform authentication.

**Components:**
- `AuthGrain` — maps auth key (device ID or `{platform}:{platformUserId}`) → PlayerId
- `JwtTokenService` — generates HS256 JWT with `sub` (PlayerId), `auth_type`, `jti` claims
- `IExternalAuthValidator` — pluggable interface for platform token verification

**Platform providers:**
- `SharedMeta.Auth.Google` — Google Play Games (server auth code → OAuth2 token exchange)
- `SharedMeta.Auth.Apple` — Sign in with Apple (JWT identity token → JWKS verification)
- `SharedMeta.Auth.Steam` — Steam (session ticket → Steam Web API validation)

**Why JWT:**
- Stateless verification on every request (no DB lookup per RPC)
- Standard ecosystem: ASP.NET Core `JwtBearer` middleware, SignalR query string support
- Claims carry player identity without additional lookups

**Account linking:** Players can link multiple auth keys (device + platform accounts) to one PlayerId. Safety: cannot unlink the last key.

### 3.7 Target Frameworks

| Project group | Targets | Rationale |
|---|---|---|
| Core/Client (shared) | `netstandard2.1; net8.0; net10.0` | Unity requires netstandard2.1; modern .NET for server and .NET clients |
| Server projects | `net8.0; net10.0` | LTS + latest .NET for Orleans and ASP.NET Core |
| Orleans.Stubs | `netstandard2.1` | Stub attributes so Unity compiles without real Orleans/MessagePack NuGet packages |
| Source Generator | `netstandard2.0` | Roslyn analyzer requirement |
| Unity projects | Unity 6000.0 (net4.7.1 legacy) | Unity's scripting runtime |

### 3.8 Observability

**Current:** Serilog + OpenTelemetry stubs in `Directory.Packages.props`.

| Package | Version | Purpose |
|---|---|---|
| Serilog.AspNetCore | 10.0.0 | Structured logging on server |
| OpenTelemetry.Extensions.Hosting | 1.15.0 | Distributed tracing infrastructure |
| OpenTelemetry.Exporter.Console | 1.15.0 | Local development exporter |

---

## 4. Execution Model

### 4.1 Execution Modes

| Mode | Client behavior | Server behavior | Use case |
|---|---|---|---|
| **Optimistic** | Execute immediately, replay server result | Execute authoritatively, record non-deterministic values | UI-responsive actions (move, buy, play card) |
| **Server** | Wait for server response, then replay | Execute, record ServerRandom values | Hidden state (loot, matchmaking) |
| **Local** | Execute locally, no RPC | — | UI state, previews, client-only filtering |
| **CrossOptimistic** | Execute on cached cross-entity state | Execute with real cross-entity calls | Cross-entity interactions (trade, attack) |
| **ServerPatch** | Apply state diff patch | Execute, generate PatchNode diff | Hotfixes, bypassing client logic bugs |
| **ServerReplace** | Replace full state from bytes | Execute, serialize full state | Map generation, full state reset |
| **Query** | Receive response, no state sync | Read-only, no persistence/broadcast | Get player info, check entity state |

### 4.2 Deterministic Replay Flow

```
Client                              Server
  │                                   │
  ├─ Execute optimistically ──────┐   │
  │  (local state mutated)        │   │
  │                               │   │
  ├─ Serialize args ──────────────┼──→├─ Deserialize, execute authoritatively
  │                               │   ├─ Record non-deterministic values
  │                               │   ├─ Return RpcResponse + ReplayPayload
  │←──────────────────────────────┼───┤
  ├─ Replay with server payload   │   │
  ├─ Compare result (desync check)│   │
  └───────────────────────────────┘   │
```

**Non-deterministic values recorded for replay:**
- `Context.ServerRandom` calls (values)
- Cross-entity state reads (serialized state snapshots)
- Cross-entity call results (response bytes)
- `Context.ServerTimeTicks` (UTC ticks at call time)

### 4.3 RPC Ordering

**Problem:** HTTP polling and threadpool scheduling can deliver RPCs to grains out of submission order. Out-of-order execution causes phantom desyncs even when client logic is correct.

**Solution:** `SessionManagerGrain` optionally enforces monotonic `RequestId` ordering via `RpcOrderingBuffer<T>` — an allocation-free ring buffer that parks out-of-order calls and drains them when predecessors arrive.

- Enabled via `SessionManagerOptions.EnforceRpcOrder` (default `false`)
- SignalR over single WebSocket connection has native FIFO — doesn't need this
- HTTP polling and custom transports should enable it

**Session health:** Stall diagnostics are lazy — pushed on the next incoming request when a gap exists, and logged on grain deactivation. No periodic grain timers. Client-side auto-retry (`ConnectionHealthOptions.RetryIntervalMs`) is the primary recovery mechanism for lost packets.

### 4.4 Client-Side Connection Health

**Problem:** Server-side stall detection (`StallNotification` / `ISessionHealthListener`) only catches out-of-order RPCs on the server. When the server is unreachable (network down, high latency), the client has no feedback mechanism.

**Solution:** `IConnectionHealthListener` monitors pending request age in `ClientDispatcher.ProcessPendingBroadcasts()` (runs every frame). Two configurable thresholds:

| Threshold | Default | Status | UI response |
|---|---|---|---|
| `SoftTimeoutMs` | 1000 ms | `Slow` | Lightweight spinner/indicator |
| `HardTimeoutMs` | 5000 ms | `Unresponsive` | "Connection issue" dialog |
| — | — | `Healthy` | Hide all overlays |

**Design decisions:**
- Separate interface from `ISessionHealthListener` — different trigger source (client-side timeout vs server-side stall), different data shape (elapsed ms + pending count vs stall info)
- Transition-only callbacks — listener is notified once per status change, not every frame
- Runs first in `ProcessPendingBroadcasts()` — before broadcast suppression check, so health monitoring and auto-retry work even while optimistic RPCs await replay
- Wired via `MetaClientOptions.ConnectionHealth` + `MetaClientOptions.ConnectionHealthOptions`

**Auto-retry:** When pending requests exceed `SoftTimeoutMs`, the client automatically resends all pending requests every `RetryIntervalMs` (default 2s). This is the primary packet loss recovery mechanism — fully client-side, independent of server stall notifications or transport type.

### 4.5 Debug Network Simulation

`DebugConnectionWrapper` wraps any `IConnection` to simulate network problems at the transport level:

| Setting | Effect |
|---|---|
| `MinLatencyMs` / `MaxLatencyMs` | Added delay before forwarding RPC to real connection |
| `PacketLossPercent` (0-100) | Behavior depends on `LossMode` (see below) |
| `LossMode` | `ConnectionDrop` (full disconnect, SignalR) or `RequestHang` (throw `HttpRequestException`, HTTP polling) |
| `SimulateDisconnect()` | Fires `OnDisconnected` event — permanent drop |
| `SimulateTemporaryDisconnectAsync(ms)` | Real disconnect→reconnect cycle with configurable outage duration |
| `Enabled` | Master switch; when false, all calls pass through unmodified |

**Location:** `Runtime/Core/Transport/DebugConnectionWrapper.cs` (UPM package, available to all clients).

**Two packet loss modes:**
- `ConnectionDrop` — full disconnect, realistic for SignalR/WebSocket/TCP (all-or-nothing transport)
- `RequestHang` — throws `HttpRequestException` immediately, realistic for HTTP polling where individual requests can fail. `SendAndCompleteAsync` catches the exception, keeps the request pending, and client auto-retry handles resending

Settings are mutable at runtime, enabling live adjustment from debug UI (see Expedition Unity example).

---

## 5. State Patching System

### 5.1 PatchNode Tree

`PatchNode` is a hierarchical diff structure representing state changes. Each node can be:
- **Terminal** — carries serialized `Value` (full field replacement)
- **Branch** — has `Children` (partial nested changes)
- **Collection** — has `StructuralOps` (Add/Insert/RemoveAt/Set/Clear/FullReplace)

**Wire format (v0.9.0+):**
- `Kind`: `Field` | `ElementByIndex`
- `FieldId`: `long` (serialization key for fields, list index for elements)
- `Value`: `byte[]?` (terminal serialized value)
- `Children`: `List<PatchNode>?` (nested changes)
- `StructuralOps`: `List<PatchListOp>?` (collection mutations)

### 5.2 Element-Sub-Wrappable Lists (v0.9.0)

For `List<T>` where `T` has serialization-keyed properties, the generator produces per-element `PatchWrapper` instances. Mutations like `state.Heroes[5].Exp += 100` produce `Heroes/[5]/Exp` patches instead of full list snapshots.

**Index shifting:** When `Insert`/`RemoveAt` reshapes a list with pending element mutations, `PatchNode.ShiftElementChildren` adjusts affected indices automatically.

### 5.3 Deep Desync Detection (v0.7.0)

Optional field-level mutation tracking:
1. Generator produces `_PatchTracked` service copy where `State` routes through `PatchWrapper`
2. Server computes FNV-1a CRC of PatchNode tree after each call
3. Client compares local CRC
4. On mismatch: `PatchTextRenderer.DiffToJson` produces side-by-side JSON diagnostics

Enabled via `[MetaServiceImpl(DeepDesync = true)]` + runtime `EntityGrainOptions.DeepDesyncEnabled`.

---

## 6. Client-Side Reactive Tracking

### 6.1 Push-Based Change Tracking

Fields marked with `[Tracked]` get generated property setters that record mutations:

```csharp
[Tracked] private int _gold;
// Generated: public int Gold { get => _gold; set { record change; _gold = value; } }
```

**Design:**
- `ChangeTracker` lives in `AsyncLocal` — one per async execution context
- `null` on server (zero overhead)
- Pooled instances via `_trackerPool`
- `ChangeNode` tree with parent references, old/new values
- `ChangeValue` discriminated union avoids boxing for primitives
- `FlushAndNotify()` walks tree and dispatches to subscribers

**Why not INotifyPropertyChanged?** Boxing overhead, no tree structure, no batching. The custom system is zero-alloc on server, batched on client, and carries full mutation path.

---

## 7. Static Configuration System

`[MetaConfig]` classes define read-only balance/design data:
- `IMetaConfigProvider<TConfig>` on server — versioned config storage
- `IConfigVersionResolver` — A/B testing, gradual rollouts
- Config version pinned per entity on first activation
- Sent to client on subscribe, materialized by `IClientMetaConfigProvider<TConfig>` registered on the resolver (built-ins: `StaticConfigProvider`, `DownloadingConfigProvider`, `CompositeConfigProvider`; optional `IClientMetaConfigCache<TConfig>` for disk caching)
- Available in `Context.Config` during all execution modes including `[MetaInit]`

---

## 8. Cross-Entity Communication

Two patterns for entity-to-entity interaction:

1. **Cross-entity calls** — declare the target as a dependency in `[MetaServiceImpl(..., typeof(ITargetService))]`; the generator injects a typed `GetITargetService(entityId)` accessor into the service partial that returns the correct proxy for each mode (Server grain call, CrossOptimistic `LocalEntityCaller`, client replayer). Call results are recorded into the replay payload. Server resolves to grain reference via `IEntityGrainResolver`
2. **Cross-entity state reads** — `Context.GetState<TState>(entityId)` returns read-only snapshot. Recorded for replay consistency

**CrossOptimistic mode:** Client executes on locally cached cross-entity state, server executes with real grain calls. Broadcast suppression prevents duplicate notifications to the caller.

---

## 9. Packaging & Distribution

### 9.1 Unity (UPM Package)

`com.coregame.sharedmeta/` is a Unity Package Manager package:
- `Runtime/Core/` — shared interfaces and types (netstandard2.1)
- `Runtime/Client/` — MetaClient, ClientDispatcher
- `Runtime/Serialization/` — MemoryPack and MessagePack serializers
- `Runtime/Transport/` — SignalR and HTTP polling client connections
- `Runtime/Auth/` — UnityMetaAuth, PlayerPrefsTokenStorage
- `Analyzers/` — pre-built `SharedMeta.Generator.dll` (Roslyn analyzer)
- `Orleans.Stubs/` — stub attributes (`[GenerateSerializer]`, `[Id]`, `[MessagePackObject]`, `[Key]`) so Unity compiles without server NuGet packages

### 9.2 .NET (NuGet Packages)

21 NuGet packages in `src/`, organized by layer:
- `SharedMeta.Core`, `SharedMeta.Client` — shared + client
- `SharedMeta.Server.Core`, `SharedMeta.Server` — server
- `SharedMeta.Orleans` — Orleans-specific grains (Lobby, etc.)
- `SharedMeta.Transport.SignalR[.Client|.MessagePack]` — SignalR
- `SharedMeta.Transport.HttpPolling[.Client]` — HTTP polling
- `SharedMeta.Serialization.MemoryPack`, `.MessagePack` — serializers
- `SharedMeta.Auth[.Google|.Apple|.Steam]` — authentication
- `SharedMeta.Generator` — source generator (analyzer)
- `SharedMeta.Debug` — InProcess transport for testing

**Client-only NuGet packages** (`*.Client`) have no server dependencies — no Orleans, no ASP.NET `FrameworkReference`. Safe for Godot/.NET desktop clients.

### 9.3 Centralized Version Management

`Directory.Packages.props` at repo root manages all NuGet package versions. `Directory.Build.props` sets shared project properties. This prevents version drift across 51 .csproj files.

---

## 10. Testing Infrastructure

| Project | Purpose |
|---|---|
| `SharedMeta.Test.Meta1` | Test service/state definitions for generator |
| `SharedMeta.Test.Server` | Server host with generated configuration |
| `SharedMeta.IntegrationTests` | Full client→server integration via InProcess transport |
| `SharedMeta.PatchFuzzTests` | 411-test patch roundtrip suite (no Orleans, no network) |
| NativeAOT tests | Verify generator output works with ahead-of-time compilation |

**InProcess transport** (`SharedMeta.Debug`): `InProcessServer` + `InProcessConnection` enable full integration testing without network. Orleans `TestCluster` provides real grain lifecycle.

---

## 11. Design Principles & Rules

### Do

- Write game logic once, in shared projects
- Use `[MetaService]` interfaces + `[MetaServiceImpl]` classes for all game logic
- Use `Context.Random` / `Context.ServerRandom` for randomness
- Use `Context.ServerTimeTicks` for time
- Use `GenerateType.VersionTolerant` on all persisted state classes
- Mark all state/DTO classes as `partial`
- Add `[MemoryPackOrder(n)]` and/or `[Key(n)]` on every serialized property
- Add `[IgnoreMember]` on computed properties (MessagePack requirement)
- Prefer generated switch dispatchers over delegate dictionaries
- Keep layers separated — transport shouldn't know about Orleans, serialization shouldn't know about transport

### Don't

- Use `System.Random` in shared logic (platform-dependent)
- Use `DateTime.Now` / `DateTime.UtcNow` in shared logic (non-deterministic)
- Use `float` / `double` arithmetic in shared logic (non-deterministic across platforms: x86 SSE vs ARM NEON, RyuJIT vs Mono). Use `int`, `long`, `decimal`, or `Fp` fixed-point type
- Add `Polyfills.cs` to individual .csproj files (already included via shared project)
- Use Orleans `[GenerateSerializer]` / `[Id(n)]` on game state/DTOs (those are for framework-internal grain wrappers only)
- Use runtime reflection or `Activator.CreateInstance` for service dispatch

---

## 12. Version History of Architectural Changes

| Version | Architectural change |
|---|---|
| 0.10.0 | Client-side connection health + auto-retry, `DebugConnectionWrapper`, lazy server stall diagnostics |
| 0.9.0 | Granular list patches: element-sub-wrappable lists, `PatchListOp`, wire format break |
| 0.8.0 | Server-side RPC reordering gate, session health stall notifications |
| 0.7.0 | Deep desync detection: PatchTracked generation, FNV-1a CRC comparison |
| 0.6.0 | Platform authentication providers (Google, Apple, Steam), account linking |
| 0.5.0 | HTTP long-polling transport, static config system |
| 0.4.0 | Push-based `[Tracked]` change tracking, reactive state generation |
| 0.3.0 | JWT authentication, `AuthGrain` |
| 0.2.0 | ServerPatch/ServerReplace execution modes, PatchNode system |
| 0.1.0 | Core framework: Optimistic/Server/Local modes, MetaContext, code generation |

---

## 13. Open Architectural Boundaries

These areas have explicit extension points for future evolution:

- **Transport:** `IConnection` interface — new transports (gRPC, WebTransport, UDP) plug in without touching game logic
- **Serialization:** `IMetaSerializer` interface — new serializers plug in without touching transport or game logic
- **Server backend:** `IMetaProvider<TState>` interface — could run on a different actor framework or monolith
- **Auth:** `IExternalAuthValidator` — new platform providers (Epic, Discord, etc.) are isolated packages
- **Config:** `IMetaConfigProvider<TConfig>` + `IConfigVersionResolver` — A/B testing, remote config, CDN delivery
- **Persistence:** Orleans `IPersistentState` — pluggable storage (Azure Table, Redis, PostgreSQL, etc.)
