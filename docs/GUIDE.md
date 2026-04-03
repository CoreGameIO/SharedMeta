# SharedMeta Framework Guide

Complete technical reference for the SharedMeta framework. Covers all subsystems, configuration options, and patterns.

> This document serves both as a developer guide and as context for AI code assistants working with the codebase.

## Table of Contents

0. [Quick Start (5 Minutes)](#0-quick-start-5-minutes)
1. [Architecture Overview](#1-architecture-overview)
2. [Shared State & Services](#2-shared-state--services)
3. [Static Game Configuration](#3-static-game-configuration)
4. [Execution Modes & Replay](#4-execution-modes--replay)
5. [Deterministic Random](#5-deterministic-random)
6. [Cross-Entity Calls](#6-cross-entity-calls)
7. [Triggers & Subscribers](#7-triggers--subscribers)
8. [Push-Based Change Tracking](#8-push-based-change-tracking)
9. [Argument Transformers](#9-argument-transformers)
10. [Transport Configuration](#10-transport-configuration)
11. [Serialization](#11-serialization)
12. [Session Management](#12-session-management)
13. [Authentication](#13-authentication)
14. [Persistence Configuration](#14-persistence-configuration)
15. [Orleans Backend](#15-orleans-backend)
16. [Server Setup](#16-server-setup)
17. [Client Setup](#17-client-setup)
18. [Matchmaking (Lobby)](#18-matchmaking-lobby)
19. [Desync Diagnostics & Common Pitfalls](#19-desync-diagnostics--common-pitfalls)
20. [Code Generation Reference](#20-code-generation-reference)
21. [Attribute Reference](#21-attribute-reference)
22. [Testing](#22-testing)
23. [Capability Overview](#23-capability-overview)
24. [Tutorial: Building Your First Service](#24-tutorial-building-your-first-service)
25. [Example: Expedition (Cross-Entity Economy)](#25-example-expedition-cross-entity-economy)
26. [Architecture Decisions](#26-architecture-decisions)

---

## 0. Quick Start (5 Minutes)

A minimal "Hello World" service in 5 steps — from zero to a working client-server call.

### Step 1: Define State

```csharp
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public partial class GameState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public int Counter { get; set; }
}
```

### Step 2: Define Service Interface

```csharp
[MetaService(StateType = typeof(GameState))]
public interface IGameService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    int Increment(int amount);
}
```

### Step 3: Implement

```csharp
[MetaServiceImpl(typeof(IGameService), typeof(GameState))]
public partial class GameServiceImpl : IGameService
{
    // State is auto-injected by the source generator
    public int Increment(int amount)
    {
        State.Counter += amount;
        return State.Counter;
    }
}
```

Build the project — the source generator produces `GameServiceDispatcher`, `GameServiceApiClient`, and DI extensions automatically.

### Step 4: Server (Program.cs)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddFileGrainStorage("Default", o => o.RootDirectory = "./data");
    silo.ConfigureServices(services =>
    {
        services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
        services.ConfigureMeta(); // Generated: registers providers and factories
    });
});
builder.Services.AddSignalR();
var app = builder.Build();
app.MapMetaHub("/meta");
app.Run();
```

### Step 5: Client

```csharp
var client = new MetaClient(
    new SignalRConnection("http://localhost:5000/meta"),
    new MemoryPackMetaSerializer()
);
var resolver = (MetaServiceResolver)client.Resolver;
resolver.RegisterAllServices(); // Generated
await client.ConnectAsync();

var api = await client.GetGameServiceAsync(); // Generated extension
var result = await api.IncrementAsync(5);     // Executes locally + sends to server
Console.WriteLine($"Counter: {result}");      // Counter: 5
```

**Unity?** Use **SharedMeta > Project Wizard** — it generates all of the above in one click. See [Tutorial](#24-tutorial-building-your-first-service) for a detailed walkthrough.

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
[MemoryPackable(GenerateType.VersionTolerant)]  // or [MessagePackObject], or both
public partial class GameState : ISharedState
{
    [MemoryPackOrder(0)] public int Score { get; set; }
    [MemoryPackOrder(1)] public List<Player> Players { get; set; } = new();
    [MemoryPackOrder(2)] public GamePhase Phase { get; set; }
}
```

`[MemoryPackOrder(n)]` (or `[Key(n)]` for MessagePack) provides version tolerance — you can add new fields without breaking existing persisted state. `GenerateType.VersionTolerant` ensures MemoryPack stores field orders explicitly, allowing safe addition/removal of fields in persisted data. States are persisted and transmitted as bytes via the chosen transport serializer; Orleans `[GenerateSerializer]`/`[Id(n)]` are not needed on game state/DTO classes.

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

### State Initialization (`[MetaInit]`)

Use `[MetaInit]` on a method in your `[MetaServiceImpl]` class to initialize or migrate state when the entity grain activates. The method is called server-side during `OnActivateAsync` — it is **not** broadcast to clients (clients receive the already-initialized state via snapshot on subscribe).

```csharp
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState))]
public partial class ProfileServiceImpl : IProfileService
{
    [MetaInit]
    public Task<int> InitState(int version)
    {
        if (version < 1)
        {
            state.Energy = 50;
            state.MaxEnergy = 50;
            state.Money = 100;
            return Task.FromResult(1);
        }
        // Future migrations:
        // if (version < 2) { state.NewField = ...; return Task.FromResult(2); }
        return Task.FromResult(version);
    }
}
```

**How it works:**
- `EntityGrainState` stores `int Version`, persisted alongside entity state
- On grain activation, the framework calls `InitializeStateAsync(currentVersion)` on the generated provider
- The returned version is saved to `EntityGrainState.Version`
- The grain is **not** persisted after init alone — persistence only happens when a player interacts with the entity (the `_isDirty` flag is not set by init)
- This prevents creating persistent state for grains that were activated but never used

**Signature:** `Task<int> MethodName(int version)` — takes current version, returns new version.

**Available during `[MetaInit]`:**
- `Context.Random` and `Context.ServerRandom` — available for deterministic initialization (e.g., map generation)
- `Config` — available if a config type is configured for the service (config is resolved before init runs)
- `State` — the entity state to initialize/migrate

**Note:** `[MetaInit]` is a server-only step. Random values used during init are not replayed on the client — the client receives the already-initialized state snapshot.

---

## 3. Static Game Configuration

Static game configuration allows defining balance parameters, level data, and other read-only data separately from entity state. Config is provided by the server and available in service methods via `Config` / `Context.Config`.

### Defining a Config Type

Mark a class with `[MetaConfig]`:

```csharp
[MetaConfig(Default = true)]
[MemoryPackable, MessagePackObject]
public partial class GameConfig
{
    [Key(0), MemoryPackOrder(0)] public int MaxEnergy { get; set; } = 100;
    [Key(1), MemoryPackOrder(1)] public int EnergyRegenMinutes { get; set; } = 5;
    [Key(2), MemoryPackOrder(2)] public int StarterGold { get; set; } = 500;
}
```

- `Default = true` — this config is automatically used by services with `DefaultConfig = true`
- Only one config class should be marked as `Default` per assembly

### Linking Config to a Service

```csharp
// Option 1: Use the default config (marked with [MetaConfig(Default = true)])
[MetaService(StateType = typeof(GameState), DefaultConfig = true)]
public interface IGameService : IMetaService { ... }

// Option 2: Explicit config type
[MetaService(StateType = typeof(GameState), ConfigType = typeof(GameConfig))]
public interface IGameService : IMetaService { ... }
```

### Accessing Config in Service Code

The source generator injects a typed `Config` property:

```csharp
[MetaServiceImpl(typeof(IGameService), typeof(GameState))]
public partial class GameServiceImpl : IGameService
{
    // Auto-injected by generator:
    //   protected GameConfig Config => (GameConfig)Context.Config!;

    public bool RegenerateEnergy()
    {
        if (State.Energy >= Config.MaxEnergy) return false;
        State.Energy = Math.Min(State.Energy + 1, Config.MaxEnergy);
        return true;
    }
}
```

Config is also available during `[MetaInit]`:

```csharp
[MetaInit]
public Task<int> InitState(int version)
{
    if (version < 1)
    {
        State.Gold = Config.StarterGold;
        return Task.FromResult(1);
    }
    return Task.FromResult(version);
}
```

### Config Versioning (`MetaConfigVersion`)

Config uses a two-part version: `Major.Minor`.

- **Major** = schema version. Changes when config structure changes (requires client update).
- **Minor** = data version. Changes when config values change (same schema).

```csharp
public readonly struct MetaConfigVersion
{
    public int Major { get; }
    public int Minor { get; }
}
```

### Server-Side Config Provider

Implement `IMetaConfigProvider<TConfig>` and register in DI:

```csharp
public class GameConfigProvider : IMetaConfigProvider<GameConfig>
{
    public MetaConfigVersion CurrentVersion => new(1, 2);

    public GameConfig GetConfig(MetaConfigVersion version)
    {
        // Return config for the requested version
        // Could load from files, database, etc.
        return new GameConfig();
    }

    public string? GetDownloadUrl(MetaConfigVersion version)
    {
        // Return URL for client to download this config version
        // Return null if config is bundled with the client
        return $"https://example.com/config/{version.Major}/{version.Minor}";
    }
}

// In server setup:
builder.Services.AddSingleton<IMetaConfigProvider<GameConfig>>(new GameConfigProvider());

// Also register inside Orleans ConfigureServices:
services.ConfigureMeta(svc =>
{
    svc.AddSingleton<IMetaConfigProvider<GameConfig>>(configProvider);
});
```

### Config Version Pinning

Each entity persists its config version in `EntityGrainState.ConfigVersion`. The version is resolved **once** on first activation and reused on subsequent activations:

1. New entity activates → `ConfigVersion` is `(0,0)` (unset)
2. Framework calls `IMetaConfigProvider.CurrentVersion` to get the default version
3. If `IConfigVersionResolver` is registered, it can override the version (for A/B tests)
4. Resolved version is persisted → all future activations use this version
5. Entity keeps using pinned version until explicitly upgraded

### Config Version Resolver (A/B Tests, Gradual Rollouts)

Register `IConfigVersionResolver` in DI to customize which config version an entity uses:

```csharp
public class AbTestConfigResolver : IConfigVersionResolver
{
    public MetaConfigVersion ResolveVersion(
        string stateTypeName, string entityId, MetaConfigVersion currentVersion)
    {
        // Example: 10% of entities get the new config version
        if (entityId.GetHashCode() % 10 == 0)
            return new MetaConfigVersion(currentVersion.Major, currentVersion.Minor + 1);

        return currentVersion;
    }
}

// Register in DI (optional — without it, CurrentVersion is always used):
services.AddSingleton<IConfigVersionResolver>(new AbTestConfigResolver());
```

### Client-Side Config Flow

1. Client subscribes to entity → server includes `ConfigVersion` in the response
2. Client checks local cache (`IMetaConfigCache`) for this version
3. If not cached, client requests download URL via `GetConfigDownloadUrlAsync(stateTypeName, version)`
4. Client downloads config bytes via `IMetaConfigDownloader`, deserializes, and caches
5. If download fails, client falls back to bundled config from shared code

```csharp
// Optional: set up config cache and downloader on client
client.Resolver.ConfigCache = new InMemoryConfigCache();
client.Resolver.ConfigDownloader = new HttpConfigDownloader();
```

Without cache/downloader configured, the client always uses the bundled config factory from shared code.

### Accessing Config from Client Code

After subscribing to an entity, retrieve its resolved config:

```csharp
var config = client.GetEntityConfig<GameConfig>(entityId);
// Returns null if entity not connected or no config configured
```

---

## 4. Execution Modes & Replay

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

### ServerReplace Mode

Method executes **only on the server**. Instead of sending a replay payload or a patch, the server serializes the **entire state** and sends it to the client, which replaces its state wholesale.

```
Client                          Server
  │                                │
  ├─ Send RPC ─────────────────►   │
  │  (waiting...)                  ├─ Execute method
  │                                ├─ Execute triggers (if any)
  │                                ├─ Serialize full state
  │  ◄──── Return result+state ────┤
  ├─ Replace _state entirely       │
  ├─ Fire OnStateRefreshed         │
  ├─ Return result to game code    │
```

**Use case:** When the method fully regenerates state (e.g., generating a game map, resetting game board), sending the full state is more efficient than computing a patch diff that enumerates every new field.

**Declaration:**
```csharp
[MetaMethod(Mode = ExecutionMode.ServerReplace)]
void GenerateMap(int seed);

[MetaMethod(Mode = ExecutionMode.ServerReplace)]
int ReplaceReset(int newValue);
```

**Key differences from ServerPatch:**
- **ServerPatch** sends a diff of changed fields — efficient for small mutations on large state
- **ServerReplace** sends the full state — efficient when state is fully regenerated (patch would be larger than full state)
- Both are server-only: client never executes the method locally
- Both support `IExecutionModeProvider` for runtime switching

**Client-side:** The generated API client checks for `StateBytes` in the response. If present, it deserializes and replaces `_state` entirely, fires `OnStateRefreshed` and `OnStateMutated`. Broadcasts to other subscribers also carry `StateBytes` for the same wholesale replacement.

**Fallback:** If `StateBytes` is not present in the response (e.g., mode was switched at runtime), the client falls back to normal replay.

### Query Calls (No Subscription)

Lightweight read-only RPC to any entity without subscribing. Use for getting brief info about other players, checking entity state in lobbies, etc.

```
Client                          Server
  │                                │
  ├─ QueryCall(entityId) ──────►   │
  │  (waiting...)                  ├─ SessionManager.QueryEntityAsync
  │                                ├─ EntityGrain.HandleQueryAsync
  │                                ├─ DispatchCall (read-only)
  │  ◄──── QueryCallResponse ─────┤
  ├─ Deserialize result            │
```

**No** state sync, broadcasts, replay, persistence, or sequence numbers.

**Declaration:**
```csharp
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileService
{
    [MetaMethod(Query = true)]
    Task<PlayerBriefInfo> GetBriefInfo();

    [MetaMethod(Query = true, OpenAccess = true)]  // bypasses EntityAccessPolicy
    Task<PlayerBriefInfo> GetPublicInfo();

    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    void SetName(string name);
}
```

- `Query = true` — method can be called without subscribing, strictly read-only
- `OpenAccess = true` — skip EntityAccessPolicy check (for public data readable by anyone)
- Query methods **must** return a value (void not allowed)
- Query methods are **not** generated in the regular `ApiClient` — they appear in the separate `QueryApi`

**Client usage:**
```csharp
// Generated: ProfileServiceQueryApi
// Create once
var profileQuery = new ProfileServiceQueryApi(connection, serializer);

// Per-entity proxy
var api = profileQuery.EntityApi("player-123");
var info = await api.GetBriefInfoAsync();
var pub = await api.GetPublicInfoAsync();
```

**Server routing:** `MetaConnectionHandler` → `SessionManager.QueryEntityAsync` → `EntityGrain.HandleQueryAsync` → `MetaProviderBase.HandleQueryAsync` → `DispatchCall`. Same path as regular RPC but without the subscription/broadcast/sequence machinery.

**Access control:** By default, query calls respect the entity's `EntityAccessPolicy`. Use `OpenAccess = true` to bypass this for public read-only data.

---

## 5. Deterministic Random

> **Need non-integer math?** `float`/`double` are not deterministic across platforms and will cause desyncs in Optimistic/CrossOptimistic methods. Use [CoreGame.FixedPoint](https://github.com/CoreGameIO/SharedLibs/tree/main/FixedPoint) (`Fp` type, Q48.16 backed by `long`) — see [Fixed-Point Arithmetic](#fixed-point-arithmetic) below.

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

## 6. Cross-Entity Calls

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

### Read-Only State Access (Context.GetState)

Read another entity's state without calling a method on it. Available as a system method on `MetaContext` — no explicit dependency injection required.

```csharp
// Read a neighbor shard's state (returns null if entity doesn't exist)
var neighborState = await Context.GetState<ShardState>("shard_north");
if (neighborState != null)
{
    // Use neighbor data for map generation, validation, etc.
    var borderTiles = neighborState.SouthBorder;
}
```

**How it works:**
- **Server:** Calls target grain via `[AlwaysInterleave]` method (read-only, no sequence increment, no broadcasts). Records state bytes to replay payload.
- **Client (replay):** Reads pre-recorded bytes from replay payload — deterministic, no network call.
- **`[AlwaysInterleave]`:** Prevents deadlocks when two entities read each other's state simultaneously.
- **Result is nullable:** Returns `null` if the target entity type is unknown. Note: if the entity exists but has never been used, Orleans activates it with default state (not null).

**Use cases:**
- Map generation split into shards where each shard reads neighbors
- Validation against another entity's state before mutation
- Aggregation of data from multiple entities

**Limitations:**
- Not supported in `CrossOptimistic` mode (throws `NotSupportedException`)
- Read-only — you get a deserialized copy, mutations don't affect the target entity
- Each call is a grain-to-grain hop on the server — for high-frequency reads, consider caching

---

## 7. Triggers & Subscribers

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

## 8. Push-Based Change Tracking

Push-based change tracking for client-side UI binding. Zero server overhead — `ChangeTracker` is `null` on server. Changes are recorded as a tree of struct nodes in a pooled list, batched and flushed after method completes.

### Architecture

```
MetaMethod executes on CLIENT
  │  ChangeTracker.Current is set (AsyncLocal)
  │
  │  state.Health = 100
  │    → generated setter writes value + ChangeTracker.Current?.RecordFieldChange(...)
  │
MetaMethod ends → ChangeTracker.FlushAndNotify()
  │  → walk tree, notify type-level subscribers, return pool

SERVER: ChangeTracker.Current == null → zero overhead
```

### Marking fields with [Tracked]

Add `[Tracked]` to private backing fields. The generator produces public properties with tracking setters:

```csharp
[MemoryPackable]
public partial class GameState : ISharedState
{
    [Key(0), MemoryPackOrder(0), MemoryPackInclude, Tracked] private int _gold;
    [Key(1), MemoryPackOrder(1), MemoryPackInclude, Tracked] private int _health = 100;
    [Key(2), MemoryPackOrder(2)] public List<Character> Characters { get; set; } = new();
}
```

Rules:
- Field must be **private** with underscore prefix (e.g. `_gold`)
- Field must have a serialization attribute (`[Key(n)]` or `[MemoryPackOrder(n)]`)
- Add `[MemoryPackInclude]` for MemoryPack (required for private fields)
- **MessagePack**: use `[MessagePackObject(true)]` (AllowPrivate) on the state class — required because `[Tracked]` fields are private
- Generator creates public property: `_gold` → `public int Gold { get; set; }` with tracking setter
- No formatter registration needed — the backing field is serialized directly as `T`

### Generated code

The generator produces (in a single `ChangeTracking.g.cs`):

**1. Unified `TrackingProperty` enum** — one enum for all `[Tracked]` types:
```csharp
public enum TrackingProperty
{
    GameState_Gold = 0,
    GameState_Health = 1,
}
```

**2. Partial class with tracking properties:**
```csharp
public partial class GameState
{
    public const int TrackedTypeId = 0;

    [MemoryPackIgnore, IgnoreMember]
    public int Gold
    {
        get => _gold;
        set
        {
            if (EqualityComparer<int>.Default.Equals(_gold, value)) return;
            var _tracker = ChangeTracker.Current;
            if (_tracker != null)
                _tracker.RecordFieldChange(this, TrackedTypeId,
                    (int)TrackingProperty.GameState_Gold,
                    ChangeValue.From(_gold), ChangeValue.From(value));
            _gold = value;
        }
    }
}
```

**3. Static subscription classes:**
```csharp
public static class TrackedGameState
{
    public static event Action<ChangeTreeArgs>? OnChanged;
    public static void Register();
    public static void Unregister();
}
```

### Subscribing to changes

```csharp
// Register once at startup
TrackedGameState.Register();

// Subscribe to type-level changes
TrackedGameState.OnChanged += args =>
{
    var leaf = args.FindLeaf((int)TrackingProperty.GameState_Health);
    if (leaf != null)
        healthBar.value = leaf.Value.NewValue.IntValue;
};
```

### Change tree structure

Changes are stored as `ChangeNode` structs in a pooled flat list, forming a tree via child indices:

| Field | Description |
|-------|-------------|
| `Field` | `TrackingProperty` enum value |
| `CollectionIndex` | -1 or index in collection |
| `OldValue` / `NewValue` | `ChangeValue` (no boxing for int/long/float/double/bool/string) |
| `ChildStartIndex` / `ChildCount` | Children in the same list (0 = leaf) |

### Core runtime types

| Type | Purpose |
|------|---------|
| `ChangeTracker` | AsyncLocal change buffer. `Activate()` / `FlushAndNotify()` / `Discard()`. |
| `ChangeNode` | Struct node in pooled list (tree via indices). |
| `ChangeValue` | Discriminated union — no boxing for common types. |
| `ChangeTreeArgs` | Passed to subscribers. `HasChange(field)`, `FindLeaf(field)`. |
| `ListPool<T>` | Pool for `List<T>` (rent/return with Clear). |
| `ObjectPool<T>` | Pool for wrapper view classes. |

### OnStateMutated event

Generated API clients fire `OnStateMutated` after any state mutation — broadcast replay, subscriber event, or reconnect. Use it as a general-purpose "state changed" signal when you don't need per-field granularity:

```csharp
var api = await client.GetServiceAsync<GameServiceApiClient>(entityId);
api.OnStateMutated += () => UpdateUI(api.State);
```

This fires in addition to `Tracked{State}.OnChanged` — use whichever granularity fits your UI pattern.

### Service Error Handling

Generated API clients catch exceptions during shared method execution (optimistic, server replay, broadcast replay) at the framework level. When a service method throws:

1. **Logged** via `MetaLog.Error` with service and method name
2. **Error state set** — `HasError` becomes `true`, `ErrorException` holds the exception
3. **Event fired** — `OnServiceError?.Invoke(serviceName, exception)`
4. **Re-thrown** — the original exception propagates to the caller

Once in error state, all subsequent method calls throw `ServiceErrorStateException` until the error is cleared.

```csharp
var api = await client.GetServiceAsync<GameServiceApiClient>(entityId);

// Subscribe to error events
api.OnServiceError += (service, ex) =>
{
    Debug.LogError($"Service error in {service}: {ex.Message}");
    ShowErrorDialog(ex);
};

try
{
    await api.MoveAsync(dx, dy);
}
catch (Exception ex)
{
    // Exception is already logged by the framework — no silent failures
    // api.HasError is now true
}

// Option 1: Clear error manually
api.ClearError();
await api.MoveAsync(0, 0); // works again

// Option 2: Error auto-clears on reconnect (RefreshState)
```

**Key properties and methods:**

| Member | Description |
|--------|-------------|
| `HasError` | `true` if the service is in error state |
| `ErrorException` | The exception that caused the error state, or `null` |
| `OnServiceError` | `Action<string, Exception>` — fires on error with (serviceName, exception) |
| `ClearError()` | Clear error state, allowing further method calls |

**Design rationale:** Game code that catches and swallows exceptions (e.g., `catch { return MoveResult.Blocked; }`) can silently hide bugs. Framework-level error handling ensures exceptions are always logged and the service enters a visible error state, making issues immediately diagnosable.

---

## 9. Argument Transformers

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

## 11. Transport Configuration

Transport is split into **server** and **client** packages. Server packages include both endpoints and connection classes; client-only packages have no server dependencies (no Orleans, no ASP.NET FrameworkReference) and work with Godot, console apps, and other .NET clients.

| Package | Type | Dependencies |
|---------|------|-------------|
| `SharedMeta.Transport.SignalR` | Server + client | Orleans, Server.Core, ASP.NET, MessagePack protocol |
| `SharedMeta.Transport.SignalR.Client` | Client only | Core, SignalR.Client (JSON protocol by default) |
| `SharedMeta.Transport.SignalR.MessagePack` | Protocol extension | Serialization.MessagePack, SignalR.Protocols.MessagePack |
| `SharedMeta.Transport.HttpPolling` | Server + client | Orleans, Server.Core, ASP.NET |
| `SharedMeta.Transport.HttpPolling.Client` | Client only | Core only (uses System.Net.Http.HttpClient) |

**Unity (BestHTTP) — included in UPM package:**

| Transport | Location | Protocol |
|-----------|----------|----------|
| `BestHttpSignalRConnection` | `Runtime/Transport/BestHttpSignalR/` | SignalR via BestHTTP (WebSocket, all platforms incl. WebGL) |
| `BestHttpPollingConnection` | `Runtime/Transport/BestHttp/` | HTTP long-polling via BestHTTP |

BestHTTP transports require the [Best HTTP](https://assetstore.unity.com/packages/tools/network/best-http-2-155981) Unity asset. They are compiled conditionally via assembly definition `defineConstraints` — no compilation errors if BestHTTP is not installed.

### SignalR (WebSocket)

**Server:**
```csharp
builder.Services.AddSignalR().AddMetaMessagePackProtocol();
app.MapHub<MetaHub>("/meta");
```

**Client (JSON protocol — default, no extra packages):**
```csharp
var connection = new SignalRConnection(
    serverUrl: "https://localhost:5001/meta",
    accessToken: jwtToken  // optional
);
```

**Client (MessagePack protocol — add `SharedMeta.Transport.SignalR.MessagePack`):**
```csharp
GeneratedMetaMessagePackConfiguration.Configure();  // auto-generated at startup
var connection = new SignalRConnection(
    serverUrl: "https://localhost:5001/meta",
    accessToken: jwtToken,
    configureBuilder: builder => builder.AddMetaMessagePackProtocol()
);
```

The server supports both JSON and MessagePack protocols simultaneously (SignalR auto-negotiation). JSON clients work with MessagePack servers without configuration.

Features:
- Real-time bidirectional (WebSocket)
- Auto-reconnect with exponential backoff: [0s, 2s, 5s, 10s, 30s]
- JSON protocol by default (zero extra dependencies), optional MessagePack (compact binary, raw byte[])

### HTTP Long-Polling

**Server:**
```csharp
app.MapMetaHttpEndpoints("/meta-http");
```

**Client (.NET — `SharedMeta.Transport.HttpPolling.Client`):**
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

**Client (Unity — `UnityHttpConnection`):**
```csharp
var connection = new UnityHttpConnection(new UnityHttpConnectionOptions
{
    ServerUrl = "https://localhost:5001/meta-http",
    AccessToken = jwtToken
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

### BestHTTP SignalR (Unity — all platforms incl. WebGL)

Requires the BestHTTP Unity asset. Uses `BestHTTP.SignalRCore.HubConnection` internally; wraps IFuture-based API with `TaskCompletionSource` for async/await compatibility.

**JSON protocol (default):**
```csharp
var connection = new BestHttpSignalRConnection(
    serverUrl: "https://localhost:5001/meta",
    accessToken: jwtToken  // optional
);
```

**MessagePack protocol:**

Requires scripting define `BESTHTTP_SIGNALR_CORE_ENABLE_MESSAGEPACK_CSHARP` and MessagePack NuGet. Set `MessagePackSerializer.DefaultOptions` before connecting:
```csharp
MessagePackSerializer.DefaultOptions = MetaMessagePackOptions.Instance;

var connection = new BestHttpSignalRConnection(new BestHttpSignalRConnectionOptions
{
    ServerUrl = "https://localhost:5001/meta",
    AccessToken = jwtToken,
    Protocol = new MessagePackCSharpProtocol()
});
```

**LitJson type registrations:** The static constructor of `BestHttpSignalRConnection` registers custom importers/exporters for `Guid` (string) and `byte[]` (base64) in LitJson, ensuring compatibility with server-side `System.Text.Json`.

### BestHTTP HTTP Polling (Unity)

```csharp
var connection = new BestHttpPollingConnection(new BestHttpPollingConnectionOptions
{
    ServerUrl = "https://localhost:5001/meta-http",
    AccessToken = jwtToken  // optional
});
```

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

## 10. Serialization

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

**MemoryPack (use `VersionTolerant` for persisted state classes):**
```csharp
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class MyState : ISharedState
{
    [MemoryPackOrder(0)] public string Name { get; set; }
    [MemoryPackOrder(1)] public int Value { get; set; }
}
```

**MessagePack:**
```csharp
[MessagePackObject]
public partial class MyState : ISharedState
{
    [Key(0)] public string Name { get; set; }
    [Key(1)] public int Value { get; set; }
}
```

**Both (cross-serializer compatibility):**
```csharp
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public partial class MyState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public string Name { get; set; }
    [Key(1), MemoryPackOrder(1)] public int Value { get; set; }
}
```

States are persisted and transmitted as bytes via the chosen transport serializer. Orleans `[GenerateSerializer]` / `[Id(n)]` are **not needed** on game state and DTO classes — those are only used internally by the framework.

### Version Tolerance Rules

- **MemoryPack**: Use `[MemoryPackable(GenerateType.VersionTolerant)]` on all persisted types (state classes, grain state wrappers). This stores field orders explicitly in the binary format, allowing safe addition/removal of fields. Without `VersionTolerant`, MemoryPack serializes as a fixed-length array — adding fields breaks deserialization of old data.
- `[MemoryPackOrder(n)]` — required with `VersionTolerant`. Defines the field identity in the binary format.
- `[Key(n)]` — MessagePack field ordering. MessagePack with integer keys is inherently version-tolerant: unknown keys are skipped (forward compatible), missing keys get defaults (backward compatible).
- For non-persisted DTOs (transport-only), plain `[MemoryPackable]` without `VersionTolerant` is acceptable since both client and server are always updated together.

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

## 12. Session Management

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

## 13. Authentication

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
- Server extracts PlayerId from JWT claims (`sub` or `ClaimTypes.NameIdentifier`), overrides request PlayerId

### Enforcing Authentication

By default, unauthenticated clients can connect with any PlayerId. To require authentication:

**Option 1: `MetaTransportOptions` (recommended — enforced inside the framework for both transports):**
```csharp
builder.Services.AddSingleton(new MetaTransportOptions { RequireAuthentication = true });
```

**Option 2: ASP.NET `[Authorize]` attribute (additional layer — rejects unauthenticated at middleware level):**
```csharp
// On a custom hub subclass:
[Authorize]
public class MyHub : MetaHub { }

// Or at endpoint mapping:
app.MapHub<MetaHub>("/meta").RequireAuthorization();
app.MapMetaHttpPolling("/meta").RequireAuthorization();
```

Both options can be combined for defense-in-depth. `MetaTransportOptions.RequireAuthentication` is the safety net inside the framework — it works regardless of whether middleware is configured correctly.

### Client-Side Authentication

Use `MetaAuth` — a cross-platform helper that works on both Unity (`UnityWebRequest`) and .NET (`HttpClient`):

```csharp
// Simple login (always makes a network call)
var login = await MetaAuth.LoginAsync($"{serverUrl}/meta/auth", deviceId);
var connection = new SignalRConnection($"{serverUrl}/meta", accessToken: login.Token);
var client = new MetaClient(connection, serializer, new MetaClientOptions { PlayerId = login.PlayerId });
```

**Token caching** — reuse tokens across sessions with `ITokenStorage`:

```csharp
// Unity: use PlayerPrefsTokenStorage
ITokenStorage storage = new PlayerPrefsTokenStorage();

// Login or reuse cached token (skips network call if token is still valid)
var login = await MetaAuth.EnsureAuthenticatedAsync($"{serverUrl}/meta/auth", deviceId, storage);

// Logout
MetaAuth.ClearToken(storage);
```

`CachedToken.IsValid` checks expiry with a 5-minute safety margin.

**Custom storage**: Implement `ITokenStorage` for platform-specific storage (e.g., `SecureStorage`, file-based, `PlayerPrefs`). The interface has three methods: `Load()`, `Save(CachedToken)`, `Clear()`.

> **Migration from `MetaClient.LoginAsync`**: `MetaClient.LoginAsync` is still available on .NET but `MetaAuth.LoginAsync` is preferred — it works on all platforms and supports cancellation tokens.

**Unity architecture**: Auth code is split into two assemblies due to `noEngineReferences`:

| Assembly | `noEngineReferences` | Contents |
|---|---|---|
| `SharedMeta.Runtime` | `true` | `MetaAuth`, `ITokenStorage`, `CachedToken`, `MetaLoginResult` |
| `SharedMeta.Auth.Client` | `false` | `UnityMetaAuth`, `PlayerPrefsTokenStorage` |

`UnityMetaAuth.Register()` is called automatically via `[RuntimeInitializeOnLoadMethod]` — it sets `MetaAuth.LoginFunc` to the Unity `UnityWebRequest` implementation. No manual registration needed.

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

## 14. Persistence Configuration

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

## 15. Orleans Backend

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

## 16. Server Setup

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
        sp.GetRequiredService<IEntityGrainResolver>(),
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

## 17. Client Setup

### NuGet Packages for .NET Clients (Godot, Console, etc.)

```xml
<ItemGroup>
  <PackageReference Include="CoreGame.SharedMeta.Core" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Client" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Serialization.MemoryPack" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Transport.SignalR.Client" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Generator" Version="0.2.0"
                    PrivateAssets="all" OutputItemType="analyzer" />
  <!-- Optional: MessagePack protocol for SignalR (better performance) -->
  <!-- <PackageReference Include="CoreGame.SharedMeta.Transport.SignalR.MessagePack" Version="0.2.0" /> -->
</ItemGroup>
```

### Unity Client (BestHTTP)

For Unity projects, transports are included in the UPM package (`com.coregame.sharedmeta`). Use the **SharedMeta Project Wizard** (Window > SharedMeta > Project Wizard) to generate client code with the correct transport configuration.

Available Unity transports:
- **BestHTTP SignalR** — WebSocket-based, works on all platforms including WebGL. Requires BestHTTP asset.
- **BestHTTP HTTP Polling** — HTTP long-polling via BestHTTP. Universal compatibility.
- **SignalR (Microsoft)** — Standard .NET SignalR client. Requires `HAS_SIGNALR` scripting define and SignalR DLLs.
- **HTTP (UnityWebRequest)** — Uses `UnityHttpConnection` with Newtonsoft.Json. Requires `com.unity.nuget.newtonsoft-json`.

### Basic Client (.NET)

```csharp
// Transport (JSON protocol by default)
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

## 18. Matchmaking (Lobby)

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

## 19. Desync Diagnostics & Common Pitfalls

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

**Recommended: [CoreGame.FixedPoint](https://github.com/CoreGameIO/SharedLibs/tree/main/FixedPoint)**

`Fp` is a 64-bit fixed-point type (Q48.16 format) backed by `long`. It provides operators (`+`, `-`, `*`, `/`, `%`, comparisons), implicit conversion from `int`, and built-in serialization support for both MemoryPack and MessagePack — no raw-value workarounds needed.

| Platform | Install |
|----------|---------|
| .NET (server / Godot) | [`dotnet add package CoreGame.FixedPoint`](https://www.nuget.org/packages/CoreGame.FixedPoint) |
| Unity | UPM → Add by git URL: `https://github.com/CoreGameIO/SharedLibs.git#upm/fixedpoint` |

```csharp
using CoreGame.FixedPoint;

Fp speed = Fp.Half;                        // 0.5
Fp distance = 10 * speed;                  // 5.0, deterministic
bool hit = distance < Fp.FromInt(6);       // true, deterministic

// Conversions
Fp value = Fp.FromInt(42);
int rounded = value.RoundToInt();
Fp precise = Fp.FromDecimal(3.14m);        // exact at compile-time

// Math (all deterministic, integer-only under the hood)
Fp root  = FpMath.Sqrt(x * x + y * y);
Fp blend = FpMath.LerpClamped(a, b, t);
Fp power = FpMath.PowInt(base, 3);
Fp log   = FpMath.Log2(value);
```

**Integration with SharedMeta state:**
```csharp
// Fp is directly serializable — use it in state as-is
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public partial class UnitState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public Fp PositionX { get; set; }
    [Key(1), MemoryPackOrder(1)] public Fp PositionY { get; set; }
    [Key(2), MemoryPackOrder(2)] public Fp Speed { get; set; }
}

// In service — arithmetic just works
public void Move(Fp dx, Fp dy)
{
    State.PositionX += dx * State.Speed;
    State.PositionY += dy * State.Speed;
}
```

**Alternatives:**
- **[FixedPointSharp](https://github.com/sschoener/FixedPointSharp)** — `fp` type (16.16 format, 32-bit), smaller range, includes trig functions
- **[FixedMath.Net](https://github.com/asik/FixedMath.Net)** — `Fix64` type (32.32 format), wider range
- **Manual scaling** — use `long` with a fixed scale factor (e.g., `× 1000`) for simple cases

**Rule of thumb:** If a value participates in Optimistic or CrossOptimistic logic and requires non-integer math, use fixed-point. Server-only logic (`ExecutionMode.Server`) can use `float` safely since only the server computes it.

### Common Pitfalls

Beyond desyncs, these are frequent mistakes when working with SharedMeta services:

**1. Static mutable state in service implementations**

Orleans grains are long-lived objects. A `static` field in your service class persists across all calls on that silo node, leaks between entities, and behaves differently across clustered nodes:

```csharp
// BAD — shared across all entities on this silo, invisible to other silos
[MetaServiceImpl(typeof(IGameService), typeof(GameState))]
public partial class GameServiceImpl : IGameService
{
    static int _globalCounter = 0; // shared between all players on this node!

    public void DoSomething()
    {
        _globalCounter++; // different value on different Orleans nodes
    }
}
```

Use `State` properties for per-entity data. For truly global data, use an Orleans grain or `[MetaConfig]`.

**2. Non-deterministic collection iteration**

`Dictionary<TKey, TValue>` and `HashSet<T>` do not guarantee iteration order. If iteration order affects the result, the client and server may diverge:

```csharp
// BAD — iteration order may differ between client and server
var firstItem = state.Inventory.First(); // Dictionary<string, int>

// GOOD — sort or use a deterministic collection
var firstItem = state.Inventory.OrderBy(kv => kv.Key).First();
```

Use `List<T>`, `PatchableList<T>`, or sort before iterating.

**3. Captured closures and lambdas in shared logic**

Avoid LINQ closures that capture local variables in Optimistic methods — compiler-generated closure classes may have different memory layouts, and any `float` arithmetic inside them is non-deterministic:

```csharp
// Risky in Optimistic mode — closure captures 'threshold'
float threshold = CalculateThreshold(); // float!
var items = state.Items.Where(i => i.Value > threshold).ToList();
```

**4. DateTime.Now / DateTime.UtcNow in shared logic**

Client and server clocks differ. Use `Context.ServerTimeTicks` instead:

```csharp
// BAD — different on client and server
var elapsed = DateTime.UtcNow - state.LastActionTime;

// GOOD — synchronized server time
var elapsed = Context.ServerTimeTicks - state.LastActionTicks;
```

**5. Forgetting `partial` on state and implementation classes**

Both MemoryPack and the SharedMeta source generator require `partial`. If you forget it, you'll get cryptic compilation errors about missing generated members:

```csharp
// BAD — won't compile (MemoryPack and SharedMeta generators need partial)
[MemoryPackable] public class GameState : ISharedState { }

// GOOD
[MemoryPackable] public partial class GameState : ISharedState { }
```

---

## 20. Code Generation Reference

The source generator (`SharedMeta.Generator`) scans assemblies for attributes and generates:

| Input | Output | Description |
|-------|--------|-------------|
| `[MetaService]` interface | `*Dispatcher.g.cs` | Server-side method routing (switch-based) |
| `[MetaService]` interface | `*ApiClient.g.cs` | Typed async client with execution mode handling |
| `[MetaService]` interface | `*ServiceExtensions.g.cs` | DI registration helpers |
| `[MetaServiceImpl]` class | `*.Context.g.cs` | Context injection (State, CallerId, dependencies) |
| Assembly with `[MetaService]` | `ServerMetaConfiguration.g.cs` | MetaProvider generation, service wiring |
| `[Transformer]` class | `TransformerRegistrations.g.cs` | Auto-registration of all transformers |
| `[Tracked]` field | `ChangeTracking.g.cs` | Push-based change tracking properties for UI binding (client-only) |

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

## 21. Attribute Reference

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[MetaService]` | Interface | Marks shared service for code generation |
| `[MetaMethod]` | Method | Configures execution mode, alias, versioning |
| `[MetaServiceImpl]` | Class | Marks service implementation for context injection |
| `[MetaInit]` | Method | State initialization/migration on grain activation |
| `[MetaConfig]` | Class | Marks a class as static game configuration |
| `[SharedState]` | Class | Marks shared state entity |
| `[Tracked]` | Field | Push-based change tracking property for UI binding (client-only) |
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
| `Query` | bool | false | Callable without subscribing (read-only, no broadcast/replay) |
| `OpenAccess` | bool | false | Bypass EntityAccessPolicy for query methods |

### MetaService Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StateType` | Type | required | State class type |
| `ConfigType` | Type | null | Explicit config type for this service |
| `DefaultConfig` | bool | false | Use the config class marked with `[MetaConfig(Default = true)]` |
| `AccessPolicy` | EntityAccessPolicy | Open | Subscribe access control |
| `SubscriberInterfaces` | Type[] | empty | Framework event subscriptions |

---

## 22. Testing

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

## 23. Capability Overview

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

## 24. Tutorial: Building Your First Service

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

## 25. Example: Expedition (Cross-Entity Economy)

A complete example demonstrating cross-entity calls, energy/money economy, procedural map generation, push-based change tracking, and static game configuration.

**Source code:** `examples/Expedition/`

### Overview

Expedition is a maze exploration game with fog of war. The player navigates a procedurally generated map, collecting treasures and spending energy. Two entities work together:

| Entity | State | Access Policy | ID Pattern |
|--------|-------|---------------|------------|
| **Profile** | `ProfileState` — energy, money, expedition counter | `UserOwned` (entityId == playerId) | `playerId` |
| **Expedition** | `ExpeditionState` — maze cells, fog, player position | `Authorized` (owner-checked) | `expedition-{playerId}-{counter}` |

Cross-entity calls connect them: Expedition spends energy and awards money on Profile; Profile creates and checks Expedition status.

```
┌──────────────────────┐         ┌──────────────────────────┐
│ ProfileState         │         │ ExpeditionState           │
│  Energy, Money       │◄────────│  Map, PlayerXY, Fog       │
│  ExpeditionCounter   │ Spend   │  TreasuresCollected       │
│                      │ Energy  │                           │
│                      │ Add     │                           │
│ ResumeOrStart ──────►│ Money   │  Move (CrossOptimistic)   │
│ Expedition           │         │  RemoveObstacle (CrossOpt)│
└──────────────────────┘         └──────────────────────────┘
```

### State Classes

```csharp
// Player profile — energy regenerates over time, money earned from treasures
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject(AllowPrivate = true)]
[SharedState]
public partial class ProfileState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public string PlayerId { get; set; } = "";
    [Key(1), MemoryPackOrder(1), MemoryPackInclude, Tracked] private int _energy = 50;
    [Key(2), MemoryPackOrder(2)] public int MaxEnergy { get; set; } = 50;
    [Key(3), MemoryPackOrder(3), MemoryPackInclude, Tracked] private int _money = 100;
    [Key(4), MemoryPackOrder(4)] public long LastEnergyUpdateTicks { get; set; }
    [Key(5), MemoryPackOrder(5)] public int EnergyRegenSeconds { get; set; } = 10;
    [Key(6), MemoryPackOrder(6)] public string? CurrentExpeditionEntityId { get; set; }
    [Key(7), MemoryPackOrder(7)] public int ExpeditionCounter { get; set; }
}
```

Key points:
- `_energy` and `_money` are `[Tracked]` — generator creates `Energy`/`Money` public properties with change tracking setters
- `LastEnergyUpdateTicks` uses `Context.ServerTimeTicks` for deterministic regen across client and server

```csharp
// Expedition map — cells, fog of war, player position
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
[SharedState]
public partial class ExpeditionState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public int Width { get; set; }
    [Key(1), MemoryPackOrder(1)] public int Height { get; set; }
    [Key(2), MemoryPackOrder(2)] public List<byte> Cells { get; set; } = new();      // CellType enum
    [Key(3), MemoryPackOrder(3)] public List<bool> Revealed { get; set; } = new();    // fog of war
    [Key(4), MemoryPackOrder(4)] public int PlayerX { get; set; }
    [Key(5), MemoryPackOrder(5)] public int PlayerY { get; set; }
    [Key(6), MemoryPackOrder(6)] public bool IsGenerated { get; set; }
    [Key(7), MemoryPackOrder(7)] public string? ProfileEntityId { get; set; }
    [Key(8), MemoryPackOrder(8)] public int TreasuresCollected { get; set; }
    [Key(9), MemoryPackOrder(9)] public int TotalTreasures { get; set; }
    [Key(10), MemoryPackOrder(10)] public bool IsComplete { get; set; }
    [Key(11), MemoryPackOrder(11)] public string? OwnerPlayerId { get; set; }
}
```

### Static Configuration

```csharp
[MetaConfig(Default = true)]
[MemoryPackable, MessagePackObject]
public partial class ExpeditionConfig
{
    [Key(0), MemoryPackOrder(0)] public int MapWidth { get; set; } = 15;
    [Key(1), MemoryPackOrder(1)] public int MapHeight { get; set; } = 10;
    [Key(2), MemoryPackOrder(2)] public int WallPercent { get; set; } = 20;
    [Key(3), MemoryPackOrder(3)] public int ObstaclePercent { get; set; } = 10;
    [Key(4), MemoryPackOrder(4)] public int TreasurePercent { get; set; } = 8;
    [Key(5), MemoryPackOrder(5)] public int MoveCost { get; set; } = 1;
    [Key(6), MemoryPackOrder(6)] public int ObstacleCost { get; set; } = 5;
    [Key(7), MemoryPackOrder(7)] public int TreasureReward { get; set; } = 25;
}
```

Balance parameters are served by `IMetaConfigProvider<ExpeditionConfig>` on the server, downloaded and cached by clients.

### Service Interfaces

```csharp
[MetaService(StateType = typeof(ExpeditionState), AccessPolicy = EntityAccessPolicy.Authorized, DefaultConfig = true)]
public interface IExpeditionService : IMetaService
{
    // Called cross-entity from ProfileService to set ownership
    [MetaMethod(Alias = "Init", Mode = ExecutionMode.Server, GenerateClientApi = false)]
    void Init(string ownerPlayerId);

    // Move player — reveals fog, collects treasures, spends energy via cross-entity call
    [MetaMethod(Alias = "Move", Mode = ExecutionMode.CrossOptimistic)]
    Task<MoveResult> Move(int dx, int dy);

    // Remove obstacle at adjacent cell — costs more energy
    [MetaMethod(Alias = "RemoveObstacle", Mode = ExecutionMode.CrossOptimistic)]
    Task<bool> RemoveObstacle(int dx, int dy);

    // Check if expedition is still active (cross-entity query)
    [MetaMethod(Alias = "IsActive", Mode = ExecutionMode.Server, GenerateClientApi = false)]
    bool IsActive();
}
```

```csharp
[MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned)]
public interface IExpeditionProfileService : IMetaService
{
    // Recalculate energy based on elapsed server time
    [MetaMethod(Alias = "UpdateEnergy")]
    int UpdateEnergy();

    // Buy energy with money (bypasses MaxEnergy cap)
    [MetaMethod(Alias = "BuyEnergy")]
    bool BuyEnergy(int energyAmount, int moneyCost);

    // Cross-entity only: spend energy (called by ExpeditionService.Move)
    [MetaMethod(Alias = "SpendEnergy", Mode = ExecutionMode.Server, GenerateClientApi = false)]
    bool SpendEnergy(int amount);

    // Cross-entity only: award money (called by ExpeditionService on treasure)
    [MetaMethod(Alias = "AddMoney", Mode = ExecutionMode.Server, GenerateClientApi = false)]
    void AddMoney(int amount);

    // Resume current expedition or start a new one
    [MetaMethod(Alias = "ResumeOrStartExpedition", Mode = ExecutionMode.Server)]
    Task<ResumeExpeditionResult> ResumeOrStartExpedition();
}
```

Key patterns:
- `GenerateClientApi = false` — methods only called cross-entity (server-to-server), no client API generated
- `CrossOptimistic` — client executes Move locally for instant response, server validates
- `ExecutionMode.Server` for `ResumeOrStartExpedition` — makes cross-entity calls that can't run on client

### Cross-Entity Call Pattern

The `[MetaServiceImpl]` declares dependencies to get cross-entity callers:

```csharp
// ExpeditionService depends on IExpeditionProfileService (for energy/money)
[MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), typeof(IExpeditionProfileService))]
public partial class ExpeditionService : IExpeditionService
{
    // Generator injects: GetIExpeditionProfileService(entityId) method

    public async Task<MoveResult> Move(int dx, int dy)
    {
        // ... validate move ...

        // Cross-entity call: spend energy on the profile entity
        if (!state.Revealed[idx])
        {
            var profileCaller = GetIExpeditionProfileService(state.ProfileEntityId!);
            bool spent = await profileCaller.SpendEnergyAsync(Config.MoveCost);
            if (!spent) return MoveResult.NoEnergy;
        }

        // ... move player, reveal fog ...

        // Cross-entity call: award money for treasure
        if (cellType == CellType.Treasure)
        {
            var profileCaller = GetIExpeditionProfileService(state.ProfileEntityId!);
            await profileCaller.AddMoneyAsync(Config.TreasureReward);
        }

        return MoveResult.Ok;
    }
}
```

```csharp
// ProfileService depends on IExpeditionService (for status checks and init)
[MetaServiceImpl(typeof(IExpeditionProfileService), typeof(ProfileState), typeof(IExpeditionService))]
public partial class ExpeditionProfileService : IExpeditionProfileService
{
    // Generator injects: GetIExpeditionService(entityId) method

    public async Task<ResumeExpeditionResult> ResumeOrStartExpedition()
    {
        // Check if current expedition is still active
        if (!string.IsNullOrEmpty(state.CurrentExpeditionEntityId))
        {
            var expService = GetIExpeditionService(state.CurrentExpeditionEntityId);
            bool active = await expService.IsActiveAsync();
            if (active)
                return new ResumeExpeditionResult { EntityId = state.CurrentExpeditionEntityId };
        }

        // Create new expedition
        state.ExpeditionCounter++;
        var entityId = $"expedition-{state.PlayerId}-{state.ExpeditionCounter}";
        state.CurrentExpeditionEntityId = entityId;

        // Initialize expedition (cross-entity call to new grain)
        var newExpService = GetIExpeditionService(entityId);
        await newExpService.InitAsync(state.PlayerId);

        return new ResumeExpeditionResult { EntityId = entityId, IsNew = true };
    }
}
```

### Map Generation with [MetaInit]

```csharp
[MetaInit]
public Task<int> GenerateMap(int version)
{
    if (version < 1)
    {
        var width = Config.MapWidth;    // from ExpeditionConfig
        var height = Config.MapHeight;

        // ... initialize cells list ...

        // Deterministic random — identical results on client and server
        for (int i = 0; i < totalCells; i++)
        {
            if (Context.Random!.Next(100) < Config.WallPercent)
                state.Cells[i] = (byte)CellType.Wall;
        }

        // ... place obstacles, treasures, reveal starting area ...

        state.IsGenerated = true;
        return Task.FromResult(1);
    }
    return Task.FromResult(version);
}
```

### Energy Regeneration with ServerTimeTicks

```csharp
public int UpdateEnergy()
{
    if (state.Energy >= state.MaxEnergy)
    {
        state.LastEnergyUpdateTicks = Context.ServerTimeTicks;
        return state.Energy;
    }

    var elapsed = Context.ServerTimeTicks - state.LastEnergyUpdateTicks;
    var secondsElapsed = elapsed / TimeSpan.TicksPerSecond;
    var regenAmount = (int)(secondsElapsed / state.EnergyRegenSeconds);

    if (regenAmount > 0)
    {
        state.Energy = Math.Min(state.Energy + regenAmount, state.MaxEnergy);
        // Advance by consumed ticks only (preserves fractional regen progress)
        state.LastEnergyUpdateTicks += regenAmount * state.EnergyRegenSeconds * TimeSpan.TicksPerSecond;
    }
    return state.Energy;
}
```

`Context.ServerTimeTicks` is synchronized — both client and server compute the same regen result.

### Unity Client

A minimal Unity MonoBehaviour that connects to the Expedition server and drives gameplay:

```csharp
using UnityEngine;
using UnityEngine.UI;
using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Reactive;
using SharedMeta.Serialization.MessagePack;
using Expedition.Shared;
using Expedition.Shared.Client;

public class ExpeditionGameClient : MonoBehaviour
{
    [SerializeField] string serverUrl = "http://localhost:5100";
    [SerializeField] Text energyText;
    [SerializeField] Text moneyText;
    [SerializeField] Text statusText;

    MetaClient client;
    ExpeditionProfileServiceApiClient profileApi;
    ExpeditionServiceApiClient expApi;
    string expeditionEntityId;

    async void Start()
    {
        // Configure MessagePack resolvers (generated)
        GeneratedMetaMessagePackConfiguration.Configure();

        // Authenticate
        var deviceId = SystemInfo.deviceUniqueIdentifier;
        var login = await MetaClient.LoginAsync($"{serverUrl}/meta/auth", deviceId);

        // Connect
        client = new MetaClient(
            new BestHttpSignalRConnection($"{serverUrl}/meta", login.Token),
            new MessagePackMetaSerializer(),
            new MetaClientOptions { PlayerId = login.PlayerId }
        );
        var resolver = (MetaServiceResolver)client.Resolver;
        resolver.RegisterAllServices();
        await client.ConnectAsync();

        // Subscribe to profile
        profileApi = await client.GetExpeditionProfileServiceAsync();
        await profileApi.UpdateEnergyAsync();

        // Register reactive change tracking
        TrackedProfileState.Register();
        TrackedProfileState.OnChanged += OnProfileChanged;

        // Start or resume expedition
        var result = await profileApi.ResumeOrStartExpeditionAsync();
        expeditionEntityId = result.EntityId;
        expApi = await client.GetServiceAsync<ExpeditionServiceApiClient>(expeditionEntityId);

        statusText.text = result.IsNew ? "New expedition!" : "Expedition resumed";
        UpdateUI();
    }

    void Update()
    {
        if (client == null) return;

        // Process server broadcasts on the main thread
        client.Dispatcher.ProcessPendingBroadcasts();
    }

    // Push-based UI updates — fires when Energy or Money changes
    void OnProfileChanged(ChangeArgs args)
    {
        if (args.HasChange((int)TrackingProperty.ProfileState_Energy))
        {
            var leaf = args.FindLeaf((int)TrackingProperty.ProfileState_Energy);
            if (leaf != null)
                energyText.text = $"Energy: {leaf.Value.NewValue.IntValue}";
        }
        if (args.HasChange((int)TrackingProperty.ProfileState_Money))
        {
            var leaf = args.FindLeaf((int)TrackingProperty.ProfileState_Money);
            if (leaf != null)
                moneyText.text = $"Money: {leaf.Value.NewValue.IntValue}";
        }
    }

    // Called from UI buttons or input
    public async void MovePlayer(int dx, int dy)
    {
        var result = await expApi.MoveAsync(dx, dy);
        statusText.text = result switch
        {
            MoveResult.Ok => "",
            MoveResult.Treasure => $"Treasure! +{25} money",
            MoveResult.NoEnergy => "Not enough energy!",
            MoveResult.Blocked => "Blocked!",
            MoveResult.Complete => "All treasures found!",
            _ => ""
        };
        UpdateUI();
    }

    public async void RemoveObstacle(int dx, int dy)
    {
        bool removed = await expApi.RemoveObstacleAsync(dx, dy);
        statusText.text = removed ? "Obstacle removed!" : "Cannot remove";
        UpdateUI();
    }

    public async void BuyEnergy()
    {
        bool bought = await profileApi.BuyEnergyAsync(10, 50);
        statusText.text = bought ? "Bought 10 energy!" : "Not enough money";
    }

    public async void StartNewExpedition()
    {
        var result = await profileApi.ResumeOrStartExpeditionAsync();
        expeditionEntityId = result.EntityId;
        expApi = await client.GetServiceAsync<ExpeditionServiceApiClient>(expeditionEntityId);
        statusText.text = "New expedition started!";
        UpdateUI();
    }

    void UpdateUI()
    {
        var profile = client.GetProfileState();
        energyText.text = $"Energy: {profile.Energy}/{profile.MaxEnergy}";
        moneyText.text = $"Money: {profile.Money}";

        // Read expedition state for map rendering
        var exp = client.GetState<ExpeditionState>(expeditionEntityId);
        // ... render map tiles based on exp.Cells, exp.Revealed, exp.PlayerX/Y ...
    }

    void OnDestroy()
    {
        TrackedProfileState.OnChanged -= OnProfileChanged;
        TrackedProfileState.Unregister();
        client?.DisposeAsync();
    }
}
```

Key Unity patterns:
- **`ProcessPendingBroadcasts()`** in `Update()` — drains server broadcasts on the main thread, ensuring state mutations and UI updates don't race
- **`TrackedProfileState.OnChanged`** — push-based UI binding, no polling needed
- **`BestHttpSignalRConnection`** — Unity transport adapter (works on WebGL, mobile, desktop)
- **`async void`** for fire-and-forget button handlers — exceptions logged by Unity

### Running the Example

**Console client** (included in the repo):
```bash
# Terminal 1: start server
dotnet run --project examples/Expedition/Expedition.Server

# Terminal 2: start client
dotnet run --project examples/Expedition/Expedition.Client
```

**Unity client** (using the MonoBehaviour above):
1. Install SharedMeta UPM package
2. Add `Expedition.Shared` as linked project in your `.asmdef` or copy the shared code
3. Create a scene with the `ExpeditionGameClient` MonoBehaviour
4. Start the server (`dotnet run --project examples/Expedition/Expedition.Server`)
5. Press Play in Unity

---

## 26. Architecture Decisions

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
