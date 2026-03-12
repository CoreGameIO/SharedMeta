# SharedMeta Framework — AI Assistant Instructions

This file provides context for AI code assistants (Claude, Copilot, Cursor, etc.) working on projects that use the SharedMeta framework. For the complete reference, see `docs/GUIDE.md`.

## What is SharedMeta

SharedMeta is a framework for shared game meta-logic between Client and Server. Game logic is written once in C# and runs on both the server (Orleans grains) and the client (Unity/.NET) with optimistic execution, automatic replay, and desync detection.

## Architecture

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

**RPC call flow:**
1. Client calls `api.PlayCardAsync(card)` (generated API client)
2. Args serialized → `IConnection.RpcCallAsync()`
3. Server `SessionManagerGrain` routes to `EntityGrain`
4. `EntityGrain` increments sequence → `MetaProvider.HandleCallAsync()`
5. `MetaProvider` sets up context (Random, ServerRandom, Replay recording)
6. Generated dispatcher routes to `CardGameService.PlayCard(card)`
7. Result + replay payload returned up the chain
8. Client receives response, replays locally, returns result to game code

---

## Critical Rules

### 1. Serialization Attributes (REQUIRED)

All state and DTO classes need a **transport serializer** attribute (choose one based on project setup):

**With MemoryPack (use `VersionTolerant` for persisted state classes):**
```csharp
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class MyState : ISharedState
{
    [MemoryPackOrder(0)] public string Name { get; set; }
    [MemoryPackOrder(1)] public int Value { get; set; }
}
```

**With MessagePack:**
```csharp
[MessagePackObject]
public partial class MyState : ISharedState
{
    [Key(0)] public string Name { get; set; }
    [Key(1)] public int Value { get; set; }
}
```

**With both (for cross-serializer compatibility):**
```csharp
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public partial class MyState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public string Name { get; set; }
    [Key(1), MemoryPackOrder(1)] public int Value { get; set; }
}
```

**Attribute roles:**
- `[MemoryPackable(GenerateType.VersionTolerant)]` — use on all persisted types. Stores field orders explicitly, allowing safe field addition/removal. Without `VersionTolerant`, adding fields breaks deserialization of old data.
- `[MemoryPackOrder(n)]` — MemoryPack field ordering. Required with `VersionTolerant`.
- `[Key(n)]` — MessagePack field ordering. MessagePack with integer keys is inherently version-tolerant.
- States are persisted and transmitted as bytes via the chosen transport serializer. Orleans `[GenerateSerializer]` / `[Id(n)]` are **not needed** on game state and DTO classes — those are only used internally by the framework.
- For non-persisted DTOs (transport-only), plain `[MemoryPackable]` without `VersionTolerant` is acceptable.

### 2. Classes Must Be Partial

All state classes, service implementations, and DTO types must be `partial` — the source generator extends them:

```csharp
public partial class GameState : ISharedState { ... }       // ✓
public partial class GameServiceImpl : IGameService { ... }  // ✓
public class GameState : ISharedState { ... }                // ✗ WRONG
```

### 3. Never Use System.Random

`System.Random` causes desyncs because client and server produce different sequences:

```csharp
// ✗ WRONG — causes desync
var rand = new Random();
int roll = rand.Next(6);

// ✓ CORRECT — deterministic across client and server
int roll = Context.Random!.Next(6);        // Optimistic random
int loot = Context.ServerRandom!.Next(100); // Server-only random
```

- `Context.Random` — identical algorithm (xoshiro128**) and seed on both sides
- `Context.ServerRandom` — generated on server, replayed on client via payload

### 4. Never Use DateTime.Now

Use `Context.ServerTimeTicks` (synchronized UTC ticks) instead:

```csharp
// ✗ WRONG — clock difference causes desync
var now = DateTime.UtcNow;

// ✓ CORRECT — synchronized with server
long now = Context.ServerTimeTicks;
```

### 5. Floating Point Is Not Deterministic

`float` and `double` arithmetic is **not portable** across platforms. Different results on x86 SSE vs ARM NEON, .NET RyuJIT vs Mono, different optimization levels.

**Safe in shared logic:** `int`, `long`, `decimal`, `Context.Random!.Next(int max)`

