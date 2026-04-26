# SharedMeta User Guide

Quick-start reference for projects using the SharedMeta framework.

## Installation

### NuGet Packages

Shared project (game logic):
```xml
<PackageReference Include="CoreGame.SharedMeta.Core" Version="0.13.0" />
<PackageReference Include="CoreGame.SharedMeta.Generator" Version="0.13.0" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Server project:
```xml
<PackageReference Include="CoreGame.SharedMeta.Server" Version="0.13.0" />
<PackageReference Include="CoreGame.SharedMeta.Server.Core" Version="0.13.0" />
<PackageReference Include="CoreGame.SharedMeta.Orleans" Version="0.13.0" />
<PackageReference Include="CoreGame.SharedMeta.Transport.SignalR" Version="0.13.0" />
<PackageReference Include="CoreGame.SharedMeta.Serialization.MemoryPack" Version="0.13.0" />
```

Client project (.NET console):
```xml
<PackageReference Include="CoreGame.SharedMeta.Client" Version="0.13.0" />
<PackageReference Include="CoreGame.SharedMeta.Transport.SignalR" Version="0.13.0" />
<PackageReference Include="CoreGame.SharedMeta.Serialization.MemoryPack" Version="0.13.0" />
```

### Unity (UPM)

Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.coregame.sharedmeta": "https://github.com/CoreGameIO/SharedMeta.git#upm"
  }
}
```

Use **Tools > SharedMeta > Project Wizard** in Unity to scaffold server/client projects.

---

## Step 1: Define Shared State

State classes need serialization attributes and must implement `ISharedState`:

```csharp
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public partial class GameState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public int Score { get; set; }
    [Key(1), MemoryPackOrder(1)] public List<string> Items { get; set; } = new();
    [Key(2), MemoryPackOrder(2)] public string CurrentPlayerId { get; set; } = "";
}
```

Rules:
- Class must be `partial` (required for code generation)
- Use `GenerateType.VersionTolerant` for MemoryPack on persisted state classes (allows adding fields without breaking old data)
- Every property needs ordinal attributes: `[Key(n)]` (MessagePack) and/or `[MemoryPackOrder(n)]` (MemoryPack)
- All nested types also need serialization attributes
- `[GenerateSerializer]` / `[Id(n)]` (Orleans) are **not needed** on state classes

---

## Step 2: Define Service Interface

```csharp
[MetaService(StateType = typeof(GameState), AccessPolicy = EntityAccessPolicy.Open)]
public interface IGameService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    bool AddItem(string itemId);

    [MetaMethod(Mode = ExecutionMode.Server)]
    void GrantReward(int amount);

    [MetaMethod(Mode = ExecutionMode.Local)]
    void SelectItem(int index);
}
```

---

## Step 3: Implement the Service

```csharp
[MetaServiceImpl(typeof(IGameService), typeof(GameState))]
public partial class GameServiceImpl : IGameService
{
    // Auto-injected by source generator:
    //   MetaContext<GameState> Context
    //   GameState State

    public bool AddItem(string itemId)
    {
        State.Items.Add(itemId);
        return true;
    }

    public void GrantReward(int amount)
    {
        int bonus = Context.ServerRandom!.Next(10);
        State.Score += amount + bonus;
    }

    public void SelectItem(int index)
    {
        // Local-only, no server call
    }
}
```

---

## State Initialization (`[MetaInit]`)

Add a `[MetaInit]` method to your service implementation to initialize or migrate state automatically when the entity grain activates on the server. Clients receive the already-initialized state when they subscribe — no client-side init call needed.

```csharp
[MetaServiceImpl(typeof(IGameService), typeof(GameState))]
public partial class GameServiceImpl : IGameService
{
    [MetaInit]
    public Task<int> InitState(int version)
    {
        if (version < 1)
        {
            State.Score = 0;
            State.Items = new List<string> { "starter-sword" };
            return Task.FromResult(1);
        }
        // Add new migrations here:
        // if (version < 2) { State.NewField = ...; return Task.FromResult(2); }
        return Task.FromResult(version);
    }
}
```

- **Signature:** `Task<int> MethodName(int version)` — takes current version, returns new version
- Version is persisted per entity — migrations only run once
- `Context.Random`, `Context.ServerRandom`, and `Config` are all available during init
- The grain is not persisted after init alone — only after player interactions

---

## Static Game Configuration

Define read-only config data (balance, level design, etc.) separately from entity state.

