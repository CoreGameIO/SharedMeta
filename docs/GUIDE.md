# SharedMeta Framework Guide

Complete technical reference for the SharedMeta framework. Covers all subsystems, configuration options, and patterns.

> This document serves both as a developer guide and as context for AI code assistants working with the codebase.

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Shared State & Services](#2-shared-state--services)
3. [Execution Modes & Replay](#3-execution-modes--replay)
4. [Deterministic Random](#4-deterministic-random)
5. [Cross-Entity Calls](#5-cross-entity-calls)
6. [Triggers & Subscribers](#6-triggers--subscribers)
7. [Argument Transformers](#7-argument-transformers)
8. [Transport Configuration](#8-transport-configuration)
9. [Serialization](#9-serialization)
10. [Session Management](#10-session-management)
11. [Authentication](#11-authentication)
12. [Persistence Configuration](#12-persistence-configuration)
13. [Orleans Backend](#13-orleans-backend)
14. [Server Setup](#14-server-setup)
15. [Client Setup](#15-client-setup)
16. [Matchmaking (Lobby)](#16-matchmaking-lobby)
17. [Desync Diagnostics](#17-desync-diagnostics)
18. [Code Generation Reference](#18-code-generation-reference)
19. [Attribute Reference](#19-attribute-reference)
20. [Testing](#20-testing)
21. [Capability Overview](#21-capability-overview)
22. [Tutorial: Building Your First Service](#22-tutorial-building-your-first-service)
23. [Architecture Decisions](#23-architecture-decisions)

---

## 1. Architecture Overview

```
Client (Unity/.NET)                           Server (.NET + Orleans)
┌──────────────────────┐                     ┌─────────────────────────────┐
│ Game Code            │                     │ MetaConnectionHandler       │
│   ↓                  │                     │   ↓                         │
│ API Client (gen)     │                     │ SessionManagerGrain         │
│   ↓                  │                     │   (per player)              │
│ MetaClient           │   SignalR/HTTP      │   ↓                         │
│   ↓                  │ ←──────────────────→│ EntityGrain<TState>         │
│ ClientDispatcher     │                     │   (per entity)              │
│   ↓                  │                     │   ↓                         │
│ IConnection          │                     │ MetaProviderBase<TState>    │
│ (SignalR/HTTP/       │                     │   ↓                         │
│  InProcess)          │                     │ Service Dispatcher (gen)    │
└──────────────────────┘                     │   ↓                         │
                                             │ Service Implementation      │
                                             │   (your game logic)         │
                                             └─────────────────────────────┘
```

**Data flow for an RPC call:**

1. Client calls `api.AttackAsync(card)` (generated API client)
2. Args serialized, sent via `IConnection.RpcCallAsync()`
3. Server `SessionManagerGrain` routes to `EntityGrain`
4. `EntityGrain` increments sequence, calls `MetaProvider.HandleCallAsync()`
5. `MetaProvider` sets up context (Random, ServerRandom, Replay recording)
6. Generated dispatcher routes to `CardGameService.Attack(card)`
7. Result + replay payload returned up the chain
8. `EntityGrain` broadcasts to other subscribers, returns result to caller's `SessionManager`
9. `SessionManager` bundles broadcasts with RPC response, assigns session sequence number
10. Client receives response, replays locally, returns result to game code

---

## 2. Shared State & Services

### State Definition

State classes implement `ISharedState` and need a transport serializer attribute:

```csharp
[MemoryPackable]  // or [MessagePackObject], or both
public partial class GameState : ISharedState
{
    [MemoryPackOrder(0)] public int Score { get; set; }
    [MemoryPackOrder(1)] public List<Player> Players { get; set; } = new();
    [MemoryPackOrder(2)] public GamePhase Phase { get; set; }
}
```

`[MemoryPackOrder(n)]` (or `[Key(n)]` for MessagePack) provides version tolerance — you can add new fields without breaking existing persisted state. States are persisted and transmitted as bytes via the chosen transport serializer; Orleans `[GenerateSerializer]`/`[Id(n)]` are not needed on game state/DTO classes.

### Service Interface

```csharp
[MetaService(StateType = typeof(GameState), AccessPolicy = EntityAccessPolicy.Open)]
public interface ICardGameService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    bool PlayCard(Card card);

    [MetaMethod(Mode = ExecutionMode.Server)]
    void DealCards();

    [MetaMethod(Mode = ExecutionMode.Local)]
    void SelectCardInHand(int index);

    [MetaMethod(Mode = ExecutionMode.CrossOptimistic)]
    Task<bool> TradeWith(string targetEntityId, Item item);
}
```

### Service Implementation

```csharp
[MetaServiceImpl(typeof(ICardGameService), typeof(GameState), typeof(IRandomService))]
public partial class CardGameServiceImpl : ICardGameService
{
    // Injected by source generator:
    // public MetaContext<GameState> Context { get; set; }
    // public GameState State => Context.State;
    // public IRandomService RandomService { get; set; }

    public bool PlayCard(Card card)
    {
        if (!State.CurrentPlayer.Hand.Contains(card)) return false;
        State.CurrentPlayer.Hand.Remove(card);
        State.Table.Add(card);
        return true;
    }

    public void DealCards()
    {
        foreach (var player in State.Players)
        {
            for (int i = 0; i < 6; i++)
            {
                int idx = Context.ServerRandom!.Next(State.Deck.Count);
                player.Hand.Add(State.Deck[idx]);
                State.Deck.RemoveAt(idx);
            }
        }
    }

    public void SelectCardInHand(int index)
    {
        State.SelectedCardIndex = index; // Local only, no server call
    }
}
```

---

## 3. Execution Modes & Replay

### Optimistic Mode (default)

Client executes immediately for instant UI feedback. Server executes authoritatively. Client replays server result for validation.

```
Client                          Server
  │                                │
  ├─ Execute locally ──────────►   │
  │  (result available)            │
  ├─ Send RPC ─────────────────►   │
  │                                ├─ Execute authoritatively
  │                                ├─ Record replay payload
  │  ◄──── Return result+replay ───┤
  ├─ Replay server result          │
  ├─ Check: local == server?       │
  │  If mismatch: desync callback  │
```

Optimistic random (`Context.Random`) uses xoshiro128** with identical seed — both sides produce same sequence. `RandomScrollDelta` tracks call count for desync detection.

### Server Mode

Client waits for server. Used when client cannot know the result (ServerRandom, hidden state).

```
Client                          Server
  │                                │
  ├─ Send RPC ─────────────────►   │
  │  (waiting...)                  ├─ Execute
  │                                ├─ Record ServerRandom values
  │  ◄──── Return result+replay ───┤
  ├─ Replay with recorded values   │
  ├─ Return result to game code    │
```

`Context.ServerRandom` on server uses `MetaRandomRecorder` that writes each value to the replay payload. On client, `MetaRandomReplayer` reads those values back sequentially.

### Local Mode

No server communication. Instant. State changes are client-only.

### CrossOptimistic Mode

Client executes locally including cross-entity calls on cached local state. Server validates. Used for interactive cross-entity gameplay.

```
Client                                    Server
  │                                          │
  ├─ Execute locally                         │
  │  ├─ Call other entity on local state     │
  │  ├─ Record cross-entity results          │
  ├─ Send RPC (isCrossOptimistic=true) ──►   │
  │                                          ├─ Execute on server
  │                                          ├─ Cross-entity grain call
  │  ◄──── Return with cross-entity info ────┤
  ├─ Compare local vs server results         │
  │  Mismatch: desync callback               │
```

### Runtime Execution Mode Override

The execution mode defined in `[MetaMethod(Mode = ...)]` is the default. You can override it at runtime using `IExecutionModeProvider` — without recompilation or redeployment.

**Override a specific method:**
```csharp
var modeProvider = client.ModeProvider as ExecutionModeProvider;

// Force "SetName" to always go through the server
modeProvider.SetMode("IProfileService", "SetName", ExecutionMode.Server);

// Force "PlayCard" to local-only (e.g., during offline mode)
modeProvider.SetMode("ICardGameService", "PlayCard", ExecutionMode.Local);
```

**Override all methods in a service:**
```csharp
// All profile methods become server-authoritative
modeProvider.SetServiceMode("IProfileService", ExecutionMode.Server);
```

**Reset overrides:**
```csharp
modeProvider.Clear();  // Revert to [MetaMethod] defaults
```

**Priority order:**
1. Specific method override (`SetMode("IService", "Method", ...)`)
2. Service-wide override (`SetServiceMode("IService", ...)`)
3. Attribute default (`[MetaMethod(Mode = ...)]`)

**Use cases:**
- Force `Server` mode during tournaments for maximum authority
- Switch to `Local` mode for offline play or latency-sensitive UI actions
- A/B testing different execution strategies without code changes
- Debugging desyncs by switching suspected methods to `Server` mode

Generated API client code calls `_modeProvider.GetMode(serviceName, methodAlias, defaultMode)` before every RPC to determine the execution path.

### ServerPatch Mode

Method executes **only on the server**. Instead of sending a replay payload, the server generates a **state diff patch** (tree of changed fields) that the client applies directly — bypassing local method execution entirely.

```
Client                          Server
  │                                │
  ├─ Send RPC ─────────────────►   │
  │  (waiting...)                  ├─ Execute with PatchNode tracking
  │                                ├─ Prune unchanged fields
  │  ◄──── Return result+patch ────┤
  ├─ Apply PatchBytes to state     │
  ├─ Return result to game code    │
```

**Use case:** Hotfixing server logic when clients can't be updated. The server runs corrected code, generates a state diff, and clients apply it without executing their buggy version.

**Server decides:** The server determines whether to use ServerPatch mode via its own `IExecutionModeProvider` (injected via DI). The client reacts to the presence of `PatchBytes` in the response.

#### PatchState Wrapper

Service implementations access state through `PatchState` — a generated typed wrapper that transparently tracks changes:

```csharp
public void Attack(Card card)
{
    var s = PatchState;                // generated typed accessor
    s.Phase = GamePhase.Attacking;     // value type → tracked automatically
    s.Table.Add(new TablePair { AttackCard = card }); // auto-tracked via PatchableList
}
```

`PatchState` is always available in service code. When the server is NOT in ServerPatch mode, it creates a non-tracking wrapper (null PatchNode) — mutations go through to `State` but no patch tree is built. This means methods can be pre-prepared for patching before it's needed.

**Property types:**
- **Value types** (int, bool, enum, string): get/set with automatic tracking
- **Nested state objects** (types with `[Id]` properties): sub-wrappers with recursive tracking
- **Collections**: `PatchableList<T>`, `PatchableDictionary<K,V>`, `PatchableHashSet<T>`, `PatchableArray<T>` — auto-mark dirty on any mutation (Add, Remove, Clear, indexed set, etc.)
- **`SetDirty()`**: Available on all wrappers for explicit marking (e.g., after mutating via `Raw`)

#### Patch Tree Structure

The patch uses `[Id(n)]` attributes on state properties as field identifiers:

```
PatchNode (root, FieldId=-1)
├── PatchNode (FieldId=8, Value=serialized Phase) ← terminal: full value
├── PatchNode (FieldId=0, Value=serialized Deck) ← terminal: full collection
└── PatchNode (FieldId=16, Children=[...])       ← non-terminal: partial changes in nested object
    └── PatchNode (FieldId=2, Value=serialized Name)
```

Terminal nodes contain the serialized value. Non-terminal nodes have children representing partial changes. After execution, the tree is pruned to remove unchanged branches.

#### Client Manifest

Clients can load execution mode overrides from a JSON manifest:

```csharp
var modeProvider = client.ModeProvider as ExecutionModeProvider;
modeProvider.LoadManifest(@"{
    ""overrides"": {
        ""ICardGameService.Attack"": ""ServerPatch"",
        ""IProfileService.*"": ""ServerPatch""
    }
}");
```

#### Deployment Workflow

1. Write methods with `PatchState` in advance for risky operations
2. When a bug is found: fix server code, configure `IExecutionModeProvider` to return `ServerPatch` for affected methods
3. Push manifest to clients so they know to expect patches instead of replays
4. After client update is deployed: remove the override, return to normal execution

#### Limitations (v1)

- Collection mutations are auto-tracked, but the entire collection is serialized as a terminal node (full replacement). Fine-grained collection diff (individual insert/remove) is planned for v2+.
- PatchNode uses only MemoryPack serialization (not Orleans `[GenerateSerializer]`).

---

## 4. Deterministic Random

### Two Random Systems

| | `Context.Random` (Optimistic) | `Context.ServerRandom` (Server) |
|---|---|---|
| Algorithm | xoshiro128** | xoshiro128** |
| Seed sync | Transmitted on subscribe | Independent on server |
| Client execution | Real generation | Replays recorded values |
| Server execution | Real generation | Real generation + recording |
| Desync detection | ScrollId delta comparison | N/A (replayed) |
| Persistence | `OptimisticRandomBytes` | `ServerRandomBytes` |
| Use case | Game mechanics both sides see | Loot, server secrets |

### API

```csharp
int value = Context.Random!.Next(100);          // [0, 100)
int ranged = Context.Random!.Next(10, 20);      // [10, 20)
float f = Context.Random!.NextFloat();           // [0.0, 1.0)

int secret = Context.ServerRandom!.Next(1000);   // Server generates, client replays
```

### Seeding

Optimistic random is seeded from the entity ID string (FNV-1a hash). State is transmitted to client on `SubscribeAsync()` and persisted in `EntityGrainState.OptimisticRandomBytes`.

---

## 5. Cross-Entity Calls

### How It Works

1. Service method calls generated entity API:
   ```csharp
   var result = await Context.GetEntityApi<ITargetService>(targetEntityId).MethodAsync(args);
   ```

2. On server: `MetaProviderBase.EntityCallHandler` resolves target grain, calls `HandleCallFromEntityAsync`

3. Target entity executes, broadcasts to ITS subscribers, returns result

4. `CrossEntityCallInfo` collected: `{ EntityId, EntitySequenceNumber, ResultBytes }`

5. `SessionManager` uses this to suppress duplicate broadcasts (advances `KnownEntitySequence` for target entity)

### Client-Side Cross-Entity (CrossOptimistic)

Client uses `CrossOptimisticMetaContext<TState>` for local execution on cached target state. Results are recorded and compared against server results for desync detection.

### Broadcast Suppression

When Entity A calls Entity B, players subscribed to both A and B would see Entity B's operation twice (once in A's RPC response, once as B's broadcast). SessionManager prevents this by advancing `KnownEntitySequence` for Entity B when processing Entity A's cross-entity call.

---

## 6. Triggers & Subscribers

### Triggers

Auto-execute a method after another method completes, if a condition is true:

```csharp
[MetaService(StateType = typeof(GameState))]
public interface ICardGameService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    void Defend(Card card);

    [Trigger(On = "Defend", Condition = "ShouldAutoEndAttack")]
    void OnDefendComplete();
}

public partial class CardGameServiceImpl : ICardGameService
{
    public void Defend(Card card) { /* ... */ }

    public bool ShouldAutoEndAttack() => State.AllPlayersDefended;

    public void OnDefendComplete()
    {
        State.Phase = GamePhase.NextTurn;
    }
}
```

Triggers execute server-side as nested operations within the parent call. The trigger's result is included in `TriggerOperations` of the response.

### Subscriber Interfaces (Framework Events)

Subscribe a service to framework events (e.g., matchmaking):

```csharp
[MetaService(
    StateType = typeof(ProfileState),
    SubscriberInterfaces = new[] { typeof(ILobbySubscriber) })]
public interface IProfileService : IMetaService
{
    [ServiceTrigger(Service = typeof(ILobbySubscriber), Method = "OnMatchFound")]
    void HandleMatchFound();
}
```

When a `LobbyGrain` calls `EntityGrain.HandleExternalEventAsync("ILobbySubscriber", "OnMatchFound", data)`, the service trigger fires.

### Client Method Subscriptions

Subscribe to specific methods being replayed from broadcasts:

```csharp
var sub = resolver.OnMethodReplayed<MatchFoundArgs>(
    entityId, "ILobbySubscriber", "OnMatchFound",
    args => Console.WriteLine($"Match found: {args.MatchId}")
);

// Later:
sub.Dispose();
```

---

## 7. Argument Transformers

Transform complex game objects into simple serializable types for RPC arguments.

### Simple Transformer

```csharp
[Transformer]
public class Vector3Transformer : IArgumentTransformer<Vector3, int[]>
{
    public int[] Box(Vector3 v) => new[] { v.X, v.Y, v.Z };
    public Vector3 Unbox(int[] a) => new Vector3(a[0], a[1], a[2]);
}
```

### State-Aware Transformer

```csharp
[Transformer]
public class PlayerTransformer : IStateArgumentTransformer<Player, int, GameState>
{
    public int Box(Player player, GameState state) => player.Id;
    public Player Unbox(int id, GameState state) =>
        state.Players.FirstOrDefault(p => p.Id == id);
}
```

### Registration

Auto-generated `TransformerRegistrations.RegisterAll(registry)` registers all `[Transformer]` classes. Manual registration:

```csharp
registry.Register<Player, int, GameState, PlayerTransformer>();
registry.RegisterSimple<Vector3, int[], Vector3Transformer>();
```

### Usage in Methods

Methods with transformer-supported parameter types are auto-boxed/unboxed by generated code. Override with:

```csharp
[MetaMethod]
void Move([Transform(typeof(Vector3Transformer))] Vector3 position);

[MetaMethod]
void RawMove([SkipTransform] Vector3 position); // No transformation
```

---

## 8. Transport Configuration

### SignalR (WebSocket)

**Server:**
```csharp
builder.Services.AddSignalR().AddMetaMessagePackProtocol();
app.MapHub<MetaHub>("/meta");
```

**Client:**
```csharp
var connection = new SignalRConnection(
    serverUrl: "https://localhost:5001/meta",
    accessToken: jwtToken  // optional
);
```

Features:
- Real-time bidirectional (WebSocket)
- Auto-reconnect with exponential backoff: [0s, 2s, 5s, 10s, 30s]
- MessagePack binary protocol (compact, supports byte[] as raw binary)

### HTTP Long-Polling

**Server:**
```csharp
app.MapMetaHttpEndpoints("/meta-http");
```

**Client:**
```csharp
var connection = new HttpPollingConnection(new HttpPollingConnectionOptions
{
    ServerUrl = "https://localhost:5001/meta-http",
    PollTimeout = TimeSpan.FromSeconds(35),    // > server hold time (30s)
    RequestTimeout = TimeSpan.FromSeconds(30),
    MaxRetryDelay = TimeSpan.FromSeconds(30),
    InitialRetryDelay = TimeSpan.FromSeconds(1),
    AccessToken = jwtToken  // optional
});
```

Endpoints:
| Method | Path | Description |
|--------|------|-------------|
| POST | `/session-connect` | Connect/resume session |
| POST | `/subscribe` | Subscribe to entity |
| POST | `/unsubscribe` | Unsubscribe |
| POST | `/rpc` | Execute RPC call |
| POST | `/ack` | Acknowledge received packets |
| POST | `/poll` | Long-poll for broadcasts (30s hold) |
| POST | `/graceful-disconnect` | Clean disconnect |
| POST | `/disconnect` | Transport disconnect |

Connection identified by `X-Connection-Id` header. Server-side inactivity timeout: 2 minutes.

### InProcess (Testing)

```csharp
var server = new InProcessServer(grainFactory, serializer, loggerFactory);
var connection = new InProcessConnection(server);

// Failure simulation
server.FailureSimulation = new FailureSimulationSettings
{
    BroadcastLossProbability = 0.1,  // 10% packet loss
    DisconnectProbability = 0.05     // 5% disconnect chance
};
```

---

## 9. Serialization

### IMetaSerializer Interface

```csharp
public interface IMetaSerializer
{
    byte[] Pack<T>(T value);
    T Unpack<T>(byte[] bytes);
    byte[] Pack(Type type, object value);
    object Unpack(Type type, byte[] bytes);
    T Clone<T>(T value);
    IPayloadWriter CreateWriter();
    IPayloadReader CreateReader(byte[] bytes);
    byte[] SerializeRpcCall(RpcCall call);
    RpcCall DeserializeRpcCall(byte[] bytes);
}
```

### MemoryPack (default)

```csharp
var serializer = new MemoryPackMetaSerializer();
```

- Requires `[MemoryPackable]` on all transported types
- Source-generated (fastest on .NET)
- RpcCall wrapped in `RpcCallDto` (MemoryPack requirement)

### MessagePack (alternative)

```csharp
var serializer = new MessagePackMetaSerializer();
```

- Uses `OrleansIdResolver` to read `[Id(n)]` attributes (no `[MessagePackObject]` needed)
- Drop-in replacement for MemoryPack
- Works with arbitrary types without registration

### Serialization Pattern

State and DTO classes need a transport serializer attribute with explicit field ordering for version tolerance:

**MemoryPack:**
```csharp
[MemoryPackable]
public partial class MyData
{
    [MemoryPackOrder(0)] public string Name { get; set; }
    [MemoryPackOrder(1)] public int Value { get; set; }
}
```

**MessagePack:**
```csharp
[MessagePackObject]
public partial class MyData
{
    [Key(0)] public string Name { get; set; }
    [Key(1)] public int Value { get; set; }
}
```

**Both (cross-serializer compatibility):**
```csharp
[MemoryPackable, MessagePackObject]
public partial class MyData
{
    [Key(0), MemoryPackOrder(0)] public string Name { get; set; }
    [Key(1), MemoryPackOrder(1)] public int Value { get; set; }
}
```

States are persisted and transmitted as bytes via the chosen transport serializer. Orleans `[GenerateSerializer]` / `[Id(n)]` are **not needed** on game state and DTO classes — those are only used internally by the framework.

### Version Tolerance Rules

- `[MemoryPackOrder(n)]` — MemoryPack version-tolerant deserialization. Without it, MemoryPack uses source declaration order — reordering or inserting fields breaks deserialization.
- `[Key(n)]` — MessagePack version-tolerant deserialization. Unknown keys skipped (forward compatible), missing keys get defaults (backward compatible).

### Adding New Fields (Version Tolerance)

When adding a new field to a persisted class:
```csharp
// MemoryPack example (for MessagePack, replace MemoryPackOrder with Key):
// Existing fields — DO NOT change their ordering
[MemoryPackOrder(0)] public string Name { get; set; }
[MemoryPackOrder(1)] public int Value { get; set; }

// New field — use the next sequential number
[MemoryPackOrder(2)] public string? NewField { get; set; }
```

Rules:
- Never reuse or change existing `[MemoryPackOrder(n)]` / `[Key(n)]` values
- Always append new fields with the next number
- Use nullable types or default values for new fields (old data won't have them)

---

## 10. Session Management

### Architecture

One `SessionManagerGrain` per player. Manages:
- Active session (SessionId)
- Entity subscriptions
- Broadcast ordering (per-entity sequence tracking)
- Missed packet buffer for reconnection (max 1000 packets)
- RPC response batching with broadcasts

### Session Lifecycle

```
1. New Connection
   Client → SessionConnect(playerId, null) → New session, new SessionId

2. Reconnection (same session)
   Client → SessionConnect(playerId, sessionId, lastAcknowledgedSequence)
   Server → Returns missed packets (sequence > lastAcknowledged)
   Server → Re-subscribes to previously subscribed entities

3. Session Supersede (another client connects)
   New Client → SessionConnect(playerId, newSessionId)
   Server → Notifies old observer: "Session superseded"
   Server → Unsubscribes old session from all entities
   Old Client → OnSessionSuperseded event fires
```

### Broadcast Ordering

Each entity has an independent sequence counter (`EntitySequenceNumber`). The `SessionManager` tracks per-entity `KnownEntitySequence` and holds out-of-order broadcasts:

```
Entity broadcasts arrive:    seq=5 (in order) → buffer
                             seq=7 (gap!)     → hold
                             seq=6 (fills gap) → buffer, drain held seq=7

All buffered broadcasts flushed as ONE SessionResponse with ONE session sequence number.
```

### RPC Response Bundling

During an active RPC, all arriving broadcasts are queued. When the RPC completes:
- **Fast path** (no entity sequence gap): RPC result + queued broadcasts in one response
- **Deferred path** (gap detected): Only queued broadcasts returned; RPC result deferred until gap fills

This ensures the client replays operations in the exact order they were applied on the server.

### Piggybacked Acknowledgments

Every `RpcCallRequest` includes `LastAcknowledgedSequence`, avoiding a separate ack roundtrip. The server prunes `_pendingPackets` for acknowledged sequences.

---

## 11. Authentication

### JWT Configuration

**Server:**
```csharp
builder.Services.AddMetaAuth(options =>
{
    options.SecretKey = "your-secret-key-minimum-32-characters";
    options.Issuer = "SharedMeta";
    options.Audience = "SharedMeta";
    options.TokenLifetime = TimeSpan.FromDays(7);
});
app.MapMetaAuthEndpoints();
```

### Login Endpoint

```
POST /meta/auth/login
Body: { "deviceId": "unique-device-id" }
Response: { "token": "jwt...", "playerId": "abc123_20260226", "isNewPlayer": true, "expiresAt": "..." }
```

### Device-Based Auth Flow

1. `AuthGrain` (keyed by DeviceId) maps DeviceId → PlayerId
2. First login: generates PlayerId (`{random8hex}_{yyyyMMdd}`)
3. Subsequent logins: returns existing PlayerId
4. JWT token contains `sub` (PlayerId), `auth_type` ("device"), `jti` (unique ID)

### Transport Integration

- **SignalR**: Token via query string `?access_token=jwt_token`
- **HTTP Polling**: Token via `Authorization: Bearer jwt_token` header
- Server extracts PlayerId from JWT claims, overrides request PlayerId

### Entity Access Policy

```csharp
[MetaService(StateType = typeof(GameState), AccessPolicy = EntityAccessPolicy.Authorized)]
public interface IGameService : IMetaService { ... }
```

| Policy | Server Behavior | Client API |
|--------|----------------|------------|
| `Open` | Anyone can subscribe | `client.GetServiceAsync<TApiClient>(entityId)` |
| `OwnerOnly` | Only if entityId == playerId | `client.GetServiceAsync<TApiClient>(entityId)` |
| `UserOwned` | Only if entityId == playerId | **Convenience:** `client.Get{ServiceName}Async()` (auto uses PlayerId) |
| `Authorized` | Custom `IsAuthorized(playerId)` on service impl | `client.GetServiceAsync<TApiClient>(entityId)` |

**UserOwned** is the only policy that generates convenience extension methods on `MetaClient`:
- `Get{ServiceName}Async()` — subscribes and returns typed API client, auto uses `client.PlayerId` as entityId
- `Get{StateName}()` — returns state, auto uses `client.PlayerId` as entityId

For all other policies, use the generic `GetServiceAsync<TApiClient>(entityId)` with explicit entityId.

**Authorized** services must implement `IsAuthorized` in the service implementation:
```csharp
public bool IsAuthorized(string playerId)
{
    return State.OwnerPlayerId == playerId;
}
```

---

## 12. Persistence Configuration

### FileGrainStorage

File-based Orleans grain persistence. Uses `IMetaSerializer` for serialization.

```csharp
siloBuilder.AddFileGrainStorage("Default", o => o.RootDirectory = "./data");
```

File layout: `{RootDirectory}/{stateName}/{sanitizedGrainId}.bin`

Features:
- Atomic writes (temp file + move)
- ETag concurrency (file last-write-time)
- Per-file semaphore locking

### MemoryPack for Storage

`SharedMeta.Server.Core` uses MemoryPack by default for `FileGrainStorage`. Opt-out:

```xml
<PropertyGroup>
  <DefineConstants>$(DefineConstants);DisableMemoryPack</DefineConstants>
</PropertyGroup>
```

When disabled, `MemoryPackableAttribute` is replaced with a stub (no-op).

### Persistence Policy

Configure how often `EntityGrain` persists state:

```csharp
services.Configure<EntityGrainOptions>(o =>
{
    o.PersistencePolicy = PersistencePolicy.RequestsOrTime(10, 5.0);
});
```

| Policy | Factory Method | Behavior |
|--------|---------------|----------|
| `EveryCall` | `PersistencePolicy.EveryCall()` | Save after every RPC (default, safest) |
| `EveryNRequests` | `PersistencePolicy.EveryNRequests(10)` | Save every N requests |
| `EveryNMinutes` | `PersistencePolicy.EveryNMinutes(5.0)` | Save when M minutes passed (checked per request, not by timer) |
| `RequestsOrTime` | `PersistencePolicy.RequestsOrTime(10, 5.0)` | N requests OR M minutes, whichever first |
| `OnDeactivationOnly` | `PersistencePolicy.OnDeactivationOnly()` | Max performance, risk of data loss on crash |

**Always persisted regardless of policy:**
- Subscribe/unsubscribe operations
- Errors (sequence number already incremented)
- Grain deactivation
- Methods marked with `[MetaMethod(ForcePersist = true)]`

### ForcePersist

Mark critical methods that must always persist state, regardless of the configured policy:

```csharp
[MetaMethod(ForcePersist = true, Mode = ExecutionMode.Server)]
bool ProcessPurchase(string itemId, int price);

[MetaMethod] // Normal method — follows policy
void UpdateNickname(string name);
```

`ForcePersist` is propagated through the full pipeline: source generator reads the attribute → emits `ForcePersist = true` in `DispatchResult` → `MetaProviderBase` copies to `HandleCallResult` → `EntityGrain` calls `WriteStateAsync()` immediately.

Use for: purchases, currency operations, inventory changes, and any operation where data loss on crash is unacceptable.

---

## 13. Orleans Backend

### Why Orleans

Orleans is a virtual actor framework. Each entity is an Orleans grain — a single-threaded, location-transparent actor with persistence.

**Key benefits:**
- **Single-threaded entities**: No locks, no race conditions in game logic
- **Location transparency**: Grains can be on any silo in the cluster
- **Automatic lifecycle**: Grains activate on first call, deactivate on inactivity
- **Built-in persistence**: `IPersistentState<T>` with pluggable storage
- **Scalability**: Add silos to scale horizontally; grains distribute automatically
- **Streaming**: Orleans Streams for pub/sub (used for broadcasts)

### Grain Architecture

```
SessionManagerGrain (per player)
  │  Manages sessions, subscriptions, broadcast ordering
  │
  ├──→ EntityGrain<GameState> (per game entity)
  │      │  State persistence, subscriber management
  │      │  Calls MetaProvider for business logic
  │      │
  │      └──→ EntityGrain<ProfileState> (cross-entity call)
  │
  ├──→ LobbyGrain (per game mode, singleton)
  │      │  Matchmaking queue, match formation
  │      │  Notifies entities via HandleExternalEventAsync
  │
  └──→ AuthGrain (per device)
         │  Device → PlayerId mapping
```

### Scalability Model

- **Single silo (dev)**: `UseLocalhostClustering()` — everything on one machine
- **Multi-silo (prod)**: Use ADO.NET/Azure/Consul clustering — grains auto-distribute
- **Entity isolation**: Each entity grain is independent; 100K+ concurrent entities per silo
- **Session isolation**: Each player's session is independent
- **No shared mutable state**: All state owned by individual grains

### Clustering Example (Production)

```csharp
siloBuilder
    .UseAdoNetClustering(options =>
    {
        options.ConnectionString = "...";
        options.Invariant = "Npgsql";
    })
    .AddAdoNetGrainStorage("Default", options =>
    {
        options.ConnectionString = "...";
        options.Invariant = "Npgsql";
    });
```

---

## 14. Server Setup

### Minimal Server

```csharp
var builder = WebApplication.CreateBuilder(args);

// Serializer
var serializer = new MemoryPackMetaSerializer();
builder.Services.AddSingleton<IMetaSerializer>(serializer);

// Transformers
var transformerRegistry = new TransformerRegistry();
TransformerRegistrations.RegisterAll(transformerRegistry);  // generated

// Orleans
builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddFileGrainStorage("Default", o => o.RootDirectory = "./data")
        .ConfigureServices(services =>
        {
            services.ConfigureMeta(svc =>
            {
                // Register server-only services
                svc.AddTransient<IRandomService, RandomServiceImpl>();
            });
        });
});

// Transport: SignalR
builder.Services.AddSignalR().AddMetaMessagePackProtocol();

// Connection handler factory
builder.Services.AddSingleton<IMetaConnectionHandlerFactory>(sp =>
    new MetaConnectionHandlerFactory(
        sp.GetRequiredService<IGrainFactory>(),
        sp.GetRequiredService<ILoggerFactory>()));

var app = builder.Build();

// Map endpoints
app.MapHub<MetaHub>("/meta");
app.MapMetaHttpEndpoints("/meta-http");  // optional: HTTP polling

app.Run();
```

### With Authentication

```csharp
builder.Services.AddMetaAuth(options =>
{
    options.SecretKey = "your-32-char-minimum-secret-key!!";
});

// After app.Build():
app.UseAuthentication();
app.UseAuthorization();
app.MapMetaAuthEndpoints();
app.MapHub<MetaHub>("/meta");
```

### With Logging

```csharp
builder.Host.UseSerilog((ctx, config) => config
    .WriteTo.Console()
    .WriteTo.File("logs/server-.log", rollingInterval: RollingInterval.Day));
```

---

## 15. Client Setup

### Basic Client

```csharp
// Transport
var connection = new SignalRConnection("https://localhost:5001/meta");

// Serializer (must match server)
var serializer = new MemoryPackMetaSerializer();

// Client
var client = new MetaClient(connection, serializer, new MetaClientOptions
{
    PlayerId = "player-123",
    Diagnostics = new ConsoleDesyncDiagnostics()
});

// Register services (generated method)
TransformerRegistrations.RegisterAll(client.TransformerRegistry);
client.Resolver.RegisterAllServices();

// Connect
await client.ConnectAsync();
```

### With Authentication

```csharp
var login = await MetaClient.LoginAsync(
    $"{serverUrl}/meta/auth",
    deviceId: "unique-device-id"
);

var connection = new SignalRConnection(
    $"{serverUrl}/meta",
    accessToken: login.Token
);

var client = new MetaClient(connection, serializer, new MetaClientOptions
{
    PlayerId = login.PlayerId
});
```

### Connection Event Handling

```csharp
client.Dispatcher.OnConnectionStatusChanged += (status, detail) =>
{
    switch (status)
    {
        case ConnectionStatus.Connected:
            Console.WriteLine("Connected");
            break;
        case ConnectionStatus.Reconnecting:
            Console.WriteLine("Connection lost, reconnecting...");
            break;
        case ConnectionStatus.Reconnected:
            Console.WriteLine("Reconnected, restoring session...");
            break;
        case ConnectionStatus.Failed:
            Console.WriteLine($"Connection failed: {detail}");
            break;
        case ConnectionStatus.Disconnected:
            Console.WriteLine("Disconnected");
            break;
    }
};

client.OnSessionSuperseded += reason =>
{
    Console.WriteLine($"Session taken over: {reason}");
    // Option: await client.RestartSessionAsync();
};
```

### Using Services

```csharp
// --- UserOwned services (AccessPolicy = EntityAccessPolicy.UserOwned) ---
// Generated convenience methods — no entityId needed (auto uses client.PlayerId)
var profileApi = await client.GetProfileServiceAsync();
var profileState = client.GetProfileState();

// --- All other services (Authorized, Open, OwnerOnly) ---
// Must provide entityId explicitly
var gameApi = await client.GetServiceAsync<CardGameServiceApiClient>("game-entity-1");
var gameState = client.GetState<GameState>("game-entity-1");

// Call methods
bool success = await gameApi.PlayCardAsync(selectedCard);

// Subscribe to specific method broadcasts
var sub = client.Resolver.OnMethodReplayed("game-entity-1",
    "ICardGameService", "PlayCard",
    ctx => Console.WriteLine("Another player played a card"));
```

### Frame-Based Processing (Required for Game Engines)

By default, `ImmediateMode` is `false` — broadcasts are queued and must be
processed explicitly from the game loop:

```csharp
// Unity MonoBehaviour.Update():
void Update()
{
    dispatcher.ProcessPendingBroadcasts();
}

// .NET game loop:
while (running)
{
    client.Dispatcher.ProcessPendingBroadcasts();
    Render();
}
```

For console apps or tests where threading is not a concern:
```csharp
dispatcher.ImmediateMode = true; // Process as they arrive
```

**Why**: Broadcast handlers execute service methods that modify state.
If processed from a transport thread, this races with the game thread
calling API methods on the same state. Always process on the same thread
that calls API methods.

---

## 16. Matchmaking (Lobby)

### Lobby Pattern

`LobbyGrain` is a singleton grain (per game mode) that manages matchmaking queues.

```csharp
// In IProfileService implementation:
public async Task RequestMatch(int playerCount)
{
    var lobbyRequester = Context.ResolveService<ILobbyRequester>();
    await lobbyRequester.RequestMatchAsync(
        Context.EntityId, Context.CallerId!, playerCount);
}
```

### LobbyGrain Flow

1. Player calls `RequestMatch` → their profile entity calls `LobbyGrain.RequestMatchAsync()`
2. `LobbyGrain` adds player to queue, periodically checks for enough players
3. When match forms: calls `EntityGrain.HandleExternalEventAsync()` for each matched player
4. Entity's `[ServiceTrigger]` fires, updating the player's state with match info
5. All subscribers of each entity receive the match notification as a broadcast

### Client-Side Match Notification

```csharp
// Subscribe to match found event
client.Resolver.OnMethodReplayed<MatchFoundArgs>(
    profileEntityId,
    "ILobbySubscriber",
    "OnMatchFound",
    args =>
    {
        Console.WriteLine($"Match found! ID: {args.MatchId}");
        // Join the match entity
    }
);

// Request match via profile service
await profileApi.RequestMatchAsync(2);
```

---

## 17. Desync Diagnostics

### IDesyncDiagnostics Interface

```csharp
public interface IDesyncDiagnostics
{
    void OnResultMismatch<T>(string serviceName, string methodName,
        T serverResult, T localResult);
    void OnRandomDesync(string serviceName, string methodName,
        long serverDelta, long localDelta);
    void OnCrossEntityResult(string entityId, string serviceName,
        string methodName, byte[]? resultBytes);
    Task<StateComparisonResult> CompareFullStateAsync(string entityId);
}
```

### Common Desync Causes

1. **Using `System.Random`** instead of `Context.Random` — different seeds
2. **Using `DateTime.Now`** instead of `Context.ServerTimeTicks` — clock difference
3. **Dictionary iteration order** — different on client vs server
4. **Floating point operations** — platform-dependent precision (see below)
5. **LINQ with unordered collections** — `FirstOrDefault` on HashSet
6. **Async methods in broadcast replay** — must complete synchronously. If a method returns `Task`, the framework logs an error if the Task is not immediately completed. Check for network calls or async subscribe in the replay path.

### Floating Point Is Not Deterministic

`float` and `double` arithmetic is **not portable** across platforms. The same expression can produce different results on:
- Server (.NET on Linux x64) vs Client (Unity IL2CPP on ARM64/iOS)
- Different CPU architectures (x86 SSE vs ARM NEON)
- Different JIT compilers (.NET RyuJIT vs Mono)
- Even different optimization levels on the same platform

**This means any `float`/`double` computation in shared logic (Optimistic/CrossOptimistic methods) can cause desyncs.**

What is safe:
- `int`, `long`, `decimal` — deterministic everywhere
- `Context.Random!.Next(int max)` — returns `int`, fully deterministic
- Storing `float` values received from `Context.ServerRandom` (Server mode replay — value is recorded, not recomputed)

What is **not** safe in shared logic:
- `float` / `double` arithmetic (`a * b + c`)
- `Math.Sin`, `Math.Sqrt`, `MathF.*` — implementation-defined precision
- `Context.Random!.NextFloat()` in Optimistic mode — the `float` division can produce different results
- Any `float` comparison (`a == b`, `a < b`) after arithmetic

### Fixed-Point Arithmetic

For deterministic math in shared logic, use a **fixed-point** library. Fixed-point types represent numbers as scaled integers — all operations reduce to integer arithmetic, which is deterministic on all platforms.

**Recommended: [FixedPointSharp](https://github.com/sschoener/FixedPointSharp)**

```csharp
// fp is a 32-bit fixed-point type (16.16 format)
fp speed = fp._0_50;                   // 0.5
fp distance = fp._10 * speed;          // 5.0, deterministic
bool hit = distance < fp._6;           // true, deterministic

// Convert to/from int
fp value = fp.FromInt(42);
int rounded = value.AsInt;

// Math operations
fp angle = fpmath.Atan2(dy, dx);       // deterministic trig
fp length = fpmath.Sqrt(x * x + y * y); // deterministic sqrt
```

**Integration pattern:**
```csharp
// In state — store as int (scaled) for serialization
[MemoryPackOrder(0)] public int PositionXRaw { get; set; }  // fp.RawValue

// In service — work with fp
public void Move(int dx, int dy)
{
    fp px = fp.FromRaw(State.PositionXRaw);
    px += fp.FromInt(dx) * State.Speed;
    State.PositionXRaw = px.RawValue;
}
```

**Alternatives:**
- **[FixedMath.Net](https://github.com/asik/FixedMath.Net)** — `Fix64` type (32.32 format), wider range, C#-native
- **[libfixmath](https://github.com/PetteriAimworlds/libfixmath)** — C library with C# bindings, battle-tested in embedded systems
- **Manual scaling** — use `long` with a fixed scale factor (e.g., `× 1000`) for simple cases

**Rule of thumb:** If a value participates in Optimistic or CrossOptimistic logic and requires non-integer math, use fixed-point. Server-only logic (`ExecutionMode.Server`) can use `float` safely since only the server computes it.

---

## 18. Code Generation Reference

The source generator (`SharedMeta.Generator`) scans assemblies for attributes and generates:

| Input | Output | Description |
|-------|--------|-------------|
| `[MetaService]` interface | `*Dispatcher.g.cs` | Server-side method routing (switch-based) |
| `[MetaService]` interface | `*ApiClient.g.cs` | Typed async client with execution mode handling |
| `[MetaService]` interface | `*ServiceExtensions.g.cs` | DI registration helpers |
| `[MetaServiceImpl]` class | `*.Context.g.cs` | Context injection (State, CallerId, dependencies) |
| Assembly with `[MetaService]` | `ServerMetaConfiguration.g.cs` | MetaProvider generation, service wiring |
| `[Transformer]` class | `TransformerRegistrations.g.cs` | Auto-registration of all transformers |

### Dispatcher Pattern (generated)

```csharp
// Generated switch-based routing (no reflection)
public DispatchResult DispatchCall(string serviceName, string methodName, byte[] payload)
{
    return serviceName switch
    {
        "ICardGameService" => DispatchCardGameService(methodName, payload),
        "IProfileService" => DispatchProfileService(methodName, payload),
        _ => throw new InvalidOperationException($"Unknown service: {serviceName}")
    };
}
```

### Method Signature Validation (generated)

The generator produces `MetaMethodSignatureValidator` with FNV-1a 64-bit hashes of every method's canonical signature. Validation runs at connection time — if client and server signatures don't match, the connection is rejected with a list of mismatches.

**Canonical signature format:**
```
{ServiceName}.{MethodAlias}({ParamType1},{ParamType2},...)->{ReturnType}
```

Examples:
```
IProfileService.SetName(string)->void
ICardGameService.PlayCard(Card)->bool
IExpeditionService.Move(int,int)->MoveResult
```

**What triggers a signature mismatch:**
- Changing a method's parameter types or order
- Changing a method's return type
- Renaming a method (without updating `Alias`)
- Adding/removing parameters

**What does NOT trigger a mismatch:**
- Adding new methods (server has extra methods — OK)
- Changing execution mode (`[MetaMethod(Mode = ...)]`)
- Changing `Version`, `SkipServerOnFalse`, `ForcePersist`

**Generated validator (server-side):**
```csharp
public static class MetaMethodSignatureValidator
{
    public static readonly Dictionary<string, ulong> ServerSignatures = new()
    {
        { "IProfileService.SetName", 0xA1B2C3D4E5F60718UL },
        { "ICardGameService.PlayCard", 0x1234567890ABCDEFUL },
        // ... all methods
    };

    // Returns null if valid, list of mismatch descriptions otherwise
    public static List<string>? ValidateClientSignatures(
        Dictionary<string, ulong> clientSignatures);
}
```

### Method Version (`MetaMethod.Version`)

The `Version` property on `[MetaMethod]` is transmitted in `RpcCall.MethodVersion`. Use it for gradual rollout when you need to support old and new clients simultaneously:

```csharp
[MetaMethod(Mode = ExecutionMode.Optimistic, Version = 0)]
bool PlayCard(Card card);

// New version with additional parameter
[MetaMethod(Mode = ExecutionMode.Optimistic, Version = 1, Alias = "PlayCard")]
bool PlayCardV2(Card card, bool autoDefend);
```

The server dispatcher receives `MethodVersion` and can route to different implementations. Combined with signature validation, this allows controlled API evolution:

1. **Signature validation** catches accidental breaking changes at connection time
2. **MethodVersion** enables intentional coexistence of old and new method signatures

---

## 19. Attribute Reference

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[MetaService]` | Interface | Marks shared service for code generation |
| `[MetaMethod]` | Method | Configures execution mode, alias, versioning |
| `[MetaServiceImpl]` | Class | Marks service implementation for context injection |
| `[SharedState]` | Class | Marks shared state entity |
| `[Trigger]` | Method | Auto-execute after condition on another method |
| `[ServiceTrigger]` | Method | Trigger on framework service event |
| `[Subscribe]` | Event | Declare method subscription |
| `[ServerMetaService]` | Interface | Server-only service (generates replayer) |
| `[Transformer]` | Class | Register argument transformer |
| `[Transform]` | Parameter | Explicit transformer for parameter |
| `[SkipTransform]` | Parameter | Disable auto-transformation |
| `[OrderedExecution]` | Interface | Broadcast ordering mode |
| `[MetaSerializer]` | Assembly | Serializer type configuration |
| `[MemoryPackable]` | Class | MemoryPack transport serialization |
| `[MessagePackObject]` | Class | MessagePack transport serialization |
| `[MemoryPackOrder(n)]` | Property | MemoryPack field ordering for version tolerance |
| `[Key(n)]` | Property | MessagePack field ordering for version tolerance |

### MetaMethod Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Mode` | ExecutionMode | Optimistic | Execution strategy |
| `Alias` | string | method name | RPC method identifier |
| `Version` | int | 0 | Method version |
| `GenerateClientApi` | bool | true | Generate API client method |
| `SkipServerOnFalse` | bool | false | Skip server if local returns false/default |
| `ForcePersist` | bool | false | Always persist after execution |

### MetaService Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StateType` | Type | required | State class type |
| `AccessPolicy` | EntityAccessPolicy | Open | Subscribe access control |
| `SubscriberInterfaces` | Type[] | empty | Framework event subscriptions |

---

## 20. Testing

### In-Process Testing

Use `InProcessServer` + `InProcessConnection` to test the full pipeline (client → session → entity → provider) in a single process without network:

```csharp
// 1. Set up Orleans TestCluster (once per test class)
var builder = new TestClusterBuilder();
builder.AddSiloBuilderConfigurator<SiloConfigurator>();
var cluster = builder.Build();
await cluster.DeployAsync();

// 2. Create in-process server
var server = new InProcessServer(fixture.CreateHandlerFactory());

// 3. Create and connect a test client
await using var client = new TestClientSetup(server, playerId: "player1");
await client.ConnectAsync();

// 4. Use generated API clients as usual
var resolver = client.CreateResolver();
var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
await api.AddValueAsync(10, 1);

// 5. Verify state
var state = resolver.GetState<CounterState>(entityId);
Assert.Equal(10, state.Sum);

// 6. Check for desyncs
Assert.Empty(client.DetectedIssues);
```

Multi-client scenarios use separate `TestClientSetup` instances sharing the same `InProcessServer`:

```csharp
await using var client1 = new TestClientSetup(server, "player1");
await using var client2 = new TestClientSetup(server, "player2");
// Both subscribe to the same entityId → broadcasts are delivered to both
```

### Orleans TestCluster

For integration tests that exercise real Orleans grain lifecycle (activation, deactivation, persistence, cross-entity calls), use `Orleans.TestingHost.TestCluster`. The silo configurator registers all meta services:

```csharp
private class SiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder
            .AddMemoryGrainStorage("Default")
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
                services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(5));
                services.ConfigureTestMeta(); // Generated DI registration
            });
    }
}
```

**When to use which:**
- **InProcess only** — sufficient for testing business logic, state mutations, and deterministic replay
- **Orleans TestCluster** — needed for session management, broadcast ordering, reconnection, cross-entity calls, and persistence

See `tests/SharedMeta.IntegrationTests/` for complete examples.

---

## 21. Capability Overview

| Category | Capabilities |
|----------|-------------|
| **Core** | Shared state definitions, source-generated dispatchers/API clients, context injection, 4 execution modes (Local, Optimistic, Server, CrossOptimistic) |
| **Networking** | SignalR (WebSocket), HTTP Long-Polling, InProcess (testing). All transports implement `IConnection` — swappable at configuration time |
| **Session** | Per-player session management, reconnection with missed packet replay, request idempotency via RequestId, session supersede (single active session per player) |
| **Broadcast Ordering** | Per-entity sequence ordering, RPC broadcast bundling, deferred responses for gap filling |
| **Security** | Optional JWT authentication (DeviceId → PlayerId), entity access policies (Open, OwnerOnly, UserOwned, Authorized), per-method `ForcePersist` for critical operations |
| **Advanced** | Cross-entity calls via Orleans grains, server-side triggers (`[Trigger]`), framework service subscribers (`[ServiceTrigger]`), argument transformers (stateless and state-aware) |
| **Deterministic Random** | `Context.Random` (optimistic, xoshiro128**) — identical on client and server. `Context.ServerRandom` — server-only with replay. ScrollId delta for desync detection |
| **Time Sync** | `Context.ServerTimeTicks` — synchronized UTC ticks for deterministic time-based mechanics (cooldowns, timers, regeneration) |
| **Serialization** | MemoryPack (transport) + Orleans GenerateSerializer (persistence). MessagePack alternative via `IMetaSerializer` |
| **Persistence** | FileGrainStorage, configurable persistence policy (5 modes), per-method ForcePersist override |
| **Code Generation** | Service dispatchers, typed API clients, context injection, DI registration, MetaProvider routing — all generated at compile time |

### Planned

- Unit & integration test framework improvements
- Multi-node cluster deployment support
- Unity UPM package with editor tools and IL2CPP/WebGL support

---

## 22. Tutorial: Building Your First Service

Step-by-step guide from empty project to working service.

### Step 1: Define State

```csharp
[MemoryPackable]
public partial class InventoryState : ISharedState
{
    [MemoryPackOrder(0)] public Dictionary<string, int> Items { get; set; } = new();
    [MemoryPackOrder(1)] public int Gold { get; set; }
}
```

`[MemoryPackable]` + `[MemoryPackOrder(n)]` provides transport serialization and version tolerance. For MessagePack, use `[MessagePackObject]` + `[Key(n)]` instead.

### Step 2: Define Service Interface

```csharp
[MetaService(StateType = typeof(InventoryState))]
public interface IInventoryService : IMetaService
{
    [MetaMethod(SkipServerOnFalse = true)]
    bool AddItem(string itemId, int count);

    [MetaMethod(ForcePersist = true, Mode = ExecutionMode.Server)]
    bool Purchase(string itemId, int price);

    [MetaMethod(Mode = ExecutionMode.Local)]
    int GetItemCount(string itemId);
}
```

### Step 3: Implement Service

```csharp
[MetaServiceImpl(typeof(IInventoryService), typeof(InventoryState))]
public partial class InventoryService : IInventoryService
{
    // Generated: Context property with State, CallerId, etc.

    public bool AddItem(string itemId, int count)
    {
        if (count <= 0) return false;
        var items = Context.State.Items;
        items[itemId] = items.GetValueOrDefault(itemId) + count;
        return true;
    }

    public bool Purchase(string itemId, int price)
    {
        if (Context.State.Gold < price) return false;
        Context.State.Gold -= price;
        AddItem(itemId, 1);
        return true;
    }

    public int GetItemCount(string itemId)
    {
        return Context.State.Items.GetValueOrDefault(itemId);
    }
}
```

### Step 4: Server Configuration

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddFileGrainStorage("Default", o => o.RootDirectory = "./data");
    silo.ConfigureServices(services =>
    {
        services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
        services.ConfigureMeta(); // Generated: registers all providers and factories
    });
});

builder.Services.AddSignalR();
var app = builder.Build();
app.MapMetaHub("/meta");  // SignalR endpoint
app.Run();
```

### Step 5: Client Configuration

```csharp
var serializer = new MemoryPackMetaSerializer();
var resolver = new MetaServiceResolver(
    entityId => new SignalRConnection("https://localhost:5001/meta"),
    serializer
);
resolver.AddInventoryServiceServices(); // Generated DI extension

var api = await resolver.GetServiceAsync<InventoryServiceApiClient>(playerId);
var success = await api.PurchaseAsync("sword", 100);
```

### AI Agent Automation

The entire workflow — defining states, service interfaces, implementations, and server/client configuration — can be automated using AI code assistants like Claude Code. The `CLAUDE.md` file at the project root provides full context about the framework architecture, code generation patterns, and conventions for AI agents to follow.

### Quick Reference Checklists

**Add a new method to existing service:**
1. Add method to `IYourService` interface with `[MetaMethod]` attribute
2. Implement in `YourService` class
3. Build — generator updates dispatcher and API client automatically

**Add a new service:**
1. Create state class with `[MemoryPackable]`/`[MessagePackObject]`, `ISharedState`, `[MemoryPackOrder(n)]`/`[Key(n)]` on properties
2. Create interface with `[MetaService(StateType = typeof(YourState))]`
3. Create implementation with `[MetaServiceImpl]`
4. Build — generator produces dispatcher, API client, DI extensions, MetaProvider routing

**Add a new entity type:**
1. Define state and service(s) as above
2. Server: `ConfigureMeta()` picks up everything automatically
3. Client: call `resolver.AddYourServiceServices()` and `GetServiceAsync<YourServiceApiClient>(entityId)`

---

## 23. Architecture Decisions

Key design decisions and their rationale.

### Code Generation over Reflection

All service dispatching uses compile-time generated switch-case routing instead of runtime delegate dictionaries or reflection. This provides:
- Compile-time validation of service/method existence
- Better performance (no dictionary lookups or delegate invocations)
- Full IDE support (go-to-definition, find-references)

### Layer Separation

The framework is organized into independent layers: Meta (business logic) → Middleware (context, replay) → Serialization → Transport → Server Backend. Each layer depends only on layers above it. This makes it easy to swap implementations — different serializer, different transport, different backend — without touching business logic.

### Async by Default

All server-side `IMetaProvider` methods are async (`Task<HandleCallResult>`). Even if a service method is synchronous, the pipeline is async because:
- Cross-entity calls require Orleans grain-to-grain RPC
- External service integration (lobby, leaderboard) is inherently async
- Orleans grain activation/deactivation is async

### Transport Serialization

Game state and DTO classes need a transport serializer attribute with field ordering:
- **MemoryPack**: `[MemoryPackable]` + `[MemoryPackOrder(n)]` on properties
- **MessagePack**: `[MessagePackObject]` + `[Key(n)]` on properties
- **Both**: `[MemoryPackable, MessagePackObject]` + `[Key(n), MemoryPackOrder(n)]`

Orleans `[GenerateSerializer]`/`[Id(n)]` are not needed on game state/DTO classes — those are only used internally by the framework for grain-to-grain calls.

### Deterministic Random

`System.Random` is forbidden in shared logic — different implementations on client/server cause desyncs. The framework provides:
- `Context.Random` (optimistic) — identical xoshiro128** PRNG on both sides, seeded from entityId
- `Context.ServerRandom` — real random on server, recorded results replayed on client
- `ScrollId` tracking for automatic desync detection

### Broadcast Ordering

Three mechanisms ensure clients process state changes in correct order:
1. **Per-entity ordering** — SessionManager tracks `KnownEntitySequence` per entity; out-of-order broadcasts are held until gaps fill
2. **RPC broadcast bundling** — during active RPC, all incoming broadcasts are queued and bundled as `PrecedingBroadcasts` in the response
3. **Deferred responses** — when RPC result arrives before preceding broadcasts, the result is deferred until the gap fills; client completes the pending Task when the deferred response is pushed