**NOT safe in shared logic (Optimistic/CrossOptimistic):**
- `float` / `double` arithmetic (`a * b + c`)
- `Math.Sin`, `Math.Sqrt`, `MathF.*`
- `Context.Random!.NextFloat()` in Optimistic mode (float division differs across platforms)

**Fix:** Use the `Fp` fixed-point type from [CoreGame.FixedPoint](https://github.com/CoreGameIO/SharedLibs/tree/main/FixedPoint) ([`CoreGame.FixedPoint`](https://www.nuget.org/packages/CoreGame.FixedPoint) NuGet / Unity UPM git URL: `https://github.com/CoreGameIO/SharedLibs.git#upm/fixedpoint`). `Fp` is a Q48.16 `long`-backed struct with full MemoryPack/MessagePack serialization — use it directly in `ISharedState` fields. See also `FpMath` for `Sqrt`, `Lerp`, `PowInt`, `Log2`. Alternatively, move the logic to `ExecutionMode.Server`.

---

## Execution Modes

### Optimistic (default)

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

### Server

Client waits for server. Used when client cannot know the result (ServerRandom, hidden state).

```
Client                          Server
  │                                │
  ├─ Send RPC ─────────────────►   │
  │  (waiting...)                  ├─ Execute
  │                                ├─ Record ServerRandom values
  │  ◄──── Return result+replay ───┤
  ├─ Replay with recorded values   │
```

### Local

No server communication. Instant. State changes are client-only. Good for UI state (selected card, open panel).

### CrossOptimistic

Client executes locally including cross-entity calls on cached local state. Server validates. Used for interactive cross-entity gameplay (trading, multiplayer moves).

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
```

### ServerPatch

Server-only execution. Instead of replay payload, server sends a state diff patch. Client applies patch directly.

**Use case:** Hotfixing server logic when clients can't be updated.

### Runtime Execution Mode Override

Override the `[MetaMethod]` default at runtime without recompilation:

```csharp
var modeProvider = client.ModeProvider as ExecutionModeProvider;

// Override specific method
modeProvider.SetMode("IProfileService", "SetName", ExecutionMode.Server);

// Override all methods in a service
modeProvider.SetServiceMode("IProfileService", ExecutionMode.Server);

// Reset to attribute defaults
modeProvider.Clear();
```

**Priority:** Specific method → Service-wide → Attribute default

---

## Shared State & Services

### State Definition

```csharp
[MemoryPackable(GenerateType.VersionTolerant)]  // or [MessagePackObject], or both
public partial class GameState : ISharedState
{
    [MemoryPackOrder(0)] public int Score { get; set; }
    [MemoryPackOrder(1)] public List<Player> Players { get; set; } = new();
    [MemoryPackOrder(2)] public GamePhase Phase { get; set; }
}
```

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
    // public string CallerId => Context.CallerId;
    // public IRandomService RandomService { get; set; }  // dependency

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

Use `[MetaInit]` on a method in your `[MetaServiceImpl]` class to initialize or migrate state when the entity grain activates. Called server-side during `OnActivateAsync` — **not** broadcast to clients (clients receive the already-initialized state via snapshot on subscribe).

```csharp
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState))]
public partial class ProfileServiceImpl : IProfileService
{
    [MetaInit]
    public Task<int> InitState(int version)
    {
        if (version < 1)
        {
            State.Energy = 50;
            State.MaxEnergy = 50;
            State.Money = 100;
            return Task.FromResult(1);
        }
        // Future migrations:
        // if (version < 2) { State.NewField = ...; return Task.FromResult(2); }
        return Task.FromResult(version);
    }
}
```

**Key points:**
- **Signature:** `Task<int> MethodName(int version)` — takes current version, returns new version
- `EntityGrainState.Version` is persisted alongside entity state
- Grain is **not** persisted after init alone — only when a player interacts (`_isDirty` guard)
- `Context.Random`, `Context.ServerRandom`, and `Config` are all available during `[MetaInit]`
- Use for: default state values, schema migration, version-gated field initialization

### Static Game Configuration

Define read-only config data with `[MetaConfig]`:

```csharp
[MetaConfig(Default = true)]
[MemoryPackable, MessagePackObject]
public partial class GameConfig
{
    [Key(0), MemoryPackOrder(0)] public int MaxEnergy { get; set; } = 100;
    [Key(1), MemoryPackOrder(1)] public int StarterGold { get; set; } = 500;
}
```

Link to service: `[MetaService(StateType = typeof(GameState), DefaultConfig = true)]` or `ConfigType = typeof(GameConfig)`.

Access in service code via the auto-injected `Config` property (also available during `[MetaInit]`).

Server provides config via `IMetaConfigProvider<TConfig>`:
- `CurrentVersion` — default `MetaConfigVersion` for new entities
- `GetConfig(MetaConfigVersion version)` — return config for specific version
- `GetDownloadUrl(MetaConfigVersion version)` — optional URL for client download

Config version is **pinned per entity** on first activation and persisted in `EntityGrainState.ConfigVersion`. Use `IConfigVersionResolver` for A/B tests and gradual rollouts.

### Context Properties

Inside `[MetaServiceImpl]` classes, the source generator injects:

```csharp
public MetaContext<TState> Context { get; set; }  // Full context
public TState State => Context.State;              // Shortcut to state
public string CallerId => Context.CallerId;        // Who called this method
// If config is configured:
protected GameConfig Config => (GameConfig)Context.Config!;
```

Available via Context:
- `Context.Random` — optimistic deterministic random (xoshiro128**)
- `Context.ServerRandom` — server-only random (null on client in Optimistic mode)
- `Context.ServerTimeTicks` — synchronized UTC ticks
- `Context.IsServer` / `Context.IsClient` — execution side
- `Context.ExecutionMode` — current execution mode
- `Context.EntityId` — current entity ID
- `Context.Config` — static game config (if configured via `[MetaConfig]`)

### Async Rules

- Service interface methods can be `void`, return a value, or return `Task`/`Task<T>` for CrossOptimistic
- Service implementations are synchronous (modify state directly)
- Server-side provider methods (`HandleCallAsync`) are always async

---

## Cross-Entity Calls

Service methods can call other entities via generated entity API:

```csharp
// In a CrossOptimistic service method:
var result = await Context.GetEntityApi<ITargetService>(targetEntityId).MethodAsync(args);
```

**On server:** `MetaProviderBase.EntityCallHandler` resolves target grain, calls `HandleCallFromEntityAsync`. Target entity executes, broadcasts to ITS subscribers, returns result.

**On client (CrossOptimistic):** Uses `CrossOptimisticMetaContext<TState>` for local execution on cached target state.

**Broadcast Suppression:** When Entity A calls Entity B, SessionManager prevents duplicate broadcasts for players subscribed to both.

---

## Triggers & Subscribers

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

Triggers execute server-side as nested operations within the parent call.

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

When `LobbyGrain` calls `EntityGrain.HandleExternalEventAsync("ILobbySubscriber", "OnMatchFound", data)`, the service trigger fires.

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

## Argument Transformers

Transform complex game objects into simple serializable types for RPC:

```csharp
// Simple transformer
[Transformer]
public class Vector3Transformer : IArgumentTransformer<Vector3, int[]>
{
    public int[] Box(Vector3 v) => new[] { v.X, v.Y, v.Z };
    public Vector3 Unbox(int[] a) => new Vector3(a[0], a[1], a[2]);
}