### 1. Define Config

```csharp
[MetaConfig(Default = true)]
[MemoryPackable, MessagePackObject]
public partial class GameConfig
{
    [Key(0), MemoryPackOrder(0)] public int MaxEnergy { get; set; } = 100;
    [Key(1), MemoryPackOrder(1)] public int StarterGold { get; set; } = 500;
}
```

### 2. Link to Service

```csharp
[MetaService(StateType = typeof(GameState), DefaultConfig = true)]
public interface IGameService : IMetaService { ... }
```

Or explicitly: `ConfigType = typeof(GameConfig)`.

### 3. Access in Code

```csharp
[MetaServiceImpl(typeof(IGameService), typeof(GameState))]
public partial class GameServiceImpl : IGameService
{
    // Auto-injected: protected GameConfig Config

    public bool BuyItem(string itemId, int price)
    {
        if (State.Gold < price) return false;
        State.Gold -= price;
        return true;
    }

    [MetaInit]
    public Task<int> InitState(int version)
    {
        if (version < 1)
        {
            State.Gold = Config.StarterGold;  // Config available during init
            return Task.FromResult(1);
        }
        return Task.FromResult(version);
    }
}
```

### 4. Server Config Provider

```csharp
public class GameConfigProvider : IMetaConfigProvider<GameConfig>
{
    public MetaConfigVersion CurrentVersion => new(1, 1);
    public GameConfig GetConfig(MetaConfigVersion version) => new();
    public string? GetDownloadUrl(MetaConfigVersion version)
        => $"https://example.com/config/{version.Major}/{version.Minor}";
}

// Register in DI:
services.AddSingleton<IMetaConfigProvider<GameConfig>>(new GameConfigProvider());
```

### Config Version Pinning

Each entity persists its config version on first activation. Subsequent activations reuse the pinned version until explicitly upgraded. This supports gradual rollouts and A/B tests via `IConfigVersionResolver`.

### Client-Side Config (0.15.0+)

Each `[MetaConfig]` type is materialized by an `IClientMetaConfigProvider<TConfig>` registered on the resolver. The generator-emitted `Add{Service}Services()` extension installs a `StaticConfigProvider<TConfig>(new TConfig())` by default — out of the box, the client receives the bundled config compiled into shared code.

To override (download from server with disk caching), register a custom provider **before** `RegisterAllServices()`:

```csharp
resolver.RegisterConfigProvider<GameConfig>(new DownloadingConfigProvider<GameConfig>(
    urlResolver: client.ConfigDownloadUrlResolver(typeof(GameState).FullName!),
    downloader: UnityConfigDownloader.DownloadAsync,
    serializer: client.Serializer,
    cache: new FileConfigCache<GameConfig>(Application.persistentDataPath + "/cfg", client.Serializer)));
resolver.RegisterAllServices();
```

`CompositeConfigProvider<TConfig>(primary, fallback)` chains two providers — typical use is to fall back to the bundled snapshot when the server is unreachable.

---

## Authentication

### Server Setup

```csharp
builder.Services.AddMetaAuth(options =>
{
    options.SecretKey = "your-secret-key-minimum-32-characters";
    options.TokenLifetime = TimeSpan.FromDays(7);
});
builder.Services.AddSingleton(new MetaTransportOptions { RequireAuthentication = true });
app.MapMetaAuthEndpoints(); // POST /meta/auth/login
```

### Client Login

Use `MetaAuth` — works on both Unity and .NET:

```csharp
// Simple login
var login = await MetaAuth.LoginAsync($"{serverUrl}/meta/auth", deviceId);
var connection = new SignalRConnection($"{serverUrl}/meta", accessToken: login.Token);
var client = new MetaClient(connection, serializer, new MetaClientOptions { PlayerId = login.PlayerId });
```

### Token Caching

Reuse tokens across app sessions with `ITokenStorage`:

```csharp
// Unity: PlayerPrefsTokenStorage stores token in PlayerPrefs
ITokenStorage storage = new PlayerPrefsTokenStorage();

// Returns cached token if still valid, otherwise makes login request
var login = await MetaAuth.EnsureAuthenticatedAsync($"{serverUrl}/meta/auth", deviceId, storage);

// Logout — clears stored token
MetaAuth.ClearToken(storage);

// Reset device binding (0.10.1+) — force-unlinks deviceId from current player.
// Useful for "Reset progress" / "Switch account" buttons. Next login creates a new player.
// Works even when the device is the player's only auth key (unlike /unlink).
await MetaAuth.ResetDeviceAsync($"{serverUrl}/meta/auth", deviceId, login.Token, storage);
```