// State-aware transformer
[Transformer]
public class PlayerTransformer : IStateArgumentTransformer<Player, int, GameState>
{
    public int Box(Player player, GameState state) => player.Id;
    public Player Unbox(int id, GameState state) =>
        state.Players.FirstOrDefault(p => p.Id == id);
}
```

Usage in methods:
```csharp
[MetaMethod]
void Move([Transform(typeof(Vector3Transformer))] Vector3 position);

[MetaMethod]
void RawMove([SkipTransform] Vector3 position); // No transformation
```

---

## Matchmaking (Lobby)

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

**Flow:**
1. Player calls `RequestMatch` → profile entity calls `LobbyGrain.RequestMatchAsync()`
2. `LobbyGrain` adds player to queue, checks for enough players
3. When match forms: calls `EntityGrain.HandleExternalEventAsync()` for each matched player
4. Entity's `[ServiceTrigger]` fires, updating state with match info
5. All subscribers receive the match notification as a broadcast

**Client-side notification:**
```csharp
client.Resolver.OnMethodReplayed<MatchFoundArgs>(
    profileEntityId, "ILobbySubscriber", "OnMatchFound",
    args => Console.WriteLine($"Match found! ID: {args.MatchId}")
);

await profileApi.RequestMatchAsync(2);
```

---

## Authentication & Access

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

**UserOwned** is the only policy that generates convenience extension methods on MetaClient. For all other policies, use the generic `GetServiceAsync<TApiClient>(entityId)`.

**UserOwned service** — entityId is always the player's own ID:
```csharp
// Generated convenience method (no entityId needed):
var profileApi = await client.GetProfileServiceAsync();
var profileState = client.GetProfileState();
```

**Authorized / Open / OwnerOnly service** — requires explicit entityId:
```csharp
// Must provide entityId explicitly:
var expApi = await client.GetServiceAsync<ExpeditionServiceApiClient>(expeditionEntityId);
var expState = client.GetState<ExpeditionState>(expeditionEntityId);
```

**Authorized services** must implement `IsAuthorized` in the service impl:
```csharp
public bool IsAuthorized(string playerId)
{
    return State.OwnerPlayerId == playerId;
}
```

### JWT Auth

```csharp
// Server
builder.Services.AddMetaAuth(options =>
{
    options.SecretKey = "your-32-char-minimum-secret-key!!";
});
builder.Services.AddSingleton(new MetaTransportOptions { RequireAuthentication = true });
app.MapMetaAuthEndpoints();

// Client
var login = await MetaClient.LoginAsync($"{serverUrl}/meta/auth", deviceId: "unique-device-id");
var connection = new SignalRConnection($"{serverUrl}/meta", accessToken: login.Token);
var client = new MetaClient(connection, serializer, new MetaClientOptions { PlayerId = login.PlayerId });
```

**Enforcing auth:** `MetaTransportOptions.RequireAuthentication = true` rejects anonymous connections at SessionConnect. Additionally, you can add `[Authorize]` on a hub subclass or `.RequireAuthorization()` on endpoint mapping for middleware-level protection.

---

## Persistence

### Persistence Policy

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
| `EveryNMinutes` | `PersistencePolicy.EveryNMinutes(5.0)` | Save when M minutes passed |
| `RequestsOrTime` | `PersistencePolicy.RequestsOrTime(10, 5.0)` | N requests OR M minutes |
| `OnDeactivationOnly` | `PersistencePolicy.OnDeactivationOnly()` | Max performance, risk of data loss |

### ForcePersist

Mark critical methods that must always persist state:

```csharp
[MetaMethod(ForcePersist = true, Mode = ExecutionMode.Server)]
bool ProcessPurchase(string itemId, int price);
```

Use for: purchases, currency operations, inventory changes.

---

## Code Generation

The SharedMeta source generator (`CoreGame.SharedMeta.Generator`) produces:

| Input | Generated Output |
|-------|-----------------|
| `[MetaService]` interface | `*Dispatcher.g.cs` — server-side method routing |
| `[MetaService]` interface | `*ApiClient.g.cs` — typed client API with async methods |
| `[MetaService]` interface | `*ServiceExtensions.g.cs` — DI registration helpers |
| `[MetaServiceImpl]` class | `*.Context.g.cs` — Context/State/dependency injection |
| `ISharedState` class | `*PatchWrapper.g.cs` — change tracking for ServerPatch mode |
| `ISharedState` class | `*PatchApplier.g.cs` — client-side patch application |
| All `[MetaService]` in assembly | `ServerMetaConfiguration.g.cs` — MetaProvider + service registration |
| `[Transformer]` classes | `TransformerRegistrations.g.cs` — auto-registration |

**Do not write** dispatcher, API client, or context injection code manually — it's all generated.

### Method Signature Validation

The generator produces FNV-1a 64-bit hashes of every method's canonical signature. Validation runs at connection time — if client and server signatures don't match, the connection is rejected.

**What triggers a mismatch:** Changing parameter types/order, return type, renaming without Alias, adding/removing parameters.

**What does NOT:** Adding new methods, changing Mode, Version, ForcePersist.

### Method Version

For gradual rollout when supporting old and new clients:
```csharp
[MetaMethod(Mode = ExecutionMode.Optimistic, Version = 0)]
bool PlayCard(Card card);