For other platforms, implement `ITokenStorage` (3 methods: `Load`, `Save`, `Clear`).

> **Unity note**: `UnityMetaAuth` auto-registers via `[RuntimeInitializeOnLoadMethod]` — no manual setup needed. Unity-dependent auth code (`PlayerPrefsTokenStorage`, `UnityMetaAuth`) lives in the `SharedMeta.Auth.Client` assembly. If your asmdef has explicit references, add `SharedMeta.Auth.Client`.

> **Custom auth provider (0.9.3+)**: To bypass the HTTP auth flow entirely (local backend, Firebase, PlayFab, etc.), implement `IMetaAuthProvider` and set `MetaAuth.Provider = yourProvider` at startup. All `MetaAuth` calls will route through it. See `SharedMeta.Backend.Local`'s `LocalMetaAuthProvider` for a reference implementation.

---

## Push-Based Change Tracking

Track state field changes for reactive UI binding. Client-only — zero server overhead.

### 1. Mark fields with `[Tracked]`

```csharp
[MemoryPackable, MessagePackObject]
public partial class GameState : ISharedState
{
    [Key(0), MemoryPackOrder(0), MemoryPackInclude, Tracked] private int _gold;
    [Key(1), MemoryPackOrder(1), MemoryPackInclude, Tracked] private int _health = 100;
    [Key(2), MemoryPackOrder(2)] public string Name { get; set; } = "";  // not tracked
}
```

Rules: field must be **private**, underscore-prefixed, with serialization attribute. Add `[MemoryPackInclude]` for MemoryPack. Generator creates public property (`_gold` → `Gold`) with tracking setter.

### 2. Subscribe to changes

```csharp
// Register once at startup
TrackedGameState.Register();
TrackedGameState.OnChanged += args =>
{
    if (args.HasChange((int)TrackingProperty.GameState_Gold))
    {
        var leaf = args.FindLeaf((int)TrackingProperty.GameState_Gold);
        if (leaf != null)
            goldText.text = $"Gold: {leaf.Value.NewValue.IntValue}";
    }
};

// Cleanup
TrackedGameState.Unregister();
```

Changes fire automatically after method execution (optimistic replay, server broadcast, reconnect). Multiple field changes in one method call are batched into a single notification.

Generated API clients also fire `OnStateMutated` after any state change — use it when you don't need per-field granularity:
```csharp
api.OnStateMutated += () => UpdateUI(api.State);
```

For a polling signal instead of a subscription, use `int MutationCount` (0.13.1+) — bumped on every mutation across every execution mode (Optimistic, CrossOptimistic, Server, ServerPatch, ServerReplace, broadcast, subscriber-event broadcast, reconnect). Since 0.14.0 the counter is **shared across every API client on the same entity** and tracks "anything happened to this entity" regardless of which service triggered it:
```csharp
if (api.MutationCount != _lastSeen) { _lastSeen = api.MutationCount; Refresh(); }
```
`OnStateMutated` fires on the same set of events; pick whichever fits — event for push, counter for polling. Without an ApiClient: `client.Resolver.GetStateContainer<TState>(entityId).MutationCount` exposes the same value, and `.OnMutated` exposes the same event.

### 3. Access config from client

```csharp
// After subscribing to an entity, get its resolved config
var config = client.GetEntityConfig<GameConfig>(entityId);
```

---

## Execution Modes

| Mode | Behavior | Use For |
|------|----------|---------|
| **Optimistic** | Client executes immediately, server validates. Rollback on mismatch. | UI-responsive actions (move, play card, buy item) |
| **Server** | Client waits for server response. | Loot drops, matchmaking, hidden state, ServerRandom |
| **Local** | Client-only, no RPC sent. | UI state, local previews, client-side filtering |
| **CrossOptimistic** | Like Optimistic but targets a different entity. | Cross-entity interactions (trading, attacking) |
| **ServerPatch** | Server sends state diffs instead of full state. | Large state optimization, bandwidth savings |
| **ServerReplace** | Server sends full serialized state. Client replaces state wholesale. | Full state regeneration (map gen, full reset) |

### Query Calls (No Subscription)

Call any entity method without subscribing — lightweight read-only RPC:

```csharp
// In service interface:
[MetaMethod(Mode = ExecutionMode.Query)]
Task<PlayerBriefInfo> GetBriefInfo();

[MetaMethod(Mode = ExecutionMode.Query, OpenAccess = true)]  // bypasses access policy
Task<PlayerBriefInfo> GetPublicInfo();

// Client usage (generated QueryApi):
// Create once
var profileQuery = new ProfileServiceQueryApi(connection, serializer);
// Per-entity proxy
var api = profileQuery.EntityApi("player-123");
var info = await api.GetBriefInfoAsync();
```

- No state sync, broadcasts, replay, or persistence
- `OpenAccess = true` bypasses EntityAccessPolicy for public data
- Query methods must return a value (not void)

### Signal Methods (Fire-and-Forget)

One-way RPC — client does not wait, server does not respond. Use for heartbeat, telemetry, and notifications that do not mutate shared state:

```csharp
// In service interface:
[MetaMethod(Mode = ExecutionMode.Signal)]
void NotifyHeartbeat(long clientTicks);

// Client usage (generated synchronous Signal overload):
api.NotifyHeartbeatSignal(DateTime.UtcNow.Ticks);  // returns instantly, no await
```

- Void return only; no RequestId tracking, no retry, no broadcast
- Server executes read-only: no sequence increment, no persistence, no broadcasts
- `[ServerMetaService]` bridges may be called from signal bodies (real side-effects happen, but recording is silently discarded — there is no replay payload)
- `EntityAccessPolicy` is still enforced (same as regular methods)
- Cannot combine with `Query`, explicit non-default `Mode`, or `Sync`
- Legacy `[MetaMethod(Signal = true)]` bool is deprecated (`CS0618`); migrate to `Mode = ExecutionMode.Signal`

**Transport shape:** InProcess dispatches directly to the grain; SignalR uses `HubConnection.SendAsync` (no wire-level ACK awaited); HttpPolling `POST /meta-http/signal` → `202 Accepted` before execution completes.

---

## Deterministic Random

**Never use `System.Random` in shared logic.** It causes desyncs.

```csharp
// Optimistic random — identical on client and server
int roll = Context.Random!.Next(6) + 1;
float chance = Context.Random!.NextFloat();

// Server random — generated on server, replayed on client
int loot = Context.ServerRandom!.Next(100);
```

- `Context.Random` — xoshiro128**, same seed on both sides
- `Context.ServerRandom` — recorded on server, replayed on client via payload

### Named random streams — `[NamedRandom]`

Declare independent streams on the state when different mechanics must not share scroll position (adding a call to one system should not shift values in another):

```csharp
[SharedState]
[NamedRandom("Combat")]
[NamedRandom("Loot")]
[NamedRandom("MapGen", Seed = "map-v2")]  // pin seed across entities
public partial class GameState : ISharedState { ... }
```

The generator emits a typed property per attribute on every service `Context` partial for this state:

```csharp
int dmg  = CombatRandom.Next(100);      // independent from Loot / MapGen
int item = LootRandom.Next(drops.Count);
float h  = MapGenRandom.NextFloat();
```

Same algorithm and seed on server and client — Optimistic/Local methods see identical values both sides. Persisted per-entity, transmitted on subscribe, and caught up on `ServerPatch` / broadcast replay via per-index scroll deltas.

**Reordering `[NamedRandom]` attributes reseeds the affected slots** (positional list) — a deliberate code change, documented because there is no meaningful "previous" random value to preserve.

Keep `Context.Random` for the common case of one logical stream; reach for `[NamedRandom]` when mechanics need isolation.

---

## Server Time

```csharp
long now = Context.ServerTimeTicks;  // UTC ticks, synced with server
```

Available in all execution modes. Use for time-based mechanics (cooldowns, timers).

---

## Deterministic Math (Fixed-Point)