[MetaMethod(Mode = ExecutionMode.Optimistic, Version = 1, Alias = "PlayCard")]
bool PlayCardV2(Card card, bool autoDefend);
```

---

## Attribute Reference

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[MetaService]` | Interface | Marks shared service for code generation |
| `[MetaMethod]` | Method | Configures execution mode, alias, versioning |
| `[MetaServiceImpl]` | Class | Marks service implementation for context injection |
| `[MetaInit]` | Method | State initialization/migration on grain activation |
| `[MetaConfig]` | Class | Marks a class as static game configuration |
| `[Trigger]` | Method | Auto-execute after condition on another method |
| `[ServiceTrigger]` | Method | Trigger on framework service event |
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
| `Version` | int | 0 | Method version for gradual rollout |
| `GenerateClientApi` | bool | true | Generate API client method |
| `SkipServerOnFalse` | bool | false | Skip server call if local returns false/default |
| `ForcePersist` | bool | false | Always persist state after execution |

### MetaService Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StateType` | Type | required | State class type |
| `ConfigType` | Type | null | Explicit config type for this service |
| `DefaultConfig` | bool | false | Use the config class marked with `[MetaConfig(Default = true)]` |
| `AccessPolicy` | EntityAccessPolicy | Open | Subscribe access control |
| `SubscriberInterfaces` | Type[] | empty | Framework event subscriptions (e.g. ILobbySubscriber) |

---

## NuGet Package Map

| Package | Purpose |
|---------|---------|
| `CoreGame.SharedMeta.Core` | Core interfaces, attributes, MetaContext, random |
| `CoreGame.SharedMeta.Client` | MetaClient, ClientDispatcher, message buffer |
| `CoreGame.SharedMeta.Generator` | Roslyn source generator (analyzer) |
| `CoreGame.SharedMeta.Server` | ServerMetaContext, cross-entity calls |
| `CoreGame.SharedMeta.Server.Core` | EntityGrain, MetaProviderBase, storage |
| `CoreGame.SharedMeta.Orleans` | Orleans integration (LobbyGrain) |
| `CoreGame.SharedMeta.Transport.SignalR` | SignalR WebSocket transport |
| `CoreGame.SharedMeta.Transport.HttpPolling` | HTTP long-polling transport |
| `CoreGame.SharedMeta.Serialization.MemoryPack` | MemoryPack serializer |
| `CoreGame.SharedMeta.Serialization.MessagePack` | MessagePack serializer |
| `CoreGame.SharedMeta.Auth` | JWT authentication |
| `CoreGame.SharedMeta.Debug` | InProcess transport for testing |

---

## Common Patterns

### Adding a Method to an Existing Service

1. Add to the `[MetaService]` interface with `[MetaMethod(Mode = ...)]`
2. Implement in the `[MetaServiceImpl]` class
3. If new argument/return types — add serializer attribute (`[MemoryPackable]`/`[MessagePackObject]`) with `[MemoryPackOrder(n)]`/`[Key(n)]` on properties
4. Build — the generator updates dispatchers and API clients automatically

### Adding a New Service

1. Create state class with `[MemoryPackable(GenerateType.VersionTolerant)]`/`[MessagePackObject]`, `ISharedState`, all properties have `[MemoryPackOrder(n)]`/`[Key(n)]`
2. Create interface with `[MetaService(StateType = typeof(TState))]` extending `IMetaService`
3. Create implementation with `[MetaServiceImpl(typeof(IService), typeof(TState))]` (must be `partial`)
4. Register on server: `services.ConfigureMeta(svc => svc.AddTransient<IMyService, MyServiceImpl>());`
5. Build — generator creates dispatcher, API client, context injection, and updates ServerMetaConfiguration