`float`/`double` arithmetic is **not deterministic** across platforms. For non-integer math in shared logic (Optimistic/CrossOptimistic), use the `Fp` fixed-point type from [CoreGame.FixedPoint](https://github.com/CoreGameIO/SharedLibs/tree/main/FixedPoint):

| Platform | Install |
|----------|---------|
| .NET | [`dotnet add package CoreGame.FixedPoint`](https://www.nuget.org/packages/CoreGame.FixedPoint) |
| Unity | UPM → Add by git URL: `https://github.com/CoreGameIO/SharedLibs.git#upm/fixedpoint` |

```csharp
using CoreGame.FixedPoint;

// Fp is Q48.16, backed by long — fully deterministic
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public partial class UnitState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public Fp PositionX { get; set; }
    [Key(1), MemoryPackOrder(1)] public Fp Speed { get; set; }
}

// In service
public void Move(Fp dx)
{
    State.PositionX += dx * State.Speed;
    Fp dist = FpMath.Sqrt(x * x + y * y);
}
```

`Fp` has built-in MemoryPack/MessagePack serialization — use it directly in state fields.

---

## Service Dependencies

Inject other services via the `[MetaServiceImpl]` attribute:

```csharp
[MetaServiceImpl(typeof(IGameService), typeof(GameState), typeof(IRandomService))]
public partial class GameServiceImpl : IGameService
{
    // Auto-injected: IRandomService RandomService
}
```

---

## Cross-Entity Calls

```csharp
[MetaMethod(Mode = ExecutionMode.CrossOptimistic)]
Task<bool> TradeWith(string targetEntityId, Item item);
```

The framework automatically routes the call to the target entity's grain on the server. The first parameter with type `string` is extracted as the target entity ID.

### Read-Only State Access

```csharp
// Read another entity's state (no method call, no mutation)
var otherState = await Context.GetState<ShardState>("shard_north");
```

System method on `MetaContext`. Server reads via `[AlwaysInterleave]` grain method (deadlock-safe). Result recorded for deterministic client replay. Returns `null` if entity type is unknown.

### Mid-Method State Persistence

```csharp
// Force-persist entity state at this point (server: saves to storage, client: no-op)
await Context.SaveStateAsync();
```

Use for pseudo-transactional patterns: mutate state → checkpoint → send acknowledgement to another entity. If the grain crashes after `SaveStateAsync()` but before the ACK, the saved state survives and the client can retry the ACK on reconnect.

---

## Triggers & Subscribers

### Triggers (server-to-client push)
```csharp
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileService : IMetaService
{
    [MetaTrigger]
    void OnRewardReceived(int amount);
}
```

### Subscribers (cross-entity events)
```csharp
[MetaService(StateType = typeof(GameState), Subscriber = typeof(ILobbySubscriber))]
public interface IGameService : IMetaService { ... }
```

---

## Server Runner (Unity Editor)

Launch and manage the server directly from Unity without switching to a terminal.

**Open:** `Tools > SharedMeta > Server Runner`

### Setup
1. Click **"..."** to browse to your server `.csproj` file (e.g., `MyGame.Server/MyGame.Server.csproj`)
2. If you used the Project Wizard, the path is auto-detected

### Controls
- **Start** — runs `dotnet run --project <your.csproj>` and streams console output
- **Stop** — terminates the server process (including Orleans silo)
- **Clear** — clears the console log
- **IDE** — opens the server solution in your default IDE (Rider, Visual Studio, VS Code)
- **Reveal** — opens the server project folder in file explorer

### Features
- Console output with color-coded errors (red) and warnings (yellow)
- Search/filter console output
- Auto-scroll toggle
- Status indicator: Stopped / Starting / Running / Stopping
- **Survives domain reload** — the server keeps running when scripts recompile or you enter Play mode
- **Extra Args** field for additional CLI arguments (e.g., `--configuration Release`)
- Server is automatically stopped when Unity Editor closes

---

## Checklist: Adding a New Method

1. Add method to `[MetaService]` interface with `[MetaMethod(Mode = ...)]`
2. Implement in `[MetaServiceImpl]` class
3. If using new argument/return types — add `[MemoryPackable, MessagePackObject]` + `[Key(n), MemoryPackOrder(n)]`
4. Build shared project (generator runs automatically)
5. On client: use generated `IXServiceApiClient` to call the method

## Checklist: Adding a New Service

1. Create `[MetaService]` interface extending `IMetaService`
2. Create `[MetaServiceImpl]` class (must be `partial`)
3. Register in server DI: `services.ConfigureMeta(svc => svc.AddTransient<IMyService, MyServiceImpl>());`
4. On client: `client.Resolver.RegisterAllServices();` (generated)
5. Build both projects

## Checklist: Adding a New Entity Type

1. Create new state class implementing `ISharedState`
2. Create services for that state type
3. Register provider on server via generated `ServerMetaConfiguration`
4. Subscribe on client with `client.SubscribeAsync<MyState>(entityId)`