### Adding New Fields (Version Tolerance)

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
- Always append new fields with the next ID
- Use nullable types or default values for new fields (old data won't have them)

### Server Setup Pattern

```csharp
var builder = WebApplication.CreateBuilder(args);

var serializer = new MemoryPackMetaSerializer();
builder.Services.AddSingleton<IMetaSerializer>(serializer);

var transformerRegistry = new TransformerRegistry();
TransformerRegistrations.RegisterAll(transformerRegistry);  // generated

builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .AddFileGrainStorage("Default", o => o.RootDirectory = "./data")
        .ConfigureServices(services =>
        {
            services.ConfigureMeta(svc =>
            {
                svc.AddTransient<IMyService, MyServiceImpl>();
            });
        });
});

builder.Services.AddSignalR().AddMetaMessagePackProtocol();

var app = builder.Build();
app.MapHub<MetaHub>("/meta");
app.MapMetaHttpEndpoints("/meta-http");  // optional: HTTP polling
app.Run();
```

### Client Setup Pattern

```csharp
var connection = new SignalRConnection(serverUrl);
var serializer = new MemoryPackMetaSerializer();
var client = new MetaClient(connection, serializer, new MetaClientOptions
{
    PlayerId = playerId,
    Diagnostics = new ConsoleDesyncDiagnostics()
});

TransformerRegistrations.RegisterAll(client.TransformerRegistry);
client.Resolver.RegisterAllServices();  // Generated

await client.ConnectAsync();

// --- Accessing services ---

// UserOwned services (AccessPolicy = EntityAccessPolicy.UserOwned):
// Generated convenience methods — no entityId needed (auto uses client.PlayerId)
var profileApi = await client.GetProfileServiceAsync();
var profileState = client.GetProfileState();

// All other services (Authorized, Open, OwnerOnly):
// Must provide entityId explicitly
var gameApi = await client.GetServiceAsync<GameServiceApiClient>(gameEntityId);
var gameState = client.GetState<GameState>(gameEntityId);

// Main loop (required for game engines)
while (true)
{
    client.Dispatcher.ProcessPendingBroadcasts();
    await Task.Delay(33);
}
```

**Unity:** Call `dispatcher.ProcessPendingBroadcasts()` from `MonoBehaviour.Update()` — never process broadcasts from a transport thread.

### Testing Pattern

```csharp
// In-process testing (no network)
var server = new InProcessServer(fixture.CreateHandlerFactory());
await using var client = new TestClientSetup(server, playerId: "player1");
await client.ConnectAsync();

var resolver = client.CreateResolver();
var api = await resolver.GetServiceAsync<MyServiceApiClient>(entityId);
await api.DoSomethingAsync(args);

var state = resolver.GetState<MyState>(entityId);
Assert.Equal(expected, state.Value);
Assert.Empty(client.DetectedIssues);  // No desyncs
```

---

## Desync Diagnostics

### Common Desync Causes

1. **Using `System.Random`** instead of `Context.Random` — different seeds
2. **Using `DateTime.Now`** instead of `Context.ServerTimeTicks` — clock difference
3. **Dictionary iteration order** — different on client vs server
4. **Floating point operations** — platform-dependent precision (use int/long/fixed-point)
5. **LINQ with unordered collections** — `FirstOrDefault` on HashSet
6. **Async methods in broadcast replay** — must complete synchronously

### IDesyncDiagnostics

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

---

## Pitfalls to Avoid

1. **Missing `partial`** — source generator cannot extend the class, build fails
2. **Missing `[MemoryPackOrder(n)]`/`[Key(n)]`** — serializer uses source declaration order, reordering or inserting fields breaks deserialization
3. **Using `System.Random`** — guaranteed desync between client and server
4. **Forgetting serializer attribute on nested types** (`[MemoryPackable]`/`[MessagePackObject]` + `[MemoryPackOrder]`/`[Key]`) — serialization exception at runtime
5. **Calling async I/O in service methods** — services run synchronously inside the state mutation
6. **Modifying state outside service methods** — bypasses replay tracking, causes desync
7. **Reusing `[MemoryPackOrder(n)]`/`[Key(n)]` after removing fields** — safe to skip numbers, but never reuse old values for new fields
8. **Using `float`/`double` in Optimistic logic** — platform-dependent, causes desync
9. **Using `DateTime.Now`** — clock difference between client and server
10. **Dictionary iteration in deterministic logic** — order is not guaranteed across platforms
