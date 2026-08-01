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
6.5. [Server-Only Services (Bridges)](#65-server-only-services-bridges)
6.6. [Stateless Meta Services](#66-stateless-meta-services)
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

**Unity?** Use **Tools > SharedMeta > Project Wizard** — it generates all of the above in one click. See [Tutorial](#24-tutorial-building-your-first-service) for a detailed walkthrough.

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

`[MemoryPackOrder(n)]` (or `[Key(n)]` for MessagePack) provides version tolerance — you can add new fields without breaking existing persisted state. `GenerateType.VersionTolerant` ensures MemoryPack stores field orders explicitly, allowing safe addition/removal of fields in persisted data.

**Persistence vs transport serialization.** The transport serializer (`IMetaSerializer` — MemoryPack or MessagePack) carries state over the wire and into replay payloads. On the server, persisted grain state passes through whatever **Orleans storage provider** you registered (Azure Tables, Redis, ADO.NET, the bundled `FileGrainStorage` in its default Orleans mode, etc.) — those providers use the **Orleans serializer**, which requires `[GenerateSerializer]` + `[Id(n)]` on every type that ends up in persisted grain state, including your `ISharedState` and the DTOs nested inside it. The Unity package ships `Orleans.Stubs` with no-op `[GenerateSerializer]` / `[Id]` attributes, so client-side code compiles without referencing the real Orleans NuGets.

If you only ever use `FileGrainStorage` with `UseOrleansSerializer = false`, you can skip the Orleans attributes — persistence will then go through `IMetaSerializer` and only the MemoryPack/MessagePack attributes matter. For any production-grade storage provider, add the Orleans attributes.

### Service Interface

```csharp
[MetaService(StateType = typeof(GameState), AccessPolicy = EntityAccessPolicy.Open)]
public interface ICardGameService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    bool PlayCard(Card card);

    [MetaMethod(Mode = ExecutionMode.Server)]
    void DealCards();

    [MetaMethod(Mode = ExecutionMode.LocalQuery)]
    int CardsInHand();   // client-side read over State, no RPC

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

Use `[MetaInit]` on a method in your `[MetaServiceImpl]` class to initialize or migrate state. Two signatures are supported — the generator detects the parameter count and emits the matching call shape:

```csharp
// Legacy single-arg form
[MetaInit] public Task<int> Init(int version) { ... }

// Two-arg form (0.19.0+) — also receives the target schema for this step
[MetaInit] public Task<int> Init(int version, int target) { ... }
```

The two-arg form pairs naturally with `[MetaStateVersion]` migration breakpoints (see [Per-Client Config Branches & State Migration](#per-client-config-branches--state-migration)) — `target` is the schema version the framework wants to reach in this step, so you can branch independently of which migration step triggered the call:

```csharp
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState))]
public partial class ProfileServiceImpl : IProfileService
{
    [MetaInit]
    public Task<int> InitState(int version, int target)
    {
        if (version < 1 && target >= 1)
        {
            // Base init — Context.Config pinned to 1.x branch.
            State.Energy = Config.StartEnergy;
            State.Money  = Config.StartMoney;
        }
        if (version < 2 && target >= 2)
        {
            // 1→2 migration — Context.Config pinned to 2.0 transition version.
            State.NewField = Config.NewFieldDefault;
        }
        return Task.FromResult(Math.Max(version, target));
    }
}
```

**When `[MetaInit]` runs (changed in 0.19.0):**
- **Activation no longer drives migration.** `OnActivateAsync` only loads persisted state.
- First-time init and lazy migration run from `SubscribeAsync` (when a client subscribes) and from `HandleCallAsync` / `HandleQueryAsync` (when a method is dispatched). Both paths cap migration to the connecting client's resolved config branch — see [Per-Client Config Branches & State Migration](#per-client-config-branches--state-migration).
- For services with `[MetaInit]` but no `[MetaStateVersion]`, the generator emits a minimal lazy-init path that runs `Init` exactly once on first interaction.
- Returned version is saved to `EntityGrainState.Version`.
- Persistence only happens when a player interacts with the entity (the `_isDirty` flag is not set by init alone) — this prevents creating persistent state for grains that were activated but never used.

**Signatures:** `Task<int> MethodName(int version)` or `Task<int> MethodName(int version, int target)`. Returns the new version.

**Available during `[MetaInit]`:**
- `Context.Random` and `Context.ServerRandom` — available for deterministic initialization (e.g., map generation)
- `Config` — pinned to the appropriate branch for this step. For base init (target=1) without an explicit `[MetaStateVersion(1, …)]`, pinned to `(1, 0, 0)`. For migration steps, pinned to the step's transition version (so a 1→2 migration sees `Config@2.0`, not the latest).
- `Context.Version` — current schema version (the source version of this step).
- `Context.ConfigVersion` — the `MetaConfigVersion` matching `Config`.
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

Config uses a three-part version: `Major.Minor.Patch` (extended from `Major.Minor` in 0.19.0).

- **Major** = schema version. Changes when config structure changes (requires client update).
- **Minor** = data version. Changes when config values change (same schema).
- **Patch** = hotfix version. Used by `IMetaConfigProvider.ResolveLatestMatching(major, minor)` to pick the latest patch within a (major, minor) range.

```csharp
public readonly struct MetaConfigVersion
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
}
```

Parses string forms via `MetaConfigVersion.Parse("1.2.3")`. Comparison operators (`<`, `<=`, `>`, `>=`, `==`) compare component-wise. `default(MetaConfigVersion) == (0, 0, 0)`.

### Server-Side Config Provider

Implement `IMetaConfigProvider<TConfig>` and register in DI:

```csharp
public class GameConfigProvider : IMetaConfigProvider<GameConfig>
{
    // 0.21.0+: no CurrentVersion. The provider serves bytes for any requested
    // version; "which version applies" is decided per-call by the framework from
    // the caller's client app version (or, for [EntityScope(Global)] entities,
    // from IConfigVersionResolver.CurrentClientVersion). For Orleans-backed,
    // hot-reloadable storage use BroadcastingConfigProvider<TConfig> + IConfigRegistry
    // (services.AddSharedMetaConfigVersioning()).

    public GameConfig GetConfig(MetaConfigVersion version)
    {
        // Return config for the requested version (load from files / DB / etc.)
        return new GameConfig();
    }

    public string? GetDownloadUrl(MetaConfigVersion version)
    {
        // Return URL for client to download this config version.
        // Return null if config is bundled with the client.
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

### Per-Call Config Resolution

> **Changed in 0.19.0**: removed per-entity config pinning. Config is now resolved **per RPC call** based on the caller's client version, so each subscriber sees the config branch appropriate for their app version. This makes optimistic execution consistent between client and server for any given call, and removes the "two services on one entity want different configs" deadlock.

Flow on each RPC:

1. Client sends RPC with its `clientVersion` (carried in `RpcCall.CallerClientVersion`, populated by the connection handler from session state — clients don't set it explicitly).
2. Generated provider looks up `clientVersion → resolved MetaConfigVersion` via `[MetaConfigVersion]` rules on the config class. **Cached per-grain** by `clientVersion`.
3. Looks up `resolved version → TConfig instance` via `IMetaConfigProvider<TConfig>.GetConfig(version)`. **Cached per-grain** by version.
4. `MetaContext.Config` is set to that instance for the duration of the call.
5. Both caches invalidate when `IMetaConfigProvider.CurrentVersion` advances (runtime patch deploy).

`EntityGrainState.ConfigVersion` was **removed in 0.19.0** — `Id(6)` is reserved as a tombstone in the serialization contract so existing persisted data deserializes cleanly. Config is no longer pinned per-entity; it's resolved per-call from the connecting client's version, so a single grain serves multiple branches without re-activation.

### Config-Dependent Mutations on Shared Entities

Per-call resolution makes **the caller** consistent with the server, but broadcast-replay-based modes (`Optimistic`, `CrossOptimistic`, `Server`) **replay the method on other subscribers** with their own config. If a method mutates state using `Config.X` and `X` differs across config branches, replays diverge.

**Rules of thumb:**

- **Owned entities (`UserOwned`, `OwnerOnly`)** — single subscriber = caller. No broadcast divergence possible. `Optimistic` with `Config.X` is safe and idiomatic.

- **Shared entities (`Open`, `Authorized` with multiple subscribers)** — choose one of:
  1. **`ExecutionMode.ServerPatch` / `ServerReplace`** — server computes, broadcasts state diff (or full snapshot) instead of replay payload. Other subscribers apply the diff bytewise; their config is irrelevant. **Recommended for any shared mutation that uses `Config.X` in a formula.**
  2. **`[MetaStateVersion]` schema gate** — declare that schema N requires config ≥ X. After any client connects with config X+, the entity migrates to schema N and `IsClientConfigCompatible` rejects subscribers on lower configs. All remaining subscribers are on the same branch, replay is consistent.
  3. **Config-stable formulas** — if the method's mutation doesn't depend on `Config` (e.g. `Move` just changes coordinates), no special mode is needed.

Methods that read `Config.X` but don't mutate state (queries, cosmetic reactions) are always safe across branches — different subscribers just get different views, no divergence.

### Config Version Resolver (A/B Tests, Gradual Rollouts)

`IConfigVersionResolver` is the per-server policy point for config-version selection. It owns two decisions: (a) the default *client app version* used by server-internal callers and `[EntityScope(Global)]` entities, and (b) the config version a fresh entity adopts on first activation. Required when the project uses any `IMetaConfigProvider<>` or declares any `[EntityScope(EntityScope.Global)]` state.

```csharp
public class AbTestConfigResolver : IConfigVersionResolver
{
    // 0.21.0+: required. The framework substitutes this for the calling client
    // version on server-internal calls (timers, background jobs, server-only
    // services) and on [EntityScope(Global)] entities. Typically wired to the
    // host's latest deployed client build via the release pipeline.
    public string CurrentClientVersion => "2.0.0";

    public MetaConfigVersion ResolveVersion(
        string stateTypeName, string entityId, MetaConfigVersion defaultVersion)
    {
        // `defaultVersion` is derived by the framework by resolving CurrentClientVersion
        // through the config class's [MetaConfigVersion] rules. Override to A/B-test:
        if (entityId.GetHashCode() % 10 == 0)
            return new MetaConfigVersion(defaultVersion.Major, defaultVersion.Minor + 1);

        return defaultVersion;
    }
}

services.AddSingleton<IConfigVersionResolver>(new AbTestConfigResolver());
```

### Multiple Configs (`[ServiceConfig]`) — 0.33.0+

A service can declare N configs, each independently versioned/published (own
`IMetaConfigProvider<TConfig>` / `IClientMetaConfigProvider<TConfig>`, own `[MetaConfigVersion]`
rules) — all symmetric, no privileged "primary". Unlike multi-config siblings
(`Get{Iface}SiblingAsync()`, see [Cross-Entity Calls](#6-cross-entity-calls)) or
`[StatelessMetaService]`'s sibling-style resolution, these resolve **synchronously in every
execution mode**, including `Optimistic`/`CrossOptimistic` where the client also executes the
method body predictively.

```csharp
[MetaService(StateType = typeof(ShopState))]
[ServiceConfig(typeof(ShopConfig), "Shop")]
[ServiceConfig(typeof(BalanceConfig), "Balance")]
[ServiceConfig(typeof(SeasonConfig), "Season")]
public interface IShopService : IMetaService { ... }

public partial class ShopServiceImpl : IShopService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    public void Buy(int itemId)
    {
        var cost = Shop.BaseCost * Balance.BaseCost * Season.Multiplier;   // generated named accessors
        // ...
    }
}
```

A service can declare zero, one, or many `[ServiceConfig]` entries — there's no minimum, and no
entry is treated as "the" config. Positional: declaration order = wire/storage index — same
deliberate-reseed-on-reorder contract `[NamedRandom]` already has. Generator emits one named
typed accessor per entry: `protected {TConfig} {Name} => ({TConfig})Context.Configs![i]!;`.

**Legacy `[MetaService(ConfigType=...)]` / `Context.Config`:** `[Obsolete]` (compiler warning)
but fully functional — existing services keep working unchanged. When a service declares both
the legacy `ConfigType` and one or more `[ServiceConfig]` entries (a migration-in-progress mix),
the legacy config occupies wire index 0 and `[ServiceConfig]` entries follow at the remaining
indices; `Context.Config`/`Context.Configs` are independent lists (a service reads whichever it
declared). New services should use `[ServiceConfig]` exclusively.

**Wire shape:** config versions travel as a **list**, not a scalar — every per-op / subscribe
DTO (`MetaOperation.ExecutedConfigVersions`, `CallResponse<T>` family,
`SubscribeResponse`/`SessionConnectResponse`'s `ConfigVersions`) is `List<MetaConfigVersion>`
(index 0 = legacy config when declared). This replaced the pre-0.33 single-scalar
`ExecutedConfigVersion` / `ConfigMajor/Minor/PatchVersion` triple outright — a wire-breaking
change, not additive.

**Full parity with the legacy primary** — `[ServiceConfig]` entries get the same mechanics the
legacy `ConfigType` path has, not a reduced subset:
- **Pin support** — `Private`/`Shared`-scope entities pin each declared entry on first subscribe
  (`EstablishConfigPinsFromClientVersion`), same as the legacy primary; a later joiner sees the
  pinned branch, not their own resolved version.
- **`[EntityScope(Global)]` substitution** — every declared entry resolves under
  `IConfigVersionResolver.CurrentClientVersion` on a Global entity, so all observers see the
  same branch regardless of who's calling.
- **`[MetaStateVersion(N, "M.m", typeof(TConfig))]` schema-floor migration** — a
  `[ServiceConfig]`-declared type can gate a migration step exactly like the legacy primary can;
  `[NoMigrate]` pins that entry to the schema-floor branch, and the Breaking-schema compat gate
  rejects old clients the same way.
- **Cross-entity / `CrossOptimistic` propagation** — `GetICounterService(otherEntityId)`-style
  calls propagate every declared entry into the target entity's context
  (`ICrossEntityResolver.GetEntityConfigs`), not just the legacy primary.
- **Multi-config sibling resolution** — `Get{Iface}SiblingAsync()` resolves each sibling's own
  `[ServiceConfig]` entries independently and pins them to that sibling instance's settable
  property, without touching the caller's shared `Context`.

**Known limitation:** when multiple services share one state and declare *different*
`[ServiceConfig]` sets, they're aggregated into one list on the shared `Context.Configs`
(state-wide collapsing) — same simplification the legacy `ConfigType` already uses for
multi-service states sharing one entity.

### Per-Client Config Branches & State Migration

> **Added in 0.19.0**: a complete system for routing connecting clients to the correct config branch and migrating entity state schema as the live config advances — without locking older clients out of fresh entities.

The problem this solves: you ship config 2.0 with a new feature, your live ops promote `_configProvider.CurrentVersion = (2, 0)`, but a chunk of your players are still on the 1.x build. Without per-client branching, they either see broken state (the new feature's fields are zeroed) or get migrated to a schema they can't reason about. With per-client branches, 1.x clients keep getting `Config@1.x` and `state.Version=1`, while 2.x clients see `Config@2.0` and `state.Version=2` — same entity grain, different views per call.

#### `[MetaConfigVersion]` — client → config routing

Declare on the config class which client app version maps to which config version. Two grammars are supported on the same attribute, picked by which constructor / properties you use:

```csharp
[MetaConfig(Default = true)]
[MemoryPackable, MessagePackObject]
[MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]   // 1.x clients → 1.x configs (latest patch)
[MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]   // 2.x clients → 2.x configs
public partial class GameConfig { … }
```

**Pattern grammar** — three components matched independently:

| Form | Example | Meaning |
|---|---|---|
| **Literal** | `2.0.5` | Matches exactly. |
| **Capture** | `x` | Matches any value AND propagates: `Client="1.x.*", Config="1.x.*"` routes 1.5.0 → 1.5.* and 1.6.2 → 1.6.* with one rule. |
| **Range** | `2.2+` | Matches 2.2 or higher within the same major. |
| **Wildcard** | `*` | Matches any value (terminal — no propagation). |

Resolution picks the most-specific rule (literal > capture > range > wildcard, then by component depth). Multiple `[MetaConfigVersion]` attributes are allowed; first-match-after-sort wins.

The framework calls `IMetaConfigProvider<T>.ResolveLatestMatching(major, minor)` on captures to materialize the actual `Patch` value (e.g. `1.6.x` → `1.6.17` if that's the latest published 1.6 patch).

#### Per-call resolution flow

On every RPC the generated `MetaProvider` runs:

1. `clientVersion → resolved MetaConfigVersion` via the `[MetaConfigVersion]` resolver. Cached per grain.
2. `resolved version → TConfig instance` via `IMetaConfigProvider.GetConfig(version)`. Cached per grain.
3. `MetaContext.Config` and `MetaContext.ConfigVersion` are set for the duration of the call.

Both caches invalidate when `_configProvider.CurrentVersion` advances (runtime patch deploy via your live-ops dashboard).

`RpcCall.CallerClientVersion` carries the version through the wire. `MetaContext.CallerClientVersion` mirrors it for the duration of dispatch — cross-entity calls read it and propagate it forward, so a 1.x session whose Profile cross-calls a fresh Expedition entity sees the same `clientVersion` on both ends.

#### `[MetaStateVersion]` — schema migration breakpoints

Declare on a state class that schema N requires config ≥ X:

```csharp
[SharedState]
[MemoryPackable(GenerateType.VersionTolerant)]
[MetaStateVersion(2, "2.0", typeof(GameConfig))]   // schema 2 requires GameConfig >= 2.0
[MetaStateVersion(3, "3.0", typeof(GameConfig))]   // schema 3 requires GameConfig >= 3.0
public partial class ProfileState : ISharedState { … }
```

Multiple attributes with the same `StateVersion` form an **AND gate** — useful when two configs gate the same schema bump:

```csharp
[MetaStateVersion(3, "3.1", typeof(GameConfig))]   // schema 3 needs BOTH:
[MetaStateVersion(3, "1.4", typeof(SeasonConfig))] //   GameConfig >= 3.1 AND SeasonConfig >= 1.4
```

The framework calls `[MetaInit]` once per applicable step, with `Context.Config` pinned to that step's transition version (not the latest), so each migration runs against the config it was authored against. Sequential migrations (a profile that skipped intermediate versions) get one `[MetaInit]` call per unprocessed step in ascending order.

#### Client-aware migration cap

Migration is **never** driven by `_configProvider.CurrentVersion` alone. Each entry point caps the migration target to the connecting client's resolved branch:

| Entry point | Cap source |
|---|---|
| `EntityGrain.SubscribeAsync` | `ComputeSchemaCapForClient(clientVersion)` |
| `MetaProviderBase.HandleCallAsync` | `min(method's [MinStateVersion], ComputeSchemaCapForClient(call.CallerClientVersion))` |
| `MetaProviderBase.HandleQueryAsync` | same as HandleCallAsync |

So a 1.x client subscribing to a fresh entity gets schema 1 (base init only), even when the server's current is 2.0. A 2.x client subscribing later triggers lazy migration to schema 2.

#### `IsClientConfigCompatible` — per-entity gate

The framework also generates a per-entity gate on Subscribe:

> If the entity is already at schema N, reject any client whose resolved config branch can't satisfy schema N's threshold.

Example: a 2.x client connected first and migrated the profile to schema 2; a 1.x client trying to subscribe to *that* profile is rejected with a clear error (`"Your app version is too old for this entity's current state…"`). The check uses the same threshold table as `[MetaStateVersion]`.

#### `[NoMigrate]` and `[MinStateVersion(N)]` — per-method controls

Two attributes on `[MetaMethod]`s give you fine-grained migration control:

```csharp
[MetaMethod(Mode = ExecutionMode.Server)]
[NoMigrate]
void DepositGift(GiftItem item);

[MetaMethod(Mode = ExecutionMode.Server)]
[MinStateVersion(2)]
void UseSeasonalAbility(int abilityId);
```

- **`[NoMigrate]`** — the method skips lazy migration entirely and pins `Context.Config` to the **schema-floor branch** (the highest config branch that does not require migration past the entity's current persisted schema). Use for cross-entity "administrative" calls (inbox/gift sending) — sending a gift to a profile shouldn't force-upgrade that profile if its owner is still on an older client. The method body is responsible for being schema-tolerant.
- **`[MinStateVersion(N)]`** — caps migration at schema N. If the entity is below N, migrate up to N (no further); if at or above, no migration runs.

#### Server-side responsibilities

Your `IMetaConfigProvider<TConfig>` must implement the full surface that 0.19.0 expects:

```csharp
public class GameConfigProvider : IMetaConfigProvider<GameConfig>
{
    public MetaConfigVersion CurrentVersion => new(2, 0, 0);

    // Fetch a SPECIFIC historical version. Used at runtime AND for download URLs.
    // Cache or memoize — migration steps repeatedly request the same transition versions.
    public GameConfig GetConfig(MetaConfigVersion version) => …;

    // Resolve a Major.Minor capture to its latest patch. Called by the [MetaConfigVersion]
    // resolver when the rule has an `x` capture in the Patch slot.
    public MetaConfigVersion ResolveLatestMatching(int major, int minor) => …;

    // Optional. Used by DownloadingConfigProvider on the client (Unity) to fetch the
    // version-specific config bytes from your CDN.
    public string? GetDownloadUrl(MetaConfigVersion version) => …;
}
```

#### `MaxClientVersion` and downgrade tracking

`MetaTransportOptions.MaxClientVersion` lets the server explicitly bound the supported client range:

```csharp
builder.Services.AddSingleton(new MetaTransportOptions
{
    ServerVersion    = "2.0.0",
    MinClientVersion = "1.1.0",
    MaxClientVersion = "2.0.*",   // accept any 2.0.x, reject 2.1+
    RequireAuthentication = true,
});
```

`ClientVersionPolicy.Validate` uses inclusive Min/Max bounds rigorously (the old `clientMajor == serverMajor` check is gone), so you can publish hotfixes outside the server's own version range without surprise rejections.

`IPlayerVersionGrain` records the highest client version a player has ever connected with. Subsequent connects from a *lower* version are rejected with a clear "downgrade not allowed" error — so a player can't quietly roll back their app to dodge a forced migration.

### Entity Scope (`[EntityScope]`)

> **Added in 0.21.0**: declare the sharing model of an entity on its state class. The framework derives subscribe rules, runtime config-version pinning, and dispatch behaviour from this single attribute.

```csharp
[SharedState]
[EntityScope(EntityScope.Private)]   // implicit default — can be omitted
public partial class PlayerProfile : ISharedState { … }

[SharedState]
[EntityScope(EntityScope.Shared)]    // PvP match, party, raid
public partial class PvpMatch : ISharedState { … }

[SharedState]
[EntityScope(EntityScope.Global)]    // clan, leaderboard, global PvP
public partial class Clan : ISharedState { … }
```

| Scope | Subscribers | Config-version pin | Per-call config | Optimistic / CrossOptimistic |
|---|---|---|---|---|
| `Private` | Owner only (others may cross-entity-call without subscribing — e.g. send a gift). | Established on owner's first connect; survives grain's active lifetime; dropped on Orleans idle-deactivation. | From pin — every caller (owner, cross-entity gift, server-internal) sees the same config. | Safe. |
| `Shared` | First subscriber establishes pin; subsequent joiners are validated against it (patch differences tolerated — joiner downgrades to pinned patch; `Major.Minor` mismatch rejects with `EntityAccessDeniedException`). | First subscriber's resolved versions. | From pin. | Safe with caveat: see Known limitations below for cross-version observer scenarios. |
| `Global` | Open subscribe gated on `IsClientConfigCompatible`. | **Never pinned** — every call resolves freshly from `IConfigVersionResolver.CurrentClientVersion`. The provider throws if the resolver isn't configured. | Always under `CurrentClientVersion`-resolved version, regardless of the caller's own version. | **Not safe today** — see Known limitations. |

The pin is *runtime grain state*, not persisted on `EntityGrainState`. It dies with Orleans idle-deactivation — the next first-subscriber re-establishes it from scratch, naturally picking up newer configs and migrating state forward via `[MetaStateVersion]` thresholds.

**Cold calls into a deactivated Private entity** (no active subscriber, no pin) fall back to project policy via `IConfigVersionResolver`. If the resolver isn't registered the framework returns `default(MetaConfigVersion)` (0.21.0 transitional behaviour); strict-throw lands in a follow-up.

**Why `[EntityScope(Global)]` rejects `Optimistic`:** the server normalizes to `CurrentClientVersion` for dispatch, so the client's local-first optimistic execution runs under a different config branch than the server's — a guaranteed desync. Use `Server` / `ServerPatch` / `ServerReplace` / `Query` / `Signal` / `Local` on Global entities. (Compile-time `SHMETA_OPT_GLOBAL` diagnostic ships in a follow-up; today the mismatch is observable only at runtime via desync diagnostics.)

### Server-Side Config Download Endpoint (0.26.2+)

When the client's `DownloadingConfigProvider<TConfig>` fetches bytes, it asks the server "what URL do I hit for this version?" via the SignalR call `GetConfigDownloadUrl`. The host has to (a) answer that question with an `IConfigDownloadUrlResolver` and (b) actually serve the bytes at the URL it advertised. Two paired helpers (in the `SharedMeta.Server` namespace, shipped from `CoreGame.SharedMeta.Transport.SignalR`) eliminate the boilerplate:

```csharp
// (1) Register the URL resolver. Builds URLs in the form
//     {publicBaseUrl}{routePrefix}/{configType}/{major}.{minor}.{patch}
builder.Services.AddMetaConfigPublicUrl(
    publicBaseUrl: builder.Configuration["Server:PublicBaseUrl"]!,   // e.g. "https://api.example.com"
    routePrefix:   "/meta/config");                                   // default

// (2) Map the matching HTTP endpoint. Non-generic overload uses generator-emitted
//     IConfigByteSource — auto-routes by configType to the right IMetaConfigProvider<T>,
//     so every [MetaConfig] declared in the assembly is served from a single endpoint.
app.MapMetaConfigDownload();   // recommended for multi-config / future-proof setups
```

Both calls accept the same `routePrefix` — keep them in sync. The endpoint responds to `GET {routePrefix}/{configType}/{version}`; the non-generic overload picks the right config type per request based on `configType`.

**Single-config dedicated-route variant** — if you want one config served from its own unique route (e.g. to gate behind separate auth):

```csharp
app.MapMetaConfigDownload<SessionConfig>("/meta/config/session");   // route prefix must differ per TConfig
app.MapMetaConfigDownload<BalanceConfig>("/meta/config/balance");
```

The generic form binds one TConfig per route. Use the non-generic overload (default) for the typical case where one endpoint covers everything.

> **Critical DI boundary.** Both helpers register on **ASP.NET DI** (`builder.Services`), not on the silo's `services.ConfigureMeta(...)`. `MetaHub` resolves `IConfigDownloadUrlResolver` from ASP.NET DI; the minimal endpoint resolves `IMetaConfigProvider<TConfig>` and `IMetaSerializer` from there too. Registrations placed inside `siloBuilder.ConfigureServices(...)` go into the **Orleans silo's** independent container and are invisible to ASP.NET-side resolution. The two containers don't share singletons.
>
> If you need the provider on both sides (Orleans grain DI for server-side meta logic, ASP.NET DI for the download endpoint), register the same instance / call the same `AddSharedMetaConfigProvider<T>()` extension in both places.

Prior to 0.26.2 the framework registered the generated `GeneratedConfigDownloadUrlResolver` with `AddSingleton` — it won over any host registration unless you used `RemoveAll<IConfigDownloadUrlResolver>` first. As of 0.26.2 it uses `TryAddSingleton`; a host-side `AddSingleton<IConfigDownloadUrlResolver>(...)` (whether via `AddMetaConfigPublicUrl` or a custom class) wins automatically.

### Client-Side Config Flow (0.15.0+)

1. Client subscribes to entity → server includes `ConfigVersion` in the response.
2. Resolver looks up the `IClientMetaConfigProvider<TConfig>` registered for the service's `ConfigType`.
3. The provider materializes the config for that version — caching, downloading, fallback are all internal to the provider.
4. The resolved instance is cached on the entity's `EntityConnection` and surfaced to all API clients on the entity (`Context.Config`, `client.GetEntityConfig<TConfig>(entityId)`).

#### Registering a provider (required for `ConfigType`, optional for `DefaultConfig`)

> **Breaking in 0.17.0** — services that declare `ConfigType = typeof(X)` without `DefaultConfig = true` MUST register a provider before subscribing. The previous "auto-emit `StaticConfigProvider<T>(new T())` for everyone" behavior silently substituted an empty config and is gone. A missing provider now throws `InvalidOperationException` with the actionable recipe at first subscribe.
>
> Services with `[MetaService(..., DefaultConfig = true)]` retain the auto-default — that flag is now the explicit "an empty `new TConfig()` is acceptable as a fallback" contract.

`RegisterConfigProvider<T>` (without "Try") clobbers any auto-emitted default, so order relative to `RegisterAllServices()` doesn't matter — the explicit call always wins.

> **Pick by who owns the config values, not by what looks simplest.** The choice fixes how live-ops updates reach the client — not just bootstrap convenience.
>
> | Provider | Server can push a new config without rebuilding the client? | Offline / first-launch behaviour | Best for |
> |---|---|---|---|
> | `StaticConfigProvider` | **No** — `GetConfigAsync(version)` ignores the version argument and always returns the instance passed at construction. The server reports the version it pinned for the entity, but this provider hands back the bundled object regardless. | Always uses the bundled instance. | Bundled snapshot, LocalBackend, single-player, tests. |
> | `DownloadingConfigProvider` | **Yes** — fetches bytes for the version the server pinned, deserialises, optionally caches to disk. | Throws on first launch with no network and no cache hit. | Live-ops servers, balance-driven games, anything where a content team ships config without app updates. |
> | `CompositeConfigProvider(downloading, static)` | **Yes** for the primary path; falls back to the bundled snapshot when the primary throws. | Bundled snapshot when offline / pre-cache. | Real shipping clients — the default recommendation when both server delivery and offline robustness matter. |

```csharp
// (A) Preloaded instance — LocalBackend / single-player / bundled ScriptableObject.
//     Same instance can be passed to LocalServer.RegisterConfig<TState>(...) on the
//     server side; one object lives on both sides, no serialization, version is ignored.
resolver.RegisterConfigProvider<GameConfig>(
    new StaticConfigProvider<GameConfig>(loadedConfig));
resolver.RegisterAllServices();
```

```csharp
// (B) Server-pushed bytes with on-disk caching — real-server live ops.
using var http = new HttpClient();
resolver.RegisterConfigProvider<GameConfig>(new DownloadingConfigProvider<GameConfig>(
    urlResolver: client.ConfigDownloadUrlResolver,      // keyed by config type, not state type
    downloader:  url => http.GetByteArrayAsync(url),
    serializer:  client.Serializer,
    cache:       new FileConfigCache<GameConfig>("./config-cache", client.Serializer)));
resolver.RegisterAllServices();
```

```csharp
// (C) Composite — try server, fall back to bundled snapshot when offline.
resolver.RegisterConfigProvider<GameConfig>(new CompositeConfigProvider<GameConfig>(
    primary:  new DownloadingConfigProvider<GameConfig>(/* ...as in (B)... */),
    fallback: new StaticConfigProvider<GameConfig>(bundledSnapshot),
    onPrimaryFailed: ex => MetaLog.Warning($"[Config] download failed, using bundled: {ex.Message}")));
resolver.RegisterAllServices();
```

In Unity, replace `http.GetByteArrayAsync` with `UnityConfigDownloader.DownloadAsync` for IL2CPP/WebGL compatibility.

#### When `DefaultConfig = true` is appropriate

Set it only when `new TConfig()` is genuinely a usable fallback — e.g. configs whose default-constructed values match the gameplay defaults the design assumes when nothing is loaded. For most game configs (item registries, balance numbers, level data), the default ctor produces zeroed nonsense — leave the flag off and register an explicit provider.

### Accessing Config from Client Code

After subscribing to an entity, retrieve its resolved config:

```csharp
var config = client.GetEntityConfig<GameConfig>(entityId);
// Returns null if entity not connected or no config configured
```

### Server-side config seeding (`ConfigureConfigs`)

On the silo, `ConfigureConfigs` wires the versioned config registry and a cold-start bootstrap. You supply an `IConfigBootstrapper` (two typed methods: `GetVersionAsync<TConfig>` / `GetBytesAsync<TConfig>`) and a `ConfigSeedStrategy` that decides when the loader's bytes get published:

```csharp
services.ConfigureConfigs(o =>
{
    o.UseBootstrapper<ConfigBootstrapper>();      // project-emitted typed dispatcher
    o.Strategy = ConfigSeedStrategy.LoadIfHashDiff;
});
```

| Strategy | Publishes when |
|---|---|
| `LoadIfEmpty` | The registry has no version for the type yet (seed once, then admin owns it). |
| `LoadIfNew` (default) | The loader's exact version isn't already in the registry. |
| `LoadAlways` | Every start — `PublishIfChanged` makes same-content a no-op, drift overwrites. |
| `LoadIfHashDiff` | Content changed. The loader reports a stable `Major.Minor` branch (reported patch ignored; `null` → the latest branch in the registry); if the bytes differ from that branch's **latest patch**, they publish as `Major.Minor.(latestPatch+1)`. Identical content is a no-op. Structural `Major`/`Minor` bumps stay explicit. |

Use `LoadIfHashDiff` when config content is baked/derived and you want a content edit to auto-produce a new patch on the same branch without a manual bump — e.g. an image-baked or generated config where the `Major.Minor` line is stable across a release.

**Holding a branch against the seed.** An admin who uploads a config version by hand (via `IConfigAdminGrain.UploadAsync`) can pin its `Major.Minor` branch so the cold-start seed leaves it alone — otherwise the next restart would overwrite (`LoadAlways`) or auto-patch-bump over it (`LoadIfHashDiff`):

```csharp
await adminGrain.SetBranchHoldAsync("MyGame.GameConfig", "1.2", held: true, changedBy: "ops");
```

While held, the bootstrap skips that branch entirely (other branches still seed normally); `ConfigBranchInfo.Held` reflects the state in the admin overview. Release with `held: false` to let the seed manage the branch again.

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

### LocalQuery Mode (0.29.0+; replaces `Local`)

Read-only client-side compute over locally replicated state, no RPC. Method body returns a value computed from `State`; never executes on the server.

**Synchronous API by default (0.29.2+):** LocalQuery defaults to `Sync = SyncApi.OnlySync`, so the generator emits a single synchronous `{Method}Sync(...)` on `{Service}ApiClient` — the impl runs over the local `State` snapshot in the calling frame, no `await`, no round-trip, which is exactly what a local read wants. An explicit `Sync` is honoured: `None` emits only `{Method}Async`, `Generate` emits both. The LocalQuery async wrapper completes synchronously (`Task.FromResult`, still no RPC); it exists for forward-compat — a call site that already `await`s `{Method}Async` keeps compiling if the method later switches to a server-backed mode. The impl must return a non-`Task` value.

```csharp
[MetaMethod(Mode = ExecutionMode.LocalQuery)]
int CardsInHand();                              // interface; default → sync only
// client:
int n = api.CardsInHandSync();                  // synchronous, reads local State, no RPC

[MetaMethod(Mode = ExecutionMode.LocalQuery, Sync = SyncApi.Generate)]
int CardsInHand();                              // → both CardsInHandSync() and CardsInHandAsync()
```

**Contract:** must return a value (no `void` / bare `Task`); must not mutate State; must not call cross-entity services; must not consume `Context.Random`; requires the entity to be subscribed at call time.

**vs `Query`:** `Query` is server-roundtrip for entities NOT subscribed; `LocalQuery` is local-snapshot read for subscribed entities. Pick `LocalQuery` for cheap method-level computations over your own / any subscribed entity's state; `Query` when authoritative source must be the server.

Pre-0.29.0 `Local` allowed client-only writes (divergence anti-pattern — server never confirms; clients diverge forever). Removed in 0.29.0. UI-state mutations belong in a ViewModel / POCO outside SharedMeta.

#### `TryGetService` — allocation-free access on the hot path (0.29.3+)

`GetServiceAsync` is the right call for the first access (it subscribes + creates the client), but on an already-subscribed entity it still allocates a `Task<TApiClient>` per call. For a frame-critical read path, use the synchronous accessor instead:

```csharp
// generic form
if (client.TryGetService<PlayerProfileServiceApiClient>(client.PlayerId, out var api))
    return api.GetProfileSummarySync();   // sync LocalQuery read → zero allocation end-to-end

// generated typed convenience (mirrors Get{Service}Async):
//   UserOwned:                bool TryGet{Service}(out api)           // auto client.PlayerId
//   Open/Authorized/OwnerOnly bool TryGet{Service}(string entityId, out api)
if (client.TryGetPlayerProfileService(out var profile))
    return profile.GetProfileSummarySync();
```

`TryGetService` returns `false` (no throw, no subscribe) when the entity isn't subscribed yet or that client type hasn't been created — fall back to `GetServiceAsync` for the first call. The hot path is a lock plus two dictionary lookups and allocates nothing; combined with a synchronous `LocalQuery` read the whole access is zero-alloc (an `async UniTask`/`async` method that never suspends isn't boxed either).

### CrossOptimistic Mode

Optimistic execution across **multiple states owned by the same player** — the split-profile pattern. When one player's data has grown large enough to be split across several `ISharedState` entities (e.g. `ProfileState` + `InventoryState` + `QuestState`, all keyed by the player's id), `CrossOptimistic` lets the client execute methods that touch more than one of those states locally without waiting for a round-trip.

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

**Intended use:** split-profile mechanics. The cross-call target must be an entity that **the caller owns** — i.e. no other client and no server-side process writes to it independently of this caller. The framework relies on this invariant in two places:

1. **Broadcast suppression** — when `IsCrossOptimistic` is set on the outer call, the target's `HandleCallFromEntityAsync` excludes the originating caller from `DistributeBroadcasts` (the effect is already inlined in the outer call's replay payload; a duplicate broadcast would double-apply on the caller's client). Other subscribers of the target — typically none in the split-profile case — still receive the broadcast.
2. **Sequence-slot reservation** — `SessionManagerGrain` reserves the target entity's seq slot for the cross-call via a marker in `HeldBroadcasts`. If a concurrent third-party writer (server-side timer, background job, admin tool) increments the target between our last-known sequence and the cross-call's sequence, the marker waits in the gap so the intermediate broadcast(s) can drain through without being mis-classified as "old/duplicate". This guards the split-profile pattern even when a server-side scheduler also writes to one of the player's split states.

**Do not** use `CrossOptimistic` against entities that are concurrently mutated by other clients (clan-level state, lobby, market, two-player trade). Those need `Server` mode so the caller's client learns the target's state change through the normal broadcast path.

### Runtime Execution Mode Override

The execution mode defined in `[MetaMethod(Mode = ...)]` is the default. You can override it at runtime using `IExecutionModeProvider` — without recompilation or redeployment.

**Override a specific method** (0.23.0+ — keyed by `ushort MethodId` from the generator-emitted `GameMethodIds` const table):
```csharp
var modeProvider = client.ModeProvider as ExecutionModeProvider;

// Force "SetName" to always go through the server
modeProvider.SetMode(GameMethodIds.IProfileService_SetName_v0, ExecutionMode.Server);

// Note: ExecutionMode.LocalQuery (0.29.0+) is a structural trait of read-only methods,
// not a runtime routing override. The mode provider can swap Optimistic ↔ Server / ServerPatch
// but cannot retarget a write to LocalQuery (would lose the server confirm).
```

**Reset overrides:**
```csharp
modeProvider.Clear();  // Revert to [MetaMethod] defaults
```

**Priority order:**
1. Specific method override via `SetMode(GameMethodIds.X, mode)`
2. Attribute default (`[MetaMethod(Mode = ...)]`)

> **0.23.0 breaking change** — the previous `SetMode(string serviceName, string methodName, mode)` / `SetServiceMode(string, mode)` / `LoadManifest(json)` overloads were removed when the wire-level method addressing switched to `ushort MethodId`. To configure overrides from a JSON manifest in your own code, resolve `"IService.Method"` → `GameMethodIds.X` constant at config-load time (the constants are public `const ushort` fields, accessible via reflection on the `{RootNamespace}.Generated.GameMethodIds` type) and feed the result into `SetMode(ushort, mode)`.

**Use cases:**
- Force `Server` mode during tournaments for maximum authority
- Switch to `Local` mode for offline play or latency-sensitive UI actions
- A/B testing different execution strategies without code changes
- Debugging desyncs by switching suspected methods to `Server` mode

Generated API client code calls `_modeProvider.GetMode(GameMethodIds.X, defaultMode)` before every RPC to determine the execution path. The lookup is a single dictionary read on a `ushort` — no string allocation per call.

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

##### Auto-generated patch-tracking copy (0.24.2+)

You usually **don't** need to write `PatchState` by hand. When a service is **force-patch-able**, the generator emits a `{Impl}_PatchTracked` copy of the impl where `State` is rebound to the wrapper — so ordinary `State.X = …` writes (and `private TState s => State;` aliases) track transparently. The server dispatcher routes to the copy whenever patch tracking is active (`MetaContext.PatchWrapper != null`): force-patch, `ServerPatch` mode, or deep desync.

A service is force-patch-able when it declares a client-callable `Optimistic` / `Server` / `CrossOptimistic` method with `Version > MinCompatibleVersion`, or its bound config carries `[MetaConfigStructureBoundary]`, or any `ServerPatch` method. (All those modes run a divergent local/replay body on the client; `Query` / `Signal` / `Notification` / `Local` / `ServerReplace` don't, so a config boundary alone doesn't make an all-server service force-patch-able.)

- **Per-state, including siblings.** The copy is emitted for *every* service on a force-patch-able state, because a force-patched call fans out across sibling services on the same state (e.g. `ProfileService.BuyEnergy` → `EnergyService.AddPurchasedEnergy` via `GetIEnergyServiceSiblingAsync`). `ResolveSiblingByType` hands out the sibling's copy under patch tracking, so the sibling's mutations to the shared state land in the diff instead of bypassing the wrapper.
- **Opt out** with `[MetaService(PatchTracking = false)]` when a body can't be expressed in the copy-compatible style (see the wrapper-typed-helper rule under *Deep Desync*). Opting out generates no copy and **rejects** force-patch clients at negotiation (method-level → `Rejected`; config-boundary → subscribe rejected with a `FeatureRequirement`) rather than shipping an empty patch. Opt-out does not suppress the copy when a *sibling* on the same state forces tracking — the copy is still needed to track that service; opt-out only governs the per-method negotiation verdict.
- **Body must be copy-compatible:** mutate via `State` (or a `State` alias), avoid leaking wrapper collections into raw `List<>` fields, and type helper returns as `{State}PatchWrapper.{Dto}PatchWrapper` (see *Deep Desync → compile-time tracking guard*). Incompatible bodies fail to compile on the generated `_PatchTracked.g.cs` — a visible error, never a silent empty patch.

**Property types:**
- **Value types** (int, bool, enum, string): get/set with automatic tracking
- **Nested state objects** (types with `[Id]` properties): sub-wrappers with recursive tracking. Both get and set are supported — `state.Profile = new Profile { ... }` assigns via the implicit operator; assign-then-mutate in the same call works (`state.Profile = new Profile { Level = 1 }; state.Profile.Level += 5`)
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
    [MetaMethod(Mode = ExecutionMode.Query)]
    Task<PlayerBriefInfo> GetBriefInfo();

    [MetaMethod(Mode = ExecutionMode.Query, OpenAccess = true)]  // bypasses EntityAccessPolicy
    Task<PlayerBriefInfo> GetPublicInfo();

    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    void SetName(string name);
}
```

- `Mode = ExecutionMode.Query` — method can be called without subscribing, strictly read-only
- `OpenAccess = true` — skip EntityAccessPolicy check (for public data readable by anyone)
- Query methods **must** return a value (void not allowed)
- Query methods are **not** generated in the regular `ApiClient` — they appear in the separate `QueryApi`
- **Note:** the legacy `[MetaMethod(Query = true)]` bool flag is still accepted but deprecated (`CS0618`); migrate to `Mode = ExecutionMode.Query`

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

### Signal Methods (Fire-and-Forget)

Sibling to Query. Like Query, runs read-only on the server without sequence/broadcast/replay machinery. Unlike Query, it returns **void** and the client does **not** wait for a result — neither a successful response nor a failure reason ever lands back on the client.

**Use cases:** heartbeat, telemetry pings, notification events that trigger a server-side side-effect (HTTP, matchmaker, push gateway) without mutating entity state.

```csharp
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Signal)]
    void NotifyHeartbeat(long clientTicks);

    [MetaMethod(Mode = ExecutionMode.Signal)]
    void RecordTelemetry(string eventName, string payload);
}
```

Generated client API is synchronous `void`:

```csharp
api.NotifyHeartbeatSignal(DateTime.UtcNow.Ticks);   // returns immediately
api.RecordTelemetrySignal("purchase", jsonBlob);     // same
```

**Contract:**
- Return type must be `void` (compile-time `#error` otherwise)
- Cannot combine with `Query`, explicit `Mode`, `Sync`, `SkipServerOnFalse`, `ForcePersist`
- Method body must not mutate state — same rule as Query (enforcement is runtime contract today; a compile-time walker is planned)
- Cross-entity calls (via generated `GetI{Service}(entityId)`) throw `NotSupportedException` from inside a signal body — use `Mode = Server` if you need to chain into another entity
- `[ServerMetaService]` bridges **can** be called — the `ServerMetaContext` is flipped to `SignalMode`, which redirects Recorder writes to `NullPayloadWriter` (zero-alloc). Real side-effects (HTTP, Orleans grain hops) run; recording is silently discarded since there is no replay payload consumer

**Server routing:** `Handler.SignalCallAsync` → `SessionManager.SignalEntityAsync` → `[OneWay] EntityGrain.HandleSignalAsync` → `MetaProviderBase.HandleSignalAsync` → generated `{Service}SignalDispatcher.Dispatch`. Orleans treats the grain invocation as one-way — the SessionManager grain does not wait for an ACK from the entity grain.

**Access control:** Standard `EntityAccessPolicy` check is enforced (`UserOwned` / `OwnerOnly` / `Authorized` / `Open`). There is **no** `OpenAccess`-equivalent escape hatch — if you need public signals, use the Query path instead.

**Transport specifics:**
| Transport | Signal mechanism |
|-----------|------------------|
| InProcess | Direct grain invocation, no serialization beyond normal message handling |
| SignalR | `HubConnection.SendAsync(nameof(SignalCall), request)` — not `InvokeAsync`; no wire-level ACK |
| HttpPolling | `POST /meta-http/signal` → server returns `202 Accepted` **before** the signal body executes; handler runs in the background task scheduler |

**Error handling:** server-side exceptions are caught in `MetaProviderBase.HandleSignalAsync` and logged via `Logger.ProviderCallError`. They never reach the client. If you need confirmation of delivery, don't use Signal — use a regular method or a Query.

### Calling a Service from Server Code — `{Service}ServerApi` — 0.35.0+

Server code that is not itself running inside a meta call — admin tooling, framework grains, background jobs — has no `MetaContext` and therefore no cross-entity accessor. `{Service}ServerApi` is the entry point for that case, generated for every `[MetaService]` that declares a `StateType`:

```csharp
await grainFactory.GetServerApi<IProfileService>(playerId).AddResourcesAsync("gold", 500);
```

It routes through `IEntityGrainBase.HandleCallFromEntityAsync`, the same server-internal entry cross-entity calls use, so the call is an ordinary dispatch — replay recording, broadcast to every subscriber, persistence and sequence advance all happen, and a connected player sees the effect immediately. The entity needs no active subscriber: a cold entity activates, runs its migration ladder and persists.

`Mode = Notification` methods route to the `[OneWay]` grain entry instead; the returned Task completes on dispatch, not on the target's completion. Errors surface as `InvalidOperationException` rather than being swallowed — a server-originated call has no client response channel to carry them.

**Declaring an admin method.** Nothing special is required — it is an ordinary meta method that clients cannot call:

```csharp
[MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned)]
public interface IProfileService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
    void AddResources(string resource, int amount);
}
```

The server API deliberately includes `GenerateClientApi = false` methods — that combination is the point: a surface reachable only from inside the silo. Authorization is the caller's responsibility; there is no per-method access policy on this path, because reaching the class already means running as the server.

**Where it is emitted.** Shared projects get it behind `#if SHAREDMETA_SERVER` (so Unity and client builds, which have no Orleans, skip it). Server projects generate their own copy from referenced assemblies — without that pass the API would exist in no compiled assembly, since shared projects do not define the symbol.

**Hosting the admin surface.** For a web admin panel, put a thin ASP.NET endpoint in the silo host and call the server API from there; authorization uses the usual ASP.NET mechanisms. An external .NET tool can also connect as an Orleans client and call it directly, at the cost of referencing the game's shared assembly and `SharedMeta.Server.Core`.

### Inheriting a Service Contract — `[MetaMethod]` on the Implementation — 0.35.0+

Method ids and the client's broadcast-replay switch are built per compilation from method **declarations**. A `[MetaService]` interface that only inherits its contract from a referenced assembly has no declarations of its own, so it would generate nothing at all. Declare the contract's `[MetaMethod]` on the implementing class instead:

```csharp
// referenced package
public interface ILobbyRequester
{
    void OnMatchFound(MatchFoundEvent evt);
}

// game
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileLobbyService : IMetaService, ILobbyRequester { }   // empty

[MetaServiceImpl(typeof(IProfileLobbyService), typeof(ProfileState))]
public partial class ProfileLobbyService : IProfileLobbyService
{
    [MetaMethod(Mode = ExecutionMode.Notification)]
    public void OnMatchFound(MatchFoundEvent evt) { State.MatchId = evt.MatchId; }
}
```

The method then gets an id, a dispatcher case and a client replay case exactly like an interface-declared one. The base interface still supplies the signature guarantee — the class must implement the inherited member, so an argument-shape drift is a compile error rather than a silent protocol break. Generated code calls through the interface-typed variable, and the compiler resolves the inherited member.

### Notification Methods (Entity → Entity Fire-and-Forget) — 0.22.0+

Peer of Signal on the cross-entity axis. **Signal** is "client → entity, no wait"; **Notification** is "entity → entity, no wait". Use when one entity needs to inform another about a state change without blocking on the round-trip.

```csharp
[MetaService(StateType = typeof(ClanState))]
public interface IClanService : IMetaService
{
    // ProfileService.GainPoints fires this and continues without awaiting.
    [MetaMethod(Mode = ExecutionMode.Notification)]
    Task AddPower(int delta);
}

[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState), typeof(IClanService))]
public partial class ProfileService : IProfileService
{
    public Task GainPoints(int amount)
    {
        S.Score += amount;
        if (!string.IsNullOrEmpty(S.ClanId))
            GetIClanService(S.ClanId).AddPower(amount);  // void call — no await
        return Task.CompletedTask;
    }
}
```

**What changes vs `Mode = Server` cross-entity call:**
| | `Server` cross-entity (await) | `Notification` |
|---|---|---|
| Caller waits for target | Yes | No |
| Result observable to caller | Yes | No (target's return is discarded) |
| Recorded in caller's replay payload | Yes | No (client replay skips this call entirely) |
| Target broadcasts to its subscribers | Yes | Yes (independent of caller) |
| Errors in target | Propagate to caller as `InvalidOperationException` | Logged on the target only — never reach the caller |
| Caller can read target state after this call | Yes | **No** — there's no observable order |

**Contract:**
- Return type must be `Task` or `void` — no `Task<T>` (there is no return value)
- `GenerateClientApi = false` is implicit — clients never originate Notifications. Enforced on both sides since 0.35.0: no client API is generated, the dispatcher rejects a client-originated call, and negotiation marks the method `Rejected` while still mapping server→client so broadcasts replay normally. Before 0.35.0 only the client-side suppression happened, so a forged packet carrying the method id reached the body.
- Cannot combine with `Sync`, `SkipServerOnFalse`, `ForcePersist`
- Cannot be overridden at runtime — structural trait
- Generator emits cross-entity caller as `void {Method}(args)` (not `Task {Method}Async(args)`) — any pre-0.22.0 `await GetIFoo(id).BarAsync(...)` call site is forced to compile-error and migrate

**Server routing:** caller-grain dispatches via `IEntityGrain.HandleCallFromEntityOneWayAsync` (marked Orleans `[OneWay]`). Source grain returns immediately; target grain processes the call body normally (state mutates, broadcasts to its own subscribers, persists if `ForcePersist` was set on the impl method) but the `EntityCallResult` is discarded. No `CrossEntityCallInfo` is recorded into the caller's replay payload, so client-side replay reads nothing for this call site.

**When to use Notification:**
- Caller doesn't read the target's state after the call (clan-power delta from profile is a textbook fit: profile never reads clan state)
- Target's broadcasts independently reach its own subscribers (clan-state broadcasts to clan subscribers — caller doesn't need to act as the bridge)
- The caller-target sync round-trip dominates the latency budget (visible in p99 of high-volume cross-entity paths)

**When NOT to use Notification:**
- You need transactional consistency (e.g. "transfer money A→B" — must observe success of debit before commit)
- Caller reads target state immediately after the call
- A failure in the target must roll back the caller's mutation
- The result value drives caller logic

**Performance:** removes one grain-to-grain `await` from the latency path. In the ClanWars stress test (1000 + 1000 simulated players, 120s, single dev machine), flipping `AddPower` to `Notification` produced:
- Cold-path operations (`Connect`, `ResolveProfile`, `CreateClan`) — p99 dropped 8–21× (clan grain no longer holds the caller while activating)
- High-volume RPCs (`GainPoints`, `ApplyToClan`) — throughput +20% at the same p99 wall under CPU saturation

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

Fresh entity randoms (server, optimistic, `[NamedRandom]` slots) are seeded from a string passed to `MetaRandom.FromString` (FNV-1a hash). Default: `"{entityId}:{streamName}"`. **Once the random advances and the entity persists, the seed is never consulted again** — `EntityGrainState.{Server,Optimistic,NamedRandoms}Bytes` carry the full internal state (s0/s1/s2/s3 + ScrollId), and that's what the client receives via `SubscribeResponse`.

**The seed is server-side-only.** It is never serialized, never sent over the wire, and never reconstructed by the client. Clients reconstruct `MetaRandom` from the persisted bytes, so optimistic execution and replay see the same advanced state the server has — without knowing how the stream started.

**Override seeding to inject entropy.** When you recreate an entity with the same id (profile reset → recycled expedition counter, regenerated game grain) the deterministic seed string produces the same stream. Two ways to inject non-deterministic entropy:

```csharp
// (A) Per-host: option-driven, no provider subclassing.
services.Configure<EntityGrainOptions>(o =>
{
    o.FreshRandomSeedFactory = (entityId, streamName) =>
        $"{entityId}:{streamName}:{DateTime.UtcNow.Ticks:x}:{Random.Shared.NextInt64():x}";
});
```

```csharp
// (B) Per-provider: subclass MetaProviderBase and override directly.
protected override string CreateFreshRandomSeed(string streamName)
    => $"{Context.EntityId}:{streamName}:{DateTime.UtcNow.Ticks:x}";
```

The factory and override are invoked **only when no persisted bytes exist** for that stream — once a stream is in motion, the factory is irrelevant. `[NamedRandom(Seed = "literal")]` continues to bypass both paths (the attribute exists specifically to pin a stream to a fixed seed across all entities).

### Named Random Streams

`Context.Random` is a single shared stream. When different game mechanics (combat, loot drops, map generation) share it, advancing one advances the stream seen by the others — which makes changes to one system subtly alter outputs in unrelated ones. Declare independent streams on the state with `[NamedRandom]`:

```csharp
[SharedState]
[NamedRandom("Combat")]
[NamedRandom("Loot")]
[NamedRandom("MapGen", Seed = "map-v2")]  // explicit seed literal — fixed across entities
public partial class GameState : ISharedState { ... }
```

Each attribute generates a typed accessor on the service `Context` partial:

```csharp
int dmg  = CombatRandom.Next(100);      // independent from Loot and MapGen
int item = LootRandom.Next(drops.Count);
float h  = MapGenRandom.NextFloat();
```

Semantics mirror `Context.Random` — identical algorithm and seed on server and client, so Optimistic/Local methods see the same values both sides. Server-only (`ServerRandom`-style) scope is not yet supported.

**Seed derivation:** default is `entityId + ":" + Name` (FNV-1a). Pass `Seed = "literal"` on the attribute to pin the stream to a fixed seed regardless of entity.

**Persistence:** all named randoms pack into one positional `byte[]` blob (`EntityGrainState.NamedRandomsBytes`, Id 7). Order = attribute declaration order on the state class. Reordering / adding / removing attributes is a code change that reseeds the affected slots from the derived seed — documented and acceptable because this is random state (no meaningful "previous value" to preserve).

**Desync detection:** server emits a per-index `long[] NamedRandomScrollDeltas` on `RpcResponse` and `EntityBroadcast` (null when nothing advanced). Client compares its local deltas and fires `IDesyncDiagnostics.OnRandomDesync` with method name suffixed `[NamedRandom:{i}]`. On `ServerPatch` / `ServerReplace` / broadcast catch-up, the client calls `Skip(delta)` per-index to stay in sync.

**Transport:** `SubscribeResponse`, `ConnectResponse`, `ResubscribedEntityInfo` carry `NamedRandomsBytes` on initial snapshot and re-subscribe after transport reconnect.

**When not to use:** if you only have one logical random stream, `Context.Random` is simpler — no attributes, no per-stream naming. Named streams earn their keep when mechanics must be decoupled.

---

## 6. Cross-Entity Calls

### How It Works

1. Declare the target service as a dependency in `[MetaServiceImpl]`. The source generator
   injects a typed `GetI{Service}(entityId)` accessor into the consumer's partial class:

   ```csharp
   [MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), typeof(IProfileService))]
   public partial class ExpeditionService : IExpeditionService
   {
       // Generator injects: GetIProfileService(string entityId) into this partial class.

       public async Task<MoveResult> Move(int dx, int dy)
       {
           var profile = GetIProfileService(State.ProfileEntityId!);
           bool spent = await profile.SpendEnergyAsync(Config.MoveCost);
           // ...
       }
   }
   ```

   > **Do not use `Context.GetEntityApi<T>(id)`** — removed in 0.12.4. The typed
   > `GetI{Service}` method is the only supported entry point; the dependency entry in
   > `[MetaServiceImpl(..., typeof(IService))]` is what tells the generator to emit it
   > and to wire the server/replay/cross-optimistic routing.

2. On server: `MetaProviderBase.EntityCallHandler` resolves target grain, calls `HandleCallFromEntityAsync`

3. Target entity executes, broadcasts to ITS subscribers, returns result

4. `CrossEntityCallInfo` collected: `{ EntityId, EntitySequenceNumber, ResultBytes }`

5. `SessionManager` uses this to suppress duplicate broadcasts (advances `KnownEntitySequence` for target entity)

### Sibling-Service Calls (0.20.0)

A "sibling" is another `[MetaServiceImpl]` hosted on the same `TState` as the caller — both impls live in the same `EntityGrain`. 0.20.0 makes sibling calls cheap (typed in-process, no serialization, no grain RPC) while keeping the existing cross-entity API intact.

#### Implicit — `GetI{Service}(entityId)` self-detect

The existing cross-entity getter gains a runtime self-detect: when `entityId == Context.EntityId` and the requested service is hosted on this grain's `TState`, the accessor returns a generated `{Service}SiblingCaller` that wraps the cached sibling impl. Each method forwards directly to the impl with typed args — no `CallEntityAsync`, no payload bytes.

```csharp
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState),
    typeof(IInventoryService))]                 // declare InventoryService dep
public partial class ProfileService : IProfileService
{
    public async Task SendGift(string targetEntityId, int itemId)
    {
        // Works whether targetEntityId is self, a friend, or anyone else.
        // Self-id → sibling-bypass (no RPC). Different id → real cross-entity grain hop.
        await GetIInventoryService(targetEntityId).GrantItemAsync(itemId);
    }
}
```

This fixes the "gift-to-self deadlock": pre-0.20.0, calling `GetIInventoryService(self).GrantItem(...)` deadlocked because Orleans grain RPC into a non-reentrant grain stalls awaiting itself.

#### Explicit — `Get{Iface}SiblingAsync()`

For each entity-service dep whose `[MetaService(StateType=...)]` matches this impl's `TState`, the generator emits an async sibling accessor returning the **original** interface:

```csharp
public async Task ApplyDailyBonus()
{
    var inv = await GetIInventoryServiceSiblingAsync();
    inv.GrantItem("daily_bonus", 1);  // sync method on original interface
    State.LastBonusUtc = Context.ServerTimeTicks;
}
```

The `await` resolves the callee's typed `Config` through its own `IMetaConfigProvider<TConfig>.GetConfigAsync` (server). Multi-config siblings (different `[MetaConfig]` types on the same state) each see their own typed config branch independently of the calling service's primary config.

Use the implicit `GetI{Iface}(entityId)` accessor when the target id is dynamic (potentially self); use `Get{Iface}SiblingAsync()` when the intent is "this entity's own sibling".

#### Multi-config siblings — different `[MetaConfig]` types on one entity

Until 0.20.0, all services on a single `TState` typically shared one config type — either all declared `DefaultConfig = true` (resolving to the project's default `[MetaConfig(Default = true)]` class) or all set the same explicit `ConfigType`. Reading any service's `Config` always cast `Context.Config` to that single shared type.

0.20.0 lets each service on the same state declare its **own** config type. The framework resolves each service's `Config` through its own `IMetaConfigProvider<TConfig>` and sets it on the sibling impl when invoked through `Get{Iface}SiblingAsync()`. This is useful when one entity has multiple independent config domains that release on different schedules — say, an inventory balance config maintained by economy designers vs. a quest text config owned by writers.

```csharp
// Two separate config classes — different release cadence, different owners
[MetaConfig(Default = true)]
public class ProfileConfig          // primary config for the state (DefaultConfig=true)
{
    public int StartingEnergy { get; set; } = 100;
}

[MetaConfig]                         // not Default — explicitly typed
public class InventoryBalanceConfig
{
    public Dictionary<string, int> ItemPrices { get; set; } = new();
    public int MaxStackSize { get; set; } = 99;
}

// Service #1 — uses ProfileConfig via DefaultConfig
[MetaService(StateType = typeof(ProfileState), DefaultConfig = true)]
public interface IProfileService : IMetaService { ... }

// Service #2 — same state, but explicitly typed to InventoryBalanceConfig
[MetaService(StateType = typeof(ProfileState), ConfigType = typeof(InventoryBalanceConfig))]
public interface IInventoryService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Server)]
    int Buy(string itemId, int qty);
}

[MetaServiceImpl(typeof(IInventoryService), typeof(ProfileState))]
public partial class InventoryService : IInventoryService
{
    public int Buy(string itemId, int qty)
    {
        // Generator emits typed Config getter for InventoryBalanceConfig — independent of
        // ProfileConfig that the sibling ProfileService uses.
        var price = Config.ItemPrices[itemId] * qty;
        State.Coins -= price;
        return price;
    }
}

// Both services on ProfileState, calling each other as siblings
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState),
    typeof(IInventoryService))]    // declare InventoryService dep
public partial class ProfileService : IProfileService
{
    public async Task<int> BuyDailyBundle()
    {
        var inv = await GetIInventoryServiceSiblingAsync();   // ← async resolves InventoryBalanceConfig
        var spent = inv.Buy("daily_bundle", 1);                // ← uses InventoryBalanceConfig.ItemPrices
        State.LastDailyBundleUtc = Context.ServerTimeTicks;    // ← reads ProfileConfig.StartingEnergy elsewhere
        return State.Coins;
    }
}
```

Both `IMetaConfigProvider<ProfileConfig>` and `IMetaConfigProvider<InventoryBalanceConfig>` must be registered with DI on the server. Each can have its own `[MetaConfigVersion]` rules, version stream, and download URL — they evolve independently.

**Limitations**:

- The primary config's `_configProvider` field is auto-emitted on the generated `MetaProvider`; secondary-config providers (anything other than the first service's config type) are wired only when referenced by `[MetaStateVersion]` migration conditions or by an explicit sibling-async call. **Direct dispatch** of a secondary-config service (i.e. a top-level RPC into `IInventoryService.Buy(...)` from the client) currently sets `Context.Config` to the primary type and would fail the cast in the secondary impl's `Config` getter. Workaround: route through the primary service via `Get{Iface}SiblingAsync()`. This is a documented 0.20.0 limitation; lifting it requires the generator to emit per-service `Config`-set in `Get{Name}()` plus per-method config dispatch in `HandleCallAsync` — planned for 0.20.x / 0.21.0.
- On the client, multi-config siblings work through server-only outer modes (`Server` / `ServerReplace` / `ServerPatch`) — the sibling-async getter's async config resolution depends on `IMetaConfigProvider<TConfig>` from `SharedMeta.Server.Core`, which isn't referenceable from shared client-side assemblies. The `#if SHAREDMETA_SERVER` guard in the generated body skips that resolution on client; for `Optimistic` / `CrossOptimistic` outer modes the client would need a parallel `IClientMetaConfigProvider<TConfig>` lookup wired into the sibling resolver — also future work.

#### What's preserved across the boundary

Sibling-bypass is a **typed in-process call** — semantically equivalent to invoking a private helper method on the impl. Specifically:

- **State** — same `Context.State` instance.
- **Random streams** — same `Context.Random` / `Context.ServerRandom` / named-randoms (advances are recorded once into the outer's replay payload).
- **Patch / change tracking** — same `Context.PatchWrapper` and `ChangeTracker`. `ServerPatch` / `ServerReplace` / `Optimistic` / `CrossOptimistic` modes ship one patch per outer call regardless of how many siblings were invoked.
- **By-reference args** — typed C# call, no serialization. Mutations the sibling makes to passed-in objects are visible to the caller.

#### What's not preserved (by design)

- **`[Transformer]` Box/Unbox** — transformers run on serialization boundaries (`WriteWithAutoBox` / `ReadWithAutoUnbox`). Sibling-bypass skips both, so transformers don't fire. If your method depends on a transformer, the dep can't be a sibling — has to be a real cross-entity dep (different `TState`).
- **Implicit rollback on exception** — sibling-bypass shares the outer's mutation pipeline. If a sibling throws after a partial mutation, the partial state IS observable to the outer. User code that needs rollback should snapshot state before the sibling call and restore on catch.
- **Per-service `Config` on direct-dispatch of secondary-config services** — only via the explicit `Get{Iface}SiblingAsync()` path. Direct dispatch of a service whose `ConfigType` differs from the state's primary config falls back to `Context.Config` (the primary's type) and crashes on the cast — so secondary-config services should always be invoked via the sibling-async accessor on the primary service.

#### Runtime safety-net

For dynamic paths that bypass the typed accessor (e.g. raw `Context.CallEntityAsync(...)` with a self-id), `EntityGrain.EntityCallHandler` also checks self-targeting and routes through `MetaProviderBase.HandleNestedCallAsync(...)` — runs `DispatchCall` as a sub-operation under the outer `MetaContext`. New `ServerMetaContext.PushNestedOperation` / `PopNestedOperation` helpers swap the inner replay-buffer and `CrossEntityCalls` list so the outer's recording state survives.

#### Required: dep `StateType`

Every entity-service dep declared in `[MetaServiceImpl(typeof(I), typeof(TState), typeof(IDep), ...)]` must carry `[MetaService(StateType = typeof(DepTState))]` on the dep interface. Without this, the framework can't decide whether sibling-bypass is type-safe and the generator emits `#error` in the consumer's compilation.

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

## 6.5. Server-Only Services (Bridges)

`[ServerMetaService]` marks an interface as a **bridge to the server-only world** —
non-deterministic sources (RNG, wall clock, external HTTP), Orleans grain-to-grain
calls (lobby, matchmaker, map allocator), or any side-effect that the client cannot
legitimately re-execute.

A bridge service is **not** an entity. It has no `[SharedState]`, no subscribers, no
access policy, no RPC dispatcher. Clients never talk to it over the wire. It only
ever runs on the server; the framework captures each call's return value into the
replay payload and the client **reads back** that value from the payload during replay
instead of executing anything. This is the same mechanism that backs `Context.ServerRandom`
(§5), but generalised to any interface you define.

### When to use it

Reach for `[ServerMetaService]` when the answer to a call is **authoritative on the
server** and cannot be reproduced deterministically on the client:

- Random sources with server-held seed (`IRandomService`)
- Orleans grain calls leaving the entity boundary (`ILobbyRequester`, `IMapManager`, `IMatchmakerRequester`)
- External HTTP / gRPC calls (payment gateway, leaderboard service, push-notification gateway)
- Any "give me an id / ticket / allocation" pattern where the source of truth lives outside the entity

Reach for `[MetaService]` instead when the logic **is** deterministic and tied to a
state the client can replay (the full client-side optimistic or patch replay path).

### `[ServerMetaService]` vs `[MetaService]`

| Aspect                    | `[MetaService]`                         | `[ServerMetaService]`                           |
|---------------------------|-----------------------------------------|-------------------------------------------------|
| Has `[SharedState]`?      | Yes (required)                          | No                                              |
| Entity subscribe?         | Yes (`AccessPolicy`, broadcasts)        | No                                              |
| Client-callable over wire?| Yes (generated `{Name}ApiClient`)       | No (no wire API is generated)                   |
| Impl class attribute      | `[MetaServiceImpl(iface, stateType)]`   | **No attribute** — plain class, DI-registered   |
| Client-side behaviour     | Deterministic re-execution / patch apply| **Replayer reads recorded return value**        |
| Server-side behaviour     | Dispatcher → impl                       | DI-resolved impl (optionally wrapped by Recorder)|
| Dispatcher generated      | `{Iface}Dispatcher.g.cs`                | None                                            |
| Recorder/Replayer generated| None                                   | `{Iface}Recorder.g.cs` + `{Iface}Replayer.g.cs` |
| Consumed from meta method | Declared as dependency in `[MetaServiceImpl(..., typeof(IT))]`; used as `GetIT(entityId)` | Declared as dependency in `[MetaServiceImpl]` |

### Pattern (teaching example — `IMapManager`)

Imagine `PlayerProfileService.JoinMap(...)` that must allocate a map slot on the
server side. Map allocation state lives in an Orleans grain (`IMapAllocatorGrain`),
not in a `[SharedState]` — the client doesn't need to replay allocation logic, it
only needs to know the resulting `mapId`.

**Shared layer — interface:**

```csharp
[ServerMetaService]
public interface IMapManager
{
    Task<string> RequestMap(MapRequest request);
    Task ReleaseMap(string mapId);
}
```

Task-returning methods are typical — bridge work is usually async (grain hops, HTTP).

**Server-only project — plain POCO implementation, NO `[MetaServiceImpl]`:**

```csharp
public class MapManager : IMapManager
{
    private readonly IGrainFactory _grainFactory;

    public MapManager(IGrainFactory grainFactory) => _grainFactory = grainFactory;

    public Task<string> RequestMap(MapRequest request)
        => _grainFactory.GetGrain<IMapAllocatorGrain>(0).AllocateAsync(request);

    public Task ReleaseMap(string mapId)
        => _grainFactory.GetGrain<IMapAllocatorGrain>(0).ReleaseAsync(mapId);
}
```

**Server DI registration:**

```csharp
// Program.cs
services.AddTransient<IMapManager, MapManager>();
// or AddSingleton if the impl is itself stateless
```

**Consumption from a meta service — declare as dependency:**

```csharp
[MetaService(StateType = typeof(ProfileState))]
public interface IPlayerProfileService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Server)]
    Task<string> JoinMap(JoinMapRequest request);
}

// IMapManager listed as a dependency — generator auto-injects it into Context
[MetaServiceImpl(typeof(IPlayerProfileService), typeof(ProfileState), typeof(IMapManager))]
public partial class PlayerProfileService : IPlayerProfileService
{
    public async Task<string> JoinMap(JoinMapRequest request)
    {
        var mapId = await Context.MapManager.RequestMap(new MapRequest { ... });
        State.CurrentMapId = mapId;     // state mutation goes into [SharedState]
        return mapId;
    }
}
```

The `Context.MapManager` getter is produced by the generated `*.Context.g.cs`
partial. On server it points at your real impl (wrapped by the generated `Recorder`);
on client it points at the generated `Replayer` that feeds the pre-recorded return
value back from the replay payload. The same `JoinMap` method body runs on both
sides with no `#if SERVER` branching.

### How Recorder / Replayer work (user's view)

For every method on a `[ServerMetaService]` interface the generator produces two wrappers:

- **`{Iface}Recorder`** — activated on the server for each `Mode = Server` meta-method
  invocation. It calls the real impl, serialises the return value with the active
  `IMetaSerializer`, and appends it to the call's replay payload. Methods returning
  `Task` (no result) write a zero-byte marker so the Replayer keeps its stream in order.
- **`{Iface}Replayer`** — activated on the client during replay. Each call reads the
  next slot from the replay payload, deserialises the return value, and hands it
  back. Parameters are **ignored** — the value was decided on the server.

Because replay is strictly positional, **call order on the server must match call
order on the client-side replay**. Normally this is automatic (the client re-runs
the same deterministic method body), but watch out for:

- Branching on `DateTime.Now` or other non-deterministic globals before a bridge call
- Client-side code that skips calls due to a local-only short-circuit
- Adding a new bridge call without a version bump in persisted replay payloads

`Context.ServerRandom` uses this exact plumbing — it's just a specialised
`[ServerMetaService]` with its own recorder/replayer optimised for numeric output.

### Anti-pattern (the one that bites LLM agents)

Do **not** combine `[ServerMetaService]` with `[MetaServiceImpl]`, and do **not**
give a bridge service its own `[SharedState]`:

```csharp
// ❌ BROKEN — this is not a valid combination
[ServerMetaService]
public interface IMapManager { ... }

[MetaServiceImpl(typeof(IMapManager), typeof(MapManagerState))]  // ← wrong
public class MapManager : IMapManager { ... }
```

This configuration is a category error: `[ServerMetaService]` declares the service
has no shared state and no wire dispatcher, while `[MetaServiceImpl(..., stateType)]`
declares it is a state-ful entity service. The source generator detects this exact
combination and emits a `#error` in the generated `ServerMetaConfiguration.g.cs`
naming the class — if you see that diagnostic, pick one of two fixes:

- **Keep it as a bridge** (the normal case): drop `[MetaServiceImpl]` from the class,
  make the impl a plain class, register it in server DI, and reference `IMapManager`
  as a dependency in the consumer's `[MetaServiceImpl(..., typeof(IMapManager))]`.
  Any persistent server state lives in Orleans grains.
- **Turn it into a real entity service**: drop `[ServerMetaService]` from the
  interface, replace it with `[MetaService(StateType = typeof(MapManagerState), AccessPolicy = …)]`,
  and accept that clients will be able to subscribe and call it directly (pick the
  access policy accordingly).

### Checklist — is `[ServerMetaService]` the right choice?

- [ ] The return value is authoritative on the server (random, grain state, external API)
- [ ] The client does not need to subscribe to this service or receive broadcasts from it
- [ ] There is no `[SharedState]` that represents the service's data on the client
- [ ] The impl class is a plain POCO registered in server DI
- [ ] All `Mode = Server` meta methods that call it can tolerate replaying the recorded return value on the client

If all five are true — `[ServerMetaService]` fits. If any fail, use `[MetaService]`.

---

## 6.6. Stateless Meta Services

`[StatelessMetaService]` marks a service with **no entity, no state** — resolution requires only
materializing a linked `[MetaConfig]`. Different from `[ServerMetaService]`: a stateless service
IS client-callable (two paths below); it's just never entity-bound. Use it for pricing/formula
services, item-registry lookups, or any business logic that only needs config, not per-player
state.

```csharp
[MetaConfig]
public class ShopConfig { public int BaseCost { get; set; } = 42; }

[StatelessMetaService(typeof(ShopConfig))]
public interface IPricingService
{
    int ComputeCost(int quantity);
}

// Impl gets ONLY a typed Config property — no Context, no Random, no ServerTimeTicks, no
// dependencies. Pure function of (Config, method args) by design.
[StatelessMetaServiceImpl(typeof(IPricingService))]
public partial class PricingService : IPricingService
{
    public int ComputeCost(int quantity) => Config.BaseCost * quantity;
}
```

### Path 1 — resolve from inside any `[MetaServiceImpl]` (sibling-like)

Declare it as a dependency and the generator emits `GetIPricingServiceAsync()`:

```csharp
[MetaServiceImpl(typeof(IShopService), typeof(ShopState), typeof(IPricingService))]
public partial class ShopServiceImpl : IShopService
{
    [MetaMethod(Mode = ExecutionMode.Server)]
    public async Task Buy(int itemId, int qty)
    {
        var pricing = await GetIPricingServiceAsync();  // resolves Config only, no entity involved
        int cost = pricing.ComputeCost(qty);
        // ...
    }
}
```

**Server-only** — same constraint as multi-config sibling resolution (`Get{Iface}SiblingAsync()`,
section 6): config materialization is async DI-backed and cannot run during client-side
synchronous replay. Only callable from methods running `Mode = Server` / `ServerReplace` /
`ServerPatch`; calling it from `Optimistic`/`CrossOptimistic` throws `NotSupportedException`.

### Path 2 — resolve directly from `MetaClient`, no entity subscribe

```csharp
IPricingService pricing = await client.GetIPricingServiceAsync();
int cost = pricing.ComputeCost(qty);
```

No entity means no pinned `MetaConfigVersion` to read — this path resolves the version via a
lightweight non-entity RPC (mirrors `GetConfigDownloadUrlAsync`), then materializes it through the
registered `IClientMetaConfigProvider<TConfig>` (same Static/Downloading/Composite providers as
everywhere else). Requires an explicit host opt-in:

```csharp
// ASP.NET DI — pairs with app.MapMetaConfigDownload() / AddMetaConfigPublicUrl
builder.Services.AddGeneratedStatelessConfigVersionSource();
```

Only SignalR and HttpPolling transports implement the version-resolve RPC (client + server).
Unity BestHttp variants and the Debug Mux/InProcess transports fall back to `IConnection`'s
default, which throws `NotSupportedException` rather than silently no-op'ing.

### Checklist — `[StatelessMetaService]`

- [ ] No `[SharedState]`, no entity, no subscribe
- [ ] Impl class is `partial`, carries `[StatelessMetaServiceImpl(typeof(IThing))]`
- [ ] Interface carries `[StatelessMetaService(typeof(TConfig))]` — ConfigType is required
- [ ] Every Path 1 caller of `GetI{Iface}Async()` runs under `Mode = Server/ServerReplace/ServerPatch`
- [ ] For Path 2, host called `services.AddGeneratedStatelessConfigVersionSource()` and registered an `IClientMetaConfigProvider<TConfig>` client-side

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

### Framework Contracts (Lobby / Matchmaking)

A framework contract like `ILobbyListener` is an ordinary interface — nothing about it is
special-cased by the dispatcher. Wire it up in two places:

```csharp
// 1. Inherit it on the service interface: dispatch and the generated APIs are typed on this.
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileService : IMetaService, ILobbyListener { }

// 2. Put [MetaMethod] on the implementation. The declarations live in the framework assembly
//    and carry no syntax in your compilation, so the attribute has to go here - that is what
//    assigns a method id, emits the dispatcher case and enables client-side replay.
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState))]
public partial class ProfileService : IProfileService
{
    [MetaMethod(Alias = "OnMatchFound", Mode = ExecutionMode.Server, GenerateClientApi = false)]
    public void OnMatchFound(MatchFoundEvent e)
    {
        State.IsSearching = false;
        State.CurrentGameId = e.MatchId;
    }
    // ... OnMatchCancelled, OnMatchmakingUpdate
}
```

`GenerateClientApi = false` keeps them server-originated: clients get no API, and the dispatcher
rejects a client packet carrying their id. `Server` rather than `Notification` so the caller can
await delivery and see a failure - `Notification` dispatches `[OneWay]` and has nothing to wait on.

### Calling a service through its contract

`ILobbyListener` carries `[MetaServiceContract]`, so the generator emits an awaitable mirror
**into the contract's own assembly**:

```csharp
public interface ILobbyListenerServerApi
{
    Task OnMatchFoundAsync(string entityId, MatchFoundEvent @event);
    // ...
}
```

That placement is the point: `LobbyGrain` lives in the framework and cannot name `IProfileService`,
but it can name the contract. The entity id is an argument rather than a binding, so one instance
serves every target — inject it and keep it:

```csharp
public LobbyGrain(ILogger<LobbyGrain> logger, ILobbyListenerServerApi? players = null) { ... }

await _players.OnMatchFoundAsync(entityId, evt);
```

The implementation is generated into the assembly of whichever service inherits the contract and
registered in DI. Declare it nullable: a host may register a framework component whose contract the
game does not use.

Delivery is a normal dispatch - state mutates, subscribers get a broadcast, the entity persists,
clients replay the method body - and it works on a cold entity, so an offline player still has the
result waiting when they next subscribe.

Declare your own contract the same way: put `[MetaServiceContract]` on an interface in an assembly
both sides can see, inherit it on a `[MetaService]`, and the mirror plus the binding are generated.

### Client Method Subscriptions

Subscribe to specific methods being replayed from broadcasts:

```csharp
var sub = resolver.OnMethodReplayed<MatchFoundEvent>(
    entityId, GameMethodIds.IProfileService_OnMatchFound_v0,
    e => Console.WriteLine($"Match found: {e.MatchId}")
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

Generated API clients fire `OnStateMutated` after every state mutation — local Optimistic / CrossOptimistic / Server / ServerPatch / ServerReplace execution, incoming broadcasts (including foreign-service ones — see *Multi-service-on-entity* below), subscriber events, and reconnect refresh. Sourced from `EntityStateContainer.OnMutated` since 0.14.0, so every API client subscribed to the same entity fires in lock-step. Use it as a general-purpose "state changed" signal when you don't need per-field granularity:

```csharp
var api = await client.GetServiceAsync<GameServiceApiClient>(entityId);
api.OnStateMutated += () => UpdateUI(api.State);
```

This fires alongside `Tracked{State}.OnChanged`. Order is preserved everywhere a setter actually runs: `Tracked` first, `OnStateMutated` after. The one exception is `ServerReplace` wholesale-replace at entity level — the container is replaced with a fresh deserialized instance (no setters called), so only `OnStateMutated` fires.

### MutationCount (0.13.1+, redesigned in 0.14.0)

Each generated API client exposes `int MutationCount` — a local counter bumped on every state-mutating operation. **Shared per entity since 0.14.0:** every API client subscribed to the same entity returns the same value, and the counter increments exactly once per mutation regardless of how many ApiClients are subscribed. Backed by `EntityStateContainer<TState>.MutationCount` on the resolver side.

Use it as a cheap polling signal — "did anything modify this entity since I last looked?":

```csharp
int lastSeen = api.MutationCount;
// ... game loop ...
if (api.MutationCount != lastSeen)
{
    lastSeen = api.MutationCount;
    InvalidateCache();
}
```

For polling without an ApiClient (e.g. UI views that only need to know "something changed"), use `MetaServiceResolver.GetStateContainer<TState>(entityId)` to access the same container directly, then poll `container.MutationCount` or subscribe to `container.OnMutated`.

Counter is local to the client process: not synchronized across clients, not persisted, not coordinated with the network sequence number.

### Multi-service-on-entity (0.14.0+) — shared state container

Multiple `[MetaService]` interfaces can target the same `ISharedState` (e.g. `IInventoryService` + `IShopService` both annotated `[MetaService(StateType = typeof(PlayerState))]`). Since 0.14.0:

- **One container per entity, regardless of how many services live on it.** `MetaServiceResolver` creates a single `EntityStateContainer<TState>` when the entity is first subscribed and hands the same container to every API client created against that entity. `apiInventory.State` and `apiShop.State` always return the same instance, even after `ServerReplace` swaps the wholesale state object.
- **Foreign-service broadcasts update local state — for ALL execution modes.** When the server broadcasts a mutation from `ISocialService.SendGift`, a client that holds only `IProfileServiceApiClient` still receives the broadcast and applies it locally. The entity-level handler chooses one of three paths automatically based on what the broadcast carries:
  - **`ServerReplace` (`StateBytes`)** — handler deserializes a fresh state object and replaces the container's reference via `IEntityStateContainer.ReplaceObject`. Wholesale swap; no setter calls happen on the old instance, so only `OnStateMutated` fires (`Tracked{State}.OnChanged` has nothing to fire).
  - **`ServerPatch` (`PatchBytes`)** — handler activates `ChangeTracker`, calls `MetaServiceConfig.PatchApplier` (which mutates the existing state through `[Tracked]` field setters), then `tracker.FlushAndNotify()`. `Tracked{State}.OnChanged` fires for every touched field, then `OnStateMutated` fires.
  - **`Optimistic` / `Server` / `CrossOptimistic` (no state-data)** — handler invokes `MetaServiceConfig.EntityReplayDispatcher`. The dispatcher spins up the foreign service's impl class on the fly, sets up `ClientMetaContext` with the replay context, activates `ChangeTracker`, runs the method against the shared state, and `FlushAndNotify`s. `Tracked` events then `OnStateMutated`, same order as a per-method ApiClient call.

  In all three cases `MutationCount` bumps and `OnStateMutated` fires on every API client subscribed to the entity, regardless of which (or how many) services they hold.

```csharp
// Two services on the same state.
[MetaService(StateType = typeof(PlayerState))]
public interface IProfileService : IMetaService { ... }

[MetaService(StateType = typeof(PlayerState))]
public interface ISocialService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Server)]
    void ReceiveGift(string fromPlayerId, int amount);
}

// Client side — only IProfileService is subscribed.
var profile = await client.GetServiceAsync<ProfileServiceApiClient>(playerId);
profile.OnStateMutated += () => RefreshProfileView(profile.State);

// Another player calls ISocialService.ReceiveGift on this player's profile entity
// (cross-entity from sender). Server broadcasts to the receiver. Receiver's
// MetaServiceResolver finds ISocialService in its config registry, instantiates
// SocialService impl, replays ReceiveGift against the shared container.
// State.Money increments, [Tracked] subscribers fire, OnStateMutated fires.
```

**What's required for foreign-service replay to work:**
- The foreign service's impl class is referenced from the client assembly (lives in shared code) and has a parameterless constructor.
- The foreign service is registered in the resolver via `RegisterAllServices()` so `MetaServiceConfig.EntityReplayDispatcher` is reachable. (The generator emits this for every `[MetaService]` in the assembly, so the typical `client.Resolver.RegisterAllServices()` call covers it.)
- Methods that make cross-entity calls (`Context.GetI{Service}(otherEntityId)`) inside the foreign service body still need the matching service subscribed locally — entity-level dispatch can't recursively chase cross-entity records. For pure local-state mutations the entity dispatcher is enough.

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

### Discovery

No registration call. The generator finds `[Transformer]` classes in the compilation and applies
one to every meta-method parameter whose type it boxes — client, server dispatcher, query proxy,
broadcast replay and server-originated calls all derive the decision from the same compilation, so
both ends of every wire agree by construction.

For a transformer to be discovered it must be visible to the assembly declaring the `[MetaService]`
interface, be a non-generic class with a public parameterless constructor, and not carry
`[Transformer(NoAutoRegister = true)]` or `[Transformer(UseResolver = true)]`. If two discoverable
transformers claim the same complex type, the first by ordinal type name wins — name the one you
mean with `[Transform]` instead of relying on that.

### Usage in Methods

A parameter whose type has a discovered transformer is boxed and unboxed automatically. Override
per parameter:

```csharp
[MetaMethod]
void Move([Transform(typeof(Vector3Transformer))] Vector3 position);

[MetaMethod]
void RawMove([SkipTransform] Vector3 position); // No transformation
```

### What crosses the wire

A transformed argument travels as its boxed type and occupies exactly one wire member, same as any
other argument — the framing is unchanged, so transformers cost nothing in the MemoryPack fast path.

Before the client runs the method locally, it replaces each transformed argument with
`Unbox(Box(arg))`. The server only ever receives the boxed form, so this is what makes both sides
execute against the same value; a transformer that is not a perfect identity would otherwise desync
on its first call. Consequence: your method body sees the *unboxed* object, not the instance the
caller passed. For a state-aware transformer that means the object resolved out of local state.

`LocalQuery` and sibling-bypass calls never serialize, so transformers do not run on them.

---

## 11. Transport Configuration

Transport is split into **server** and **client** packages. Server packages host the endpoints/hub only; client-only packages have no server dependencies (no Orleans, no ASP.NET FrameworkReference) and work with Godot, console apps, and other .NET clients.

| Package | Type | Dependencies |
|---------|------|-------------|
| `SharedMeta.Transport.SignalR` | Server only (`MetaHub`) | Orleans, Server.Core, ASP.NET |
| `SharedMeta.Transport.SignalR.Client` | Client only (`SignalRConnection`) | Core, SignalR.Client (JSON protocol by default) |
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

**Client (JSON protocol — default, no extra packages):** reference `CoreGame.SharedMeta.Transport.SignalR.Client` (.NET / Godot / console; Unity gets `SignalRConnection` from the UPM package).
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
    // Single-value: returns ROM<byte> (0.23.0+). Stock codec wraps a fresh byte[];
    // GrainScopedSerializer writes into per-grain scratch and returns a slice (ephemeral —
    // valid until the next Handle*Async entry resets the pool). Use .ToArray() at the
    // boundary where ownership matters (persistence, cross-grain hop without a pooled payload).
    ReadOnlyMemory<byte> Pack<T>(T value);
    void Pack<T>(T value, IBufferWriter<byte> writer);                    // zero-alloc writer overload
    T Unpack<T>(byte[] bytes);
    T Unpack<T>(ReadOnlyMemory<byte> bytes);

    ReadOnlyMemory<byte> Pack(Type type, object value);
    object Unpack(Type type, byte[] bytes);
    T Clone<T>(T value);

    IPayloadWriter CreateWriter();
    IPayloadReader CreateReader(byte[] bytes);
    IPayloadReader CreateReader(ReadOnlyMemory<byte> bytes);
    byte[] SerializeRpcCall(RpcCall call);
    RpcCall DeserializeRpcCall(byte[] bytes);
}
```

> **Wire-DTO shape (0.23.0+).** Hot-path packets carry payloads as `ReadOnlyMemory<byte>` instead of `byte[]?` — `MetaOperation`, `RpcCall`, `SessionOp`, `EntityBroadcast.OpBytes`, etc. MemoryPack wire-bytes are identical to the prior shape (`bin` length-prefix + bytes); MessagePack uses a custom formatter for ROM round-trip. JSON-based clients on Newtonsoft.Json (Unity HTTP polling) need `RomByteJsonConverter` registered in `JsonSettings.Converters` — System.Text.Json's built-in converter already uses base64 strings matching the wire. Persistence fields (`EntityGrainState.*Bytes`) stay `byte[]`.

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

**For server persistence through Orleans storage providers, also add `[GenerateSerializer]` + `[Id(n)]`:**
```csharp
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
public partial class MyState : ISharedState
{
    [Key(0), MemoryPackOrder(0), Id(0)] public string Name { get; set; }
    [Key(1), MemoryPackOrder(1), Id(1)] public int Value { get; set; }
}
```

Real Orleans storage providers (Azure Tables, Redis, ADO.NET, and the bundled `FileGrainStorage` in its default Orleans mode) drive persistence through the **Orleans serializer**, which requires `[GenerateSerializer]` + `[Id(n)]` on every type in the persisted graph — including your `ISharedState` and nested DTOs. The transport serializer (`IMetaSerializer` — MemoryPack/MessagePack) still owns wire payloads and replay, so the MemoryPack/MessagePack attributes are not redundant. Unity-side compilation works thanks to `Orleans.Stubs` (no-op attributes shipped with the UPM package).

If you opt `FileGrainStorage` into MemoryPack/MessagePack mode (`UseOrleansSerializer = false`), the Orleans attributes are not strictly required — but adding them costs nothing and lets you switch storage providers later without touching state classes.

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
- Active session (`SessionId`, `SequenceNumber`)
- Broadcast ordering (per-entity sequence tracking)
- Missed packet buffer for reconnection (`PendingPackets`, max 1000 packets)
- RPC response batching with broadcasts
- Persisted RPC-ordering baseline (`LastDispatchedRequestId`) — survives silo restart

Subscriptions are **client-owned** since 0.24.0 — the server no longer persists a parallel subscription list. The client claims its set on every `Resume`, the entity grain verifies each claim. The grain's own `Subscribers` dict is a soft cache rebuilt from claims, not the authoritative store.

### Session Lifecycle

Two explicit connect modes from 0.24.0 — `SessionConnectMode.Resume` (rehydrate the existing session) vs `SessionConnectMode.StartNew` (allocate a fresh session, supersede any existing). No silent fallback between them.

```
1. New connection (or explicit StartNew)
   Client → SessionConnect(playerId, Mode=StartNew)
   Server → Fresh SessionId, empty subscription set, supersedes any prior session

2. Resume (same SessionId)
   Client → SessionConnect(playerId, Mode=Resume, sessionId, lastAcknowledgedSequence,
                           lastCompletedRequestId, ClaimedSubscriptions=[...])
   Server → Returns missed packets (sequence > lastAcknowledged)
          → Per-claim verdict for every entity the client says it was subscribed to (see below)
          → Mismatch on SessionId ⇒ SessionUnknown failure (no silent re-start)

3. Session supersede (a fresh StartNew from the same player)
   New Client → SessionConnect(playerId, Mode=StartNew)
   Server → Notifies old observer: "Session superseded"
          → Unsubscribes old session from all entities
   Old Client → OnSessionSuperseded fires
```

### Client-Owned Subscriptions and Reclaim Verdicts (0.24.0+)

The client tracks its own subscription set (entity id + state type + last known entity sequence). On every `Resume`, that set ships in `SessionConnectRequest.ClaimedSubscriptions`. The server forwards each claim to the entity grain (`EntityGrain.ReclaimSubscriptionAsync`) and returns a per-claim verdict in `SessionConnectResponse.Subscriptions`:

| Verdict | When | What the response carries |
|---|---|---|
| `Continued` | Sequence matches; signature hash unchanged. The grain may need to repair its `Subscribers` entry (after silo restart) — done in place, no state ship. | No state bytes; client keeps its local view. |
| `Refreshed` | Sequence gap, or signature hash changed (schema migration). | Full snapshot — state bytes, optimistic + named-random scrolls, config version. Client deserializes and fires `StateRefresher` hooks. |
| `Failed` | Access denied, schema incompatible, unknown state type, or grain exception. | Failure reason. Any single `Failed` aborts the whole Resume with `SessionConnectFailureReason.SubscriptionReclaimFailed` — the client routes through `IMetaSessionRecoveryHandler`. |

**Truth source for Continued is `EntitySequenceNumber` alone.** If the server's `state.Subscribers` lost the player's entry (e.g. silo crash without graceful deactivate) but the sequence number still matches, the cheap path repairs the entry in place — it does **not** force-push a Refreshed snapshot. Without this guarantee, a client that applied offline RPCs during a server outage would have its locally-applied state overwritten by the pre-outage snapshot on reconnect.

**Client-side cross-entity tracking.** `_lastKnownEntitySeq` advances on every `CrossEntityOperations[i].TargetEntityId` in the response, not just the outer entity. Without this, a cross-entity call from entity A into entity B leaves B's client-side seq stale; the next Reclaim of B would report a gap and the server would force-push a Refreshed snapshot.

### `IMetaSessionRecoveryHandler` — Game-Level Recovery Callback (0.24.0+)

When the server reports `SessionUnknown` (Resume against a session the grain no longer remembers) or `SubscriptionReclaimFailed` (any subscription verdict was `Failed`), the client invokes a game-level callback so the game decides what to do:

```csharp
public interface IMetaSessionRecoveryHandler
{
    Task<SessionRecoveryAction> OnSessionLost(SessionLostContext ctx, CancellationToken ct);
}

public enum SessionRecoveryAction { Reconnect, Restart, Disconnect }
```

| Action | Behavior |
|---|---|
| `Reconnect` (default) | Dispatcher runs a fresh `StartNew` and re-subscribes to known entities. Pending RPCs from the lost session fail with `SessionLostException`. |
| `Restart` | Full client restart — typical when the game wants to drop and reload local UI state. |
| `Disconnect` | No automatic recovery — the game decides (e.g. show a logout prompt). |

Wire it through `MetaClientOptions`:

```csharp
var client = new MetaClient(connection, serializer, new MetaClientOptions
{
    SessionRecoveryHandler = new MyRecoveryUi(),  // or omit for default Reconnect
});
```

A single `TriggerRecovery` entry point handles both transport-reconnect (SignalR `Reconnected`) and server-pushed `RequireSessionReconnect` notifications. `Interlocked` debounce prevents concurrent `ConnectSessionAsync` calls racing inside SignalR's connection lock.

### RPC Ordering Baseline Persistence (0.24.0+)

After silo restart, the grain rehydrates the persisted `RpcOrderingBuffer.LastDispatchedRequestId` from `SessionManagerGrainState`. Without this baseline, the buffer would reset to 0 and the client's re-sent RequestIds would be misclassified `OutOfOrder` against a stale baseline, stashed forever, producing infinite client retry loops.

The client additionally reports `SessionConnectRequest.LastCompletedRequestId` on Resume — the server advances its baseline to `max(persisted, clientReported)`, handling the case where the server lost cached responses (eviction or crash before persistence flush) but the client did receive them. Wire-additive; old clients send 0 (no-op).

Stale + cache-miss returns an error op keyed to the original `RequestId` instead of silent re-execution — at-most-once semantics for non-idempotent operations whose prior responses the client already ack'd.

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

### Server-Side RPC Reordering (0.8.0+)

Some transports do not preserve the order in which the client invoked RPCs by the time those calls reach the entity grain on the server. SignalR over a single hub connection is fine — it serializes calls at the wire level. **HTTP polling, custom UDP, anything with intermediate `Task.Run`** does not. Concurrent Optimistic calls then race on the threadpool and cause phantom patch desyncs ("the local Add(10) and Add(20) succeeded in order locally, but the server processed them as Add(20)→Add(10)").

`SessionManagerOptions.EnforceRpcOrder = true` opts in to a per-session reordering gate inside `SessionManagerGrain`. The gate uses a fixed-capacity ring buffer (`RpcOrderingBuffer<T>`) keyed by monotonic `RequestId` (already supplied by `ClientDispatcher`):

```csharp
services.Configure<SessionManagerOptions>(o =>
{
    o.EnforceRpcOrder = true;            // default: false
    o.StashCapacity = 256;               // bounded buffer
    o.SoftStallNotifyTimeout = TimeSpan.FromMilliseconds(500);
    o.HardStallNotifyTimeout = TimeSpan.FromSeconds(10);
    o.MaxStallDuration = TimeSpan.FromMinutes(5);
    o.StallTickInterval = TimeSpan.FromSeconds(1);
    o.DuplicateStashLogLevel = StashDuplicateLogLevel.Debug;
});
```

Flow:
1. Each incoming `SendToEntityAsync` is classified as `Stale` (already dispatched) / `NextExpected` (process inline) / `OutOfOrder` (park in stash).
2. Out-of-order calls return an empty ack response immediately — TCS on the client stays pending.
3. When the missing predecessor arrives, the gate processes it inline, then drains every consecutive stash entry in the same grain method invocation.
4. All results — the in-line call + drained stash — are bundled into **one** `SessionResponse` with one sequence number. The client's existing `RequestId` matching dispatches them to the right pending TCS.

Client side requires no changes. The grain stays single-threaded; ordering is restored before any state mutation runs.

### Stall Notifications and `ISessionHealthListener` (0.8.0+)

When an ordering gap stays open beyond `SoftStallNotifyTimeout`, the server pushes a `StallNotification` to the client through the existing observer channel as a new `SessionResponse.StallNotification` field (with empty `Operations`). Stages:

| Stage | When | Typical UI |
|-------|------|-----------|
| `StallStage.Stalled` | gap open ≥ `SoftStallNotifyTimeout` (default 500 ms) | low-key "syncing…" indicator |
| `StallStage.TimeoutPending` | gap open ≥ `HardStallNotifyTimeout` (default 10 s) | "Connection issue. Wait or reconnect?" prompt |
| `StallStage.Recovered` | gap closed | hide stall UI |

Client wires it through `MetaClientOptions.SessionHealth`:

```csharp
public interface ISessionHealthListener
{
    void OnSessionStalled(StallNotification notification);    // Stalled or TimeoutPending
    void OnSessionRecovered(StallNotification notification);
}

new MetaClient(connection, serializer, new MetaClientOptions
{
    SessionHealth = new MyStallUiListener(),
});
```

`ClientDispatcher.ProcessServerResponse` short-circuits stall-only batches directly to the listener — they don't touch the broadcast buffer or pending requests.

After `MaxStallDuration` (default 5 minutes) the server **terminates the session** via `ISessionObserver.OnSessionTerminated` with a `"RPC ordering stall exceeded …"` reason. Stash overflow (more than `StashCapacity` simultaneously parked requests) terminates immediately. In both cases the client must reconnect — the assumption is that the missing predecessor was lost permanently and continuing risks state desync.

> **Note (0.10.0+):** Server-side stall notifications are now **lazy** — pushed on the next incoming request rather than via periodic grain timers. This eliminates timer overhead for HTTP polling games where brief out-of-order arrival is normal. Client-side auto-retry is the primary recovery mechanism.

### Client-Side Connection Health Monitoring (0.10.0+)

`IConnectionHealthListener` monitors pending RPC request age on the client side. Independent of server stall detection — works even when the server is completely unreachable.

```csharp
public interface IConnectionHealthListener
{
    void OnConnectionHealthChanged(ConnectionHealthStatus status, long oldestPendingMs, int pendingCount);
}
// Status: Healthy → Slow (spinner) → Unresponsive (modal dialog)
```

Configuration via `ConnectionHealthOptions`:

| Option | Default | Description |
|--------|---------|-------------|
| `SoftTimeoutMs` | 1000 | Show spinner |
| `HardTimeoutMs` | 5000 | Show "connection issue" dialog |
| `RetryIntervalMs` | 2000 | Auto-resend pending requests every N ms |

Wire it through `MetaClientOptions`:

```csharp
var client = new MetaClient(connection, serializer, new MetaClientOptions
{
    ConnectionHealth = new MyConnectionHealthUI(),
    ConnectionHealthOptions = new ConnectionHealthOptions
    {
        SoftTimeoutMs = 1000,
        HardTimeoutMs = 5000,
        RetryIntervalMs = 2000,
    }
});
```

**Auto-retry** is the primary recovery mechanism for lost packets. When pending requests exceed `SoftTimeoutMs`, the client automatically resends all pending requests every `RetryIntervalMs`. This is fully client-side — no server dependency, works with any transport.

**Session resume:** When showing a "reconnect" dialog, call `MetaClient.ResumeSessionAsync()` to restore the current session (same `sessionId`, missed packet recovery) without restarting. Falls back to `RestartSessionAsync()` if resume fails.

### Debug Network Simulation (0.10.0+)

`DebugConnectionWrapper` wraps any `IConnection` to simulate network problems:

```csharp
var settings = new DebugConnectionSettings
{
    MinLatencyMs = 200,
    MaxLatencyMs = 500,
    PacketLossPercent = 10,
    LossMode = PacketLossMode.RequestHang, // for HTTP polling
    Enabled = true,
};
IConnection connection = new DebugConnectionWrapper(realConnection, settings);
```

| `PacketLossMode` | Behavior | Transport |
|-------------------|----------|-----------|
| `ConnectionDrop` | Full disconnect (`OnDisconnected`) | SignalR, WebSocket, TCP |
| `RequestHang` | Throw `HttpRequestException` per request | HTTP polling |

Debug methods:
- `SimulateDisconnect()` — permanent drop
- `SimulateTemporaryDisconnectAsync(3000)` — real disconnect→reconnect with 3s outage, server saves/restores subscriptions

Settings are mutable at runtime for live debug UI adjustment. See the Expedition Unity example for a complete debug panel implementation.

### Diagnostics Log (0.10.0+)

File-based request lifecycle tracing for debugging connection issues:

```csharp
if (client.Dispatcher is ClientDispatcher cd)
{
    var writer = new StreamWriter("connection_diag.log", append: false) { AutoFlush = true };
    cd.DiagnosticsLog = msg => writer.WriteLine(msg);
}
```

Output format:
```
[HH:mm:ss.fff] SEND reqId=5 IExpeditionService.Move entity=expedition-xxx
[HH:mm:ss.fff] TRANSPORT_ERROR reqId=5 HttpRequestException: ... (kept pending)
[HH:mm:ss.fff] AUTO_RETRY 3 pending, oldest=2005ms, ids=[5,6,7]
[HH:mm:ss.fff] RESEND_ALL count=3 ids=[5,6,7]
[HH:mm:ss.fff] CONFIRMED reqId=5 IExpeditionService.Move
```

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
    options.AccessTokenLifetime  = TimeSpan.FromMinutes(30); // short — paired with refresh (0.30.0+)
    options.RefreshTokenLifetime = TimeSpan.FromDays(30);    // absolute session expiry
});
app.MapMetaAuthEndpoints();
```

> `TokenLifetime` is an `[Obsolete]` alias of `AccessTokenLifetime` since 0.30.0 — its default dropped from 7 days to 30 minutes because access tokens are now short-lived and renewed via refresh (see **Refresh Tokens** below).

### Login Endpoint

```
POST /meta/auth/login
Body: { "deviceId": "unique-device-id" }
Response: { "token": "jwt...", "playerId": "abc123_20260226", "isNewPlayer": true,
            "expiresAt": "...", "refreshToken": "abc123_...family.secret", "refreshExpiresAt": "..." }
```

### Device-Based Auth Flow

1. `AuthGrain` (keyed by DeviceId) maps DeviceId → PlayerId
2. First login: generates PlayerId (`{random8hex}_{yyyyMMdd}`)
3. Subsequent logins: returns existing PlayerId
4. JWT token contains `sub` (PlayerId), `auth_type` ("device"), `jti` (unique ID)

### Refresh Tokens (0.30.0+)

Login (device or platform) now returns a long-lived **refresh token** alongside the short-lived access JWT. When the access token expires, exchange the refresh token for a new one instead of re-running the full login.

**Server model.** Active refresh sessions live in a per-player `IRefreshTokenGrain` (key = PlayerId). The token is `{playerId}.{familyId}.{secret}`; only a **SHA-256 hash** is stored, so a leak of grain state never yields a usable token. Each refresh **rotates** the secret — the presented token becomes invalid. Presenting an already-rotated token is treated as theft: the whole session **family is revoked** (reuse detection). Family expiry is absolute (rotation doesn't extend it). `reset-device` and `unlink` revoke that credential's sessions.

```
POST /meta/auth/refresh   Body: { "refreshToken": "..." }
  → 200 { token, playerId, expiresAt, refreshToken (rotated), refreshExpiresAt }   on success
  → 401                                                                            on invalid / expired / reuse
POST /meta/auth/logout    Body: { "refreshToken": "..." }   → revokes that session (idempotent)
```

**Client — at connect/reconnect.** `EnsureAuthenticatedAsync` does it for you: a still-valid access token is reused; an expired access token with a valid refresh token is silently refreshed (rotating); otherwise it falls back to a full login. The refresh token is persisted by `ITokenStorage` (`CachedToken.RefreshValid`).

```csharp
// Same call as before — refresh is automatic.
var login = await MetaAuth.EnsureAuthenticatedAsync(authUrl, deviceId, tokenStorage);
// Or refresh explicitly:
var refreshed = await MetaAuth.RefreshAsync(authUrl, cached.RefreshToken);
```

**Client — mid-session (`MetaTokenManager`).** For long sessions that outlive the access-token TTL, drive the connection from a token manager instead of a fixed string. It hands out a currently-valid token (refreshing on demand, **single-flight**), and every transport accepts it as an **access-token provider** so a reconnect picks up a fresh token automatically — no rebuilding the connection.

```csharp
var tokens = new MetaTokenManager(authUrl, deviceId, tokenStorage);
tokens.StartAutoRefresh();                                   // optional proactive renewal before expiry

// SignalR: provider ctor (invoked on every (re)connect)
var connection = new SignalRConnection(serverUrl, tokens.GetTokenAsync);
// HTTP polling: provider on the options (resolved fresh per request)
var http = new HttpPollingConnection(new HttpPollingConnectionOptions {
    ServerUrl = serverUrl, AccessTokenProvider = tokens.GetTokenAsync });

await tokens.RefreshNowAsync();  // reactive: force a refresh (e.g. from a session-lost handler)
```

The fixed-string `accessToken` ctors/options remain for back-compat. `GetTokenAsync`'s fast path returns the cached token with no network call, so per-request/per-reconnect resolution is cheap.

**Recovering from a server-rejected token (0.30.1+).** A cached access token that hasn't locally expired can still be rejected by the server — most commonly when the JWT signing key changed, so the token now fails signature validation and `ConnectAsync` throws `Authentication is required`. The client can't tell from the token alone (it only checks expiry). Set `MetaClientOptions.AccessTokenSource` and recovery is **automatic** — on an auth-type connect failure the client invalidates the token and retries the connect once:

```csharp
var tokens = new MetaTokenManager(authUrl, deviceId, storage);
var connection = new SignalRConnection(url, tokens.GetTokenAsync);              // provider, not a fixed string
var client = new MetaClient(connection, serializer,
    new MetaClientOptions { AccessTokenSource = tokens });                      // ← enables auto-recovery
```

This works only with a **provider**-based connection (`tokens.GetTokenAsync`); a fixed-token connection still holds the old value on retry. To override the default policy use `MetaClientOptions.OnConnectAuthFailedAsync` (return `true` to retry once). Without a token manager, recover manually: `MetaAuth.ClearToken(storage)` then re-login + rebuild the connection.

### Platform Authentication (0.6.0+)

In addition to device-based login, the framework ships pluggable validators for platform identity providers. Each is a separate NuGet package; you register them via DI and the existing `/meta/auth/login-platform` endpoint dispatches to the right validator by `platform` name.

| Package | Platform | Token type |
|---------|----------|-----------|
| `CoreGame.SharedMeta.Auth.Google` | Google Play Games | server auth code → OAuth2 token exchange |
| `CoreGame.SharedMeta.Auth.Apple` | Sign in with Apple | JWT identity token → JWKS verification |
| `CoreGame.SharedMeta.Auth.Steam` | Steam | session ticket → Steam Web API validation |

Custom validators implement `IExternalAuthValidator` (`Validate(token)` → returns stable `platformUserId` + display data).

```csharp
builder.Services.AddMetaAuth(...);
builder.Services.AddMetaAuthGoogle(o =>
{
    o.ClientId = "...apps.googleusercontent.com";   // OAuth2 *Web* client id (not Android/iOS)
    o.ClientSecret = "...";                          // server-side only — never ship to clients
});
```

**Server-side validation (what the client sends is verified).** For Google, the client sends a one-time **server auth code** as `platformToken`; `GoogleAuthValidator` exchanges it with Google's OAuth2 token endpoint using the `ClientSecret`, receives a signed `id_token`, and extracts the stable `sub`. The code can't be forged (the exchange requires the secret) and is single-use. Apple validates the identity-token JWT against Apple's JWKS; Steam validates the session ticket via the Steam Web API.

**Login endpoint:**
```
POST /meta/auth/login-platform
Body: { "platform": "google", "platformToken": "<server auth code>" }
Response: { "token": "jwt...", "playerId": "abc123_20260226", "isNewPlayer": false,
           "expiresAt": "...", "refreshToken": "...", "refreshExpiresAt": "..." }
```

The flow is the same as device login but the auth key is `{platform}:{platformUserId}` instead of the raw device id, so the same player can have multiple keys (e.g. one device id + one Google account) all mapped to the same `PlayerId`.

**Client (Unity, Google Play) — end to end.** The Google Play Games SDK is game-side (the framework doesn't depend on it). Get a fresh server auth code from GPGS and feed it to the token manager's login strategy, so platform sign-in participates in the full token lifecycle (refresh + recovery):

```csharp
// Game-side: obtain a fresh GPGS server auth code (Google Play Games plugin v2).
Task<string> GetGoogleServerAuthCodeAsync() {
    var tcs = new TaskCompletionSource<string>();
    PlayGamesPlatform.Instance.RequestServerSideAccess(forceRefreshToken: true, tcs.SetResult);
    return tcs.Task;
}

var tokens = new MetaTokenManager(
    $"{serverUrl}/meta/auth", tokenStorage,
    login: async (url, ct) =>
    {
        var code = await GetGoogleServerAuthCodeAsync();           // fresh, single-use
        return await MetaAuth.LoginWithPlatformAsync(url, "google", code, cancellation: ct);
    });

await tokens.GetTokenAsync();                                       // first login (learns PlayerId)
var connection = new SignalRConnection($"{serverUrl}/meta", tokens.GetTokenAsync);
var client = new MetaClient(connection, serializer, new MetaClientOptions { AccessTokenSource = tokens });
```

The framework's refresh token then renews the session without re-hitting Google; the `login` delegate runs only for a full re-login (first sign-in, or after the refresh token is gone). Linking a Google account to an existing device player is the **Account Linking** flow below.

### Account Linking

After a player is authenticated via one method, additional auth keys can be linked to the same `PlayerId`:

```
POST /meta/auth/link [Authorize]
Body: { "platform": "google", "platformToken": "..." }
Response: { "success": true, "linkedKeys": [ "device:dev-abc", "google:1234567" ] }

POST /meta/auth/unlink [Authorize]
Body: { "authKey": "device:dev-abc" }
Response: { "success": true, "remainingKeys": [ "google:1234567" ] }

GET /meta/auth/keys [Authorize]
Response: [ "device:dev-abc", "google:1234567" ]

POST /meta/auth/reset-device [Authorize]
Body: { "deviceId": "dev-abc" }
Response: { "success": true }
```

Safety:
- Cannot unlink the **last** auth key via `/unlink` (would orphan the account).
- Linking fails if the platform is already bound to a different `PlayerId` (conflict detection).
- All link/unlink endpoints require an `[Authorize]` JWT — only the player themselves can modify their key list.

### Resetting Device Binding (0.10.1+)

Sometimes a player wants to start a fresh profile while keeping the same device — for example, a "Reset progress" or "Switch account" debug/settings command. `/unlink` cannot do this when the device is the player's only auth key (the "last key" safety check rejects it).

`POST /meta/auth/reset-device` is an authorized endpoint that **force-unlinks** the given `deviceId` from the current player, with no "last key" check. After the call, the next login with that `deviceId` creates a new player profile.

**Client (any platform):**
```csharp
await MetaAuth.ResetDeviceAsync(
    authUrl,
    deviceId,                  // typically SystemInfo.deviceUniqueIdentifier
    accessToken,               // current JWT
    tokenStorage);             // optional — cached token is cleared on success

// next login creates a brand-new player
var login = await MetaAuth.EnsureAuthenticatedAsync(authUrl, deviceId, tokenStorage);
```

The server-side primitive is `IAuthGrain.ForceUnlinkAsync()` — same as `UnlinkAsync` but skips the safety check. Use it directly from server code if you need to wipe a binding outside the HTTP flow.

### Transport Integration

- **SignalR**: Token via query string `?access_token=jwt_token`
- **HTTP Polling**: Token via `Authorization: Bearer jwt_token` header
- Server extracts PlayerId from JWT claims (`sub` or `ClaimTypes.NameIdentifier`), overrides request PlayerId
- Pass a fixed token (`accessToken` ctor / `AccessToken` option) **or** an access-token provider (`Func<Task<string?>>` ctor / `AccessTokenProvider` option, 0.30.0+) — the provider is resolved on each (re)connect (SignalR) or per request (HTTP), so a refreshed token is picked up without rebuilding the connection. See **Refresh Tokens** above.

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
// Unity: use PlayerPrefsTokenStorage. Pass the deviceId so dev builds running multiple
// instances on one device (or with random/rotating deviceIds) each get their own token slot —
// without scoping, a fresh deviceId picks up a JWT cached for a previous PlayerId.
ITokenStorage storage = new PlayerPrefsTokenStorage(deviceId);

// Login or reuse cached token (skips network call if token is still valid)
var login = await MetaAuth.EnsureAuthenticatedAsync($"{serverUrl}/meta/auth", deviceId, storage);

// Logout
MetaAuth.ClearToken(storage);
```

`CachedToken.IsValid` checks expiry with a 5-minute safety margin.

**Custom storage**: Implement `ITokenStorage` for platform-specific storage (e.g., `SecureStorage`, file-based, `PlayerPrefs`). The interface has three methods: `Load()`, `Save(CachedToken)`, `Clear()`.

> **Migration from `MetaClient.LoginAsync`**: `MetaClient.LoginAsync` is still available on .NET but `MetaAuth.LoginAsync` is preferred — it works on all platforms and supports cancellation tokens.

### Client Version Enforcement

The server can reject clients whose version is incompatible before they establish a session.

**Compatibility rules** (checked on every `SessionConnect`):

| Condition | Result |
|---|---|
| Client did not send a version | Allowed through (backward compat) |
| `clientMajor ≠ serverMajor` | Rejected — breaking change, hard upgrade required |
| `clientVersion < MinClientVersion` | Rejected — client too old, soft upgrade required |
| Otherwise | Accepted |

**Server static config** (`appsettings`-level, set at startup):

```csharp
builder.Services.AddSingleton(new MetaTransportOptions
{
    RequireAuthentication = true,
    ServerVersion    = "1.3.0",   // this silo's version — sent to every client
    MinClientVersion = "1.2.0",   // oldest client version accepted
});
```

**Client** — pass the game's version string when creating the connection:

```csharp
// Unity
var connection = new SignalRConnection(serverUrl, accessToken, clientVersion: Application.version);

// .NET
var connection = new SignalRConnection(serverUrl, accessToken, clientVersion: "1.3.0");

// HTTP polling (.NET)
var connection = new HttpPollingConnection(new HttpPollingConnectionOptions
{
    ServerUrl = serverUrl,
    ClientVersion = "1.3.0"
});
```

When the connection is rejected, `MetaClient.ConnectAsync()` throws `InvalidOperationException` with the server's error message. Handle it to show an upgrade prompt:

```csharp
try
{
    await client.ConnectAsync();
}
catch (InvalidOperationException ex)
{
    Debug.LogError($"[Meta] Connection refused: {ex.Message}");
    // Show "Please update your game" UI, redirect to store, etc.
}
```

**Runtime update without restart** — `MinClientVersion` lives on `IVersionPolicyGrain`, a persisted Orleans singleton (key `"global"`) shared across the whole cluster. Push from any process — a dedicated admin service, an in-process `MapPost`, etc. — and every silo's `ClientVersionPolicy` picks the new value up within its 60-second TTL window:

```csharp
app.MapPost("/admin/min-client-version", async (string? version, IGrainFactory grains) =>
{
    await grains.GetGrain<IVersionPolicyGrain>("global").SetMinClientVersionAsync(version);
    return Results.Ok();
});
```

Pass `null` to clear the override and fall back to `MetaTransportOptions.MinClientVersion`.

**How the gate works on connect** — `MetaConnectionHandler` calls `ClientVersionPolicy.ValidateAsync(request.ClientVersion)` once per `SessionConnect`. The policy refreshes its grain cache when stale, runs the major/minor/patch comparison, and returns a `ClientVersionValidationResult { ServerVersion, MinClientVersion, Error }`. Non-null `Error` ⇒ the handler rejects the session and echoes both versions back so the client can show an actionable upgrade prompt.

Precedence (highest → lowest): grain override → `MetaTransportOptions.MinClientVersion`.

**Unity architecture**: Auth code is split into two assemblies due to `noEngineReferences`:

| Assembly | `noEngineReferences` | Contents |
|---|---|---|
| `SharedMeta.Runtime` | `true` | `MetaAuth`, `ITokenStorage`, `CachedToken`, `MetaLoginResult` |
| `SharedMeta.Auth.Client` | `false` | `UnityMetaAuth`, `PlayerPrefsTokenStorage` |

`UnityMetaAuth.Register()` is called automatically via `[RuntimeInitializeOnLoadMethod]` — it sets `MetaAuth.LoginFunc` to the Unity `UnityWebRequest` implementation. No manual registration needed.

**Custom auth providers (0.9.3+)**: `MetaAuth.Provider` accepts any `IMetaAuthProvider` implementation and takes precedence over the legacy Func hooks and the built-in HTTP fallback. Use this to plug in a local backend (`SharedMeta.Backend.Local`'s `LocalMetaAuthProvider`), Firebase, PlayFab, or any other auth service without game-code changes. Priority order: `Provider` → legacy Funcs → HTTP default.

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

### Mid-Method Persistence (Context.SaveStateAsync)

`ForcePersist` saves state **after** the method returns. When you need a checkpoint **during** execution — e.g., before sending an acknowledgement to another entity — use `Context.SaveStateAsync()`:

```csharp
// IRoomService must be declared as a dependency on this impl:
//   [MetaServiceImpl(typeof(IPlayerService), typeof(PlayerState), typeof(IRoomService))]
[MetaMethod(Mode = ExecutionMode.Server)]
async Task<ResolveResult> ResolveSessionResources(string blockUid)
{
    // 1. Query room for actual resources
    var roomApi = GetIRoomService(roomId);
    var resources = await roomApi.GetResources(Context.CallerId, blockUid);

    // 2. Apply to own state
    State.ApplyResources(resources);
    State.RemoveResourceBlock(blockUid);

    // 3. Persist NOW — before sending ACK
    await Context.SaveStateAsync();

    // 4. Safe to acknowledge — if crash happens here, player state is already saved
    await roomApi.AcknowledgeBlock(Context.CallerId, blockUid);

    return new ResolveResult { Success = true };
}
```

**Behavior by environment:**

| Environment | Behavior |
|-------------|----------|
| Server | Persists state + random bytes to Orleans storage immediately, resets persistence tracking |
| Client | No-op (`Task.CompletedTask`) — method continues without side effects |

**When to use:**
- Pseudo-transactional cross-entity resource transfers (lock → save → ack)
- Long-running server methods where intermediate state must survive a crash
- Any point where you need a durable checkpoint before an irreversible external action

**ForcePersist vs SaveStateAsync:**

| | `[MetaMethod(ForcePersist = true)]` | `Context.SaveStateAsync()` |
|---|---|---|
| When | After method returns | Explicit call site during execution |
| Granularity | Whole method | Any point within a method |
| Declaration | Attribute on interface | Code in service implementation |
| Use case | "This method must always save" | "Save here, then continue" |

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
  │      │  Notifies entities via the generated contract server API
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
  <PackageReference Include="CoreGame.SharedMeta.Core" Version="0.12.3" />
  <PackageReference Include="CoreGame.SharedMeta.Client" Version="0.12.3" />
  <PackageReference Include="CoreGame.SharedMeta.Serialization.MemoryPack" Version="0.12.3" />
  <PackageReference Include="CoreGame.SharedMeta.Transport.SignalR.Client" Version="0.12.3" />
  <PackageReference Include="CoreGame.SharedMeta.Generator" Version="0.12.3"
                    PrivateAssets="all" OutputItemType="analyzer" />
  <!-- Optional: MessagePack protocol for SignalR (better performance) -->
  <!-- <PackageReference Include="CoreGame.SharedMeta.Transport.SignalR.MessagePack" Version="0.12.3" /> -->
</ItemGroup>
```

### Unity Client (BestHTTP)

For Unity projects, transports are included in the UPM package (`com.coregame.sharedmeta`). Use the **SharedMeta Project Wizard** (Tools > SharedMeta > Project Wizard) to generate client code with the correct transport configuration.

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
3. When match forms: awaits `_players.OnMatchFoundAsync(entityId, evt)` per player
4. That dispatches `OnMatchFound` on the player's profile entity, updating state with match info
5. All subscribers of each entity receive the match notification as a broadcast

### Client-Side Match Notification

```csharp
// Subscribe to match found event
client.Resolver.OnMethodReplayed<MatchFoundEvent>(
    profileEntityId,
    GameMethodIds.IProfileService_OnMatchFound_v0,
    e =>
    {
        Console.WriteLine($"Match found! ID: {e.MatchId}");
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
    // Patch-level desync (deep desync, 0.7.0+)
    void OnPatchDesync(string serviceName, string methodName,
        uint serverCrc, uint localCrc);
    void OnCrossEntityResult(string entityId, string serviceName,
        string methodName, byte[]? resultBytes);
    Task<StateComparisonResult> CompareFullStateAsync(string entityId);
}
```

There are three independent desync detection layers, each fires its own callback:

| Layer | When it fires | What it catches |
|-------|---------------|-----------------|
| **Result** | `OnResultMismatch` — return value bytes differ | Different return values from local vs server execution |
| **Random** | `OnRandomDesync` — scroll delta differs (method name `"[NamedRandom:{i}]"` suffix for named streams) | Mismatched number of `Context.Random` or `[NamedRandom]` calls |
| **Patch** (deep desync) | `OnPatchDesync` — patch CRC differs | State mutations differ even when return values match |

Result-level catches the easy cases. Patch-level catches "the method returned `true` on both sides but wrote different values to the state" — for example, `state.Money = rng.Next(100)` with `System.Random`. See [Deep Desync Detection](#deep-desync-detection-070) below.

### Desync Message Values

The `[Desync]` log line and `DesyncException.Message` print both result values. For a DTO that
does not override `ToString()`, `object.ToString()` yields the type name — so the message named
the same type twice and said nothing about what diverged:

```
Desync in IShopService.SellCargo: server=Game.Meta.SellCargoResult, local=Game.Meta.SellCargoResult
```

The generator therefore emits a `MetaDescribe` formatter per result type (and, recursively, per
nested type) into `DesyncFormatters.g.cs`, and the desync paths call it:

```
Desync in IShopService.SellCargo: server=SellCargoResult { Gold = 10, Item = "ore", Line = CargoLine { Quantity = 5, Sku = "SKU-7" }, Ids = [1, 2, 3] }, local=SellCargoResult { Gold = 8, ... }
```

Resolved at compile time rather than by reflection — IL2CPP strips member reflection, which is
exactly where these logs matter most. Types that override `ToString()` (records included) keep
their own rendering; nesting stops at 3 levels and collections at 8 elements.

Trim the output project-wide:

```csharp
// Assembly.cs / AssemblyInfo.cs of the meta assembly
[assembly: SharedMetaDiagnosticsOptions(DesyncValues = DesyncValueDetail.Short)]
```

| `DesyncValues` | Members | Nested types | Collections |
|----------------|---------|--------------|-------------|
| `Full` (default) | all public | recursed, 3 levels | first 8 elements |
| `Short` | all public | `<TypeName>` | `[N items]` |

The values are also available programmatically — `DesyncException.ServerResult` / `.LocalResult`
hold the objects, and the full byte forms of both sides go to the server in the desync report.

### Deep Desync Detection (0.7.0+)

Field-level state mutation tracking via PatchNode CRC comparison. Opt-in per service:

```csharp
[MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), DeepDesync = true)]
public partial class ExpeditionService : IExpeditionService { … }
```

The generator produces a `_PatchTracked` copy of the service class where every `State.X = …` write routes through a generated `{State}PatchWrapper`, recording the field id and serialized value into a `PatchNode` tree. Server computes FNV-1a CRC of the serialized tree after each call; client does the same for its local execution; the CRCs are compared in the existing `OnResult/OnRandom` validation pipeline. Mismatch fires `OnPatchDesync`.

> **0.24.2+:** the `_PatchTracked` copy is no longer DeepDesync-exclusive. It is also auto-generated (per state, including siblings) for **force-patch-able** services so force-patch produces a real diff — see *ServerPatch Mode → Auto-generated patch-tracking copy*. `DeepDesync = true` still forces the copy (for CRC detection) even when the service isn't force-patch-able. The compile-time tracking guard below applies to both.

**Runtime activation** is independent of the compile-time flag:

```csharp
// Server: global override (default null = per-session opt-in)
services.Configure<EntityGrainOptions>(o => o.DeepDesyncEnabled = true);

// Client: per-session toggle (server must opt in via MetaTransportOptions.AllowDebugApi)
await client.SetDeepDesyncAsync(true);
```

`[MetaServiceImpl(DeepDesync = true)]` only generates the supporting infrastructure (PatchTracked service copy + `PatchSchema`); it does not force the feature on at runtime. This way `SetDeepDesyncAsync(false)` actually disables CRC computation per-session, and `EntityGrainOptions.DeepDesyncEnabled = false` works as a kill switch.

**`PatchableList<T>`, `PatchableDictionary<K,V>`, `PatchableHashSet<T>`** wrap base collections and auto-record mutations into the same patch tree — use them for collection fields if you want fine-grained tracking. They have full API parity with the base collections + implicit conversion from them.

### Server-Side Desync Reporting (0.7.0+)

When a desync is detected on the client, the framework can ship the full evidence to the server for centralized logging and analysis. Opt-in:

```csharp
builder.Services.AddSingleton(new MetaTransportOptions
{
    DesyncReportingEnabled = true,             // default: false
    DesyncReportPatchCacheSize = 16,           // per-connection patch ring
    DesyncLogLevel = DesyncLogLevel.Warning,   // None | Warning | Information | Debug
});
```

Behavior:
- **Server side** caches the most recent server-computed `PatchNode` per `(entityId, service, method)` per connection (small bounded ring).
- **Client side** detects a CRC mismatch (or result/random mismatch) → fires `OnPatchDesync`/`OnResultMismatch`/`OnRandomDesync` locally → fire-and-forget `SendDesyncReportAsync(...)` to the server with `MismatchKind` flags (`Patch | Result | Random`) and the relevant payload (patch bytes / result bytes / random delta).
- **Server** receives the report, looks up the cached server patch, runs `PatchNodeDiffer.Compare`, formats the divergence as JSON via `PatchTextRenderer.DiffToJson` (using the per-state `IPatchSchema`), stores a `DeepDesyncReport` in `DesyncReportGrain` (per-player, bounded ring of 50), and logs at the configured level.

Result and random mismatches don't need any server cache — both values are sent inside the request itself.

### Patch Schema and JSON Renderer

`{State}PatchSchema.g.cs` is generated alongside every `{State}PatchWrapper.g.cs`. It maps `[MemoryPackOrder(n)]` field ids to property names and decoder types. `PatchTextRenderer.ToJson` visualizes a single patch and `PatchTextRenderer.DiffToJson` produces a side-by-side `{ "server": ..., "client": ... }` JSON of two diverged patches:

```
PATCH:
{
  "Cells": {
    "server": [0, 0, 1, 0, 0, 2, 0, ...],
    "client": [0, 0, 1, 0, 0, 0, 0, ...]
  },
  "TreasuresCollected": { "server": 5, "client": 4 }
}
```

`IPatchSchemaRegistry` is registered automatically by `ConfigureMeta()`. Schemas are only consulted when a desync report is being formatted — **zero overhead** in normal RPC flow.

### Granular Collection Patches (0.9.0+)

When a service has `[MetaServiceImpl(DeepDesync = true)]`, list-typed state fields use a fine-grained patch representation instead of dumping the whole list on every mutation. This dramatically reduces patch sizes for the common case of mutating one element of a large collection.

**Element-sub-wrappable lists.** If `T` in `List<T>` has its own `[MemoryPackOrder]` properties, the generator emits a specialized `{Element}PatchableList` nested inside the parent `{State}PatchWrapper`. Its indexer hands out `{Element}PatchWrapper` bound to a per-element subtree node:

```csharp
[MemoryPackable]
public partial class Hero
{
    [MemoryPackOrder(0)] public int Id { get; set; }
    [MemoryPackOrder(1)] public int Exp { get; set; }
    [MemoryPackOrder(2)] public List<Item> Equipment { get; set; } = new();
}

[MemoryPackable]
public partial class PartyState : ISharedState
{
    [MemoryPackOrder(0)] public List<Hero> Heroes { get; set; } = new();
}

[MetaServiceImpl(typeof(IPartyService), typeof(PartyState), DeepDesync = true)]
public partial class PartyService : IPartyService
{
    private PartyState state => State;

    public void AwardExp(int heroIndex, int amount)
    {
        // Patch contains only Heroes/[heroIndex]/Exp — other heroes are NOT touched.
        state.Heroes[heroIndex].Exp += amount;
    }

    public void EquipItem(int heroIndex, Item item)
    {
        // Two-level nesting works end-to-end. Patch contains an element subtree at
        // Heroes/[heroIndex] with an Equipment collection node carrying one Insert op.
        state.Heroes[heroIndex].Equipment.Add(item);
    }
}
```

**Compile-time tracking guard.** Helper methods that look up an element must be typed as `{Element}PatchWrapper`, not raw `{Element}`. The same source compiles in both the regular service class and the generated `_PatchTracked` copy thanks to a one-way `implicit operator {Wrapper}({State}?)`. The reverse direction is intentionally absent — returning raw `Hero` from a helper compiles in the regular branch but fails the `_PatchTracked` copy with `CS0029`, catching silent loss of patch tracking at compile time.

```csharp
private PartyStatePatchWrapper.HeroPatchWrapper? FindById(int heroId)
    => state.Heroes.FirstOrDefault(h => h.Id == heroId);

public void AwardExpById(int heroId, int amount)
{
    var hero = FindById(heroId);    // var = HeroPatchWrapper in _PatchTracked
    if (hero != null) hero.Exp += amount;
}
```

If you write `Hero? FindById(...)` instead, the regular branch compiles fine (it returns raw `Hero` from `List<Hero>.FirstOrDefault`), but the `_PatchTracked` branch fails:
```
error CS0029: Cannot implicitly convert type
'PartyStatePatchWrapper.HeroPatchWrapper' to 'Hero'
```
Use `var` for locals so the same source works in both branches.

**Structural ops.** `Add` / `Insert` / `RemoveAt` / `Remove` / `Clear` and indexer assignment record individual `PatchListOp` entries on the collection node's `StructuralOps` list:

| Method | Op kind | Notes |
|--------|---------|-------|
| `Add(item)` | `Insert` | Index = `Count - 1` after the add |
| `Insert(idx, item)` | `Insert` | Sender shifts existing element children's indices forward |
| `RemoveAt(idx)` | `RemoveAt` | Sender drops element child at `idx` and shifts higher indices down |
| `Remove(item)` | `RemoveAt` | Resolved to index via `IndexOf` |
| `list[i] = value` | `Set` | Drops in-place mutations for that index (element wholesale replaced) |
| `Clear()` | `Clear` | Drops all element children and prior structural ops |
| `Sort` / `Reverse` / `AddRange` / `RemoveAll` | `FullReplace` | Falls back to packing the whole list |
| `state.Heroes = newList` | `FullReplace` | Wholesale field reassignment is also recorded as a `FullReplace` op (clears any prior ops/element children on the node first) |

All applied in submission order on the receiver via `CollectionPatchApplier.Apply<T>(...)`.

**Invariant:** list-typed patch nodes never carry a terminal `Value` — wholesale replacement is just another op in the `StructuralOps` stream. This means **assign-then-mutate in the same call works**:

```csharp
public void RegenerateMap()
{
    state.Cells = new List<byte>(totalCells);   // FullReplace op (packed empty list)
    for (int i = 0; i < totalCells; i++)
        state.Cells.Add(0);                      // chained Insert ops
    state.Cells[5] = wallValue;                  // chained Set op
}
```

The receiver applies all ops in submission order — `FullReplace` → `Insert` × N → `Set` — so client and server converge to identical lists. Earlier versions wrote a terminal `Value` on the setter and silently dropped subsequent structural ops; that's no longer possible.

**Mixed structural + element mutations** in the same call work correctly: when `RemoveAt` shifts indices, the sender automatically calls `ShiftElementChildren` on the collection node so any pending element subtree mutations end up at the correct post-removal indices.

**Scalar lists also benefit.** `PatchableList<T>` for `List<int>`, `List<byte>`, `List<string>`, etc. uses op-based recording in 0.9.0. A single `state.Cells[5] = newValue` writes one `Set` op instead of dumping the entire array — important for things like `List<byte> Cells` map data.

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

### Signature Negotiation (0.22.0+, redesigned in 0.24.0)

The generator emits a `MetaClientSignature` constant containing every `[MetaMethod]` the client knows about. On `SessionConnect` the client sends just its `ClientSignatureHash` (8 bytes). The server side:

1. Looks up the hash in its silo-local cache. **Known + same `ServerSignatureHash` as client's cached entry** → ships back a `ClientSignatureAnnotated` (verdict + id-translation array) directly on the SessionConnect response. **Unknown OR server redeployed** → returns `NeedsSignatureRegistration = true`, prompting the client to follow up with `RegisterClientSignatureRequest` carrying the full signature; server computes the annotation, persists the signature, returns the annotation on the phase-2 response.
2. The annotation lives on the silo-local cache for the lifetime of the silo; the underlying signature is persisted in `IClientSignatureGrain` keyed by hash and survives silo restarts.

**Client wiring is automatic (0.24.0+).** The generator publishes the assembly's `GameServiceDiscoveryBase.ClientSignature` into `ClientSignatureDefault.Value` — from the generated `RegisterAllServices()` (primary path, works on Unity) and, on net5+, additionally from a module initializer. The client resolves that default at connect time when `MetaClientOptions.ClientSignature` is left null — so a normal client needs **no** manual wiring (just call `RegisterAllServices()` before connecting, as usual). Set `MetaClientOptions.ClientSignature` explicitly only to pin one signature when several signature-bearing assemblies are loaded (last writer otherwise wins). To force legacy opt-out (Hash=0, no negotiation) set `MetaClientOptions.DisableClientSignatureNegotiation = true` — but note 0.24.0 servers reject Hash=0 on RPC dispatch, so opt-out only works against a pre-0.24 server.

**`ClientSignatureAnnotated` wire shape:**
```csharp
public partial class ClientSignatureAnnotated
{
    public ulong ClientSignatureHash;
    public ulong ServerSignatureHash;
    public ushort[] ServerToClient;        // index = serverMethodId, value = clientMethodId
    public MethodStatus[] Statuses;        // index = clientMethodId
}

public enum MethodStatus : byte { Ok, ForceServerPatch, Rejected }
```

**Client-side cache** — `IServerAnnotationCache` (in-memory default; Unity `PlayerPrefsServerAnnotationCache` for persistence across app launches). Cache key = `clientHash`; invalidation when the `ServerSignatureHash` returned on a fresh connect diverges from the cached entry's stored hash.

**What goes into the signature hashes:**
- `MetaClientSignature.SignatureHash` = FNV-1a over canonical `{ServiceName}.{MethodAlias}@{Version}#{ArgHash}` lines, sorted.
- `MetaServerSignature.SignatureHash` = same, plus per-method server-only fields (`MinCompatibleVersion`, `GenerateClientApi`, `ConfigTypeFullName`, `PatchTrackingAvailable`) and `[MetaConfigStructureBoundary]` declarations.

**What the per-method `Statuses[clientId]` verdict can be:**
- `Ok` (default) — the client's `Version` exactly matches a version the server still declares; call proceeds normally on the wire.
- `ForceServerPatch` — the client's `Version` isn't declared on the server but falls back to a higher arg-compatible body at/above `MinCompatibleVersion`, **and the service can produce a patch** (`PatchTrackingAvailable`); the client's local body would diverge, so it downgrades to ServerPatch (server runs the authoritative body, ships a diff). Self-clears once the client ships at the declared version.
- `Rejected` — method missing on the server, flagged `GenerateClientApi = false`, no arg-compatible body (`ArgHash` mismatch), the client's `Version` is below the fallback entry's `MinCompatibleVersion`, **or** the fallback would force ServerPatch but the service opted out of patch tracking (`[MetaService(PatchTracking = false)]` → `PatchTrackingAvailable = false`, no copy, can't ship a diff). Client gate throws `IncompatibleFeatureException` locally without going to the wire. (Config-boundary force-patch on a patch-tracking-disabled service rejects at *subscribe* instead — `EntityGrain` throws `IncompatibleFeatureException` with a `FeatureRequirement` before any state mutation.)

**Tracing the handshake:** both sides log INFO entries at every phase transition. Run client + server with logging enabled to verify they understand each other:
```
# server-side
Handshake[playerX] phase-1 HIT: clientHash=0x..., serverHash=0x..., annotation: 47 methods (0 rejected, 0 force-patch)
Handshake[playerX] phase-1 MISS: clientHash=0x... unknown, serverHash=0x...; needs phase-2 registration
Handshake[playerX] phase-2 REGISTER: clientHash=0x... (47 methods) -> serverHash=0x..., annotation: 0 rejected, 0 force-patch, 3 server-only

# client-side
[ClientDispatcher] Handshake phase-1 HIT: clientHash=0x..., serverHash=0x..., annotation: ...
[ClientDispatcher] Handshake phase-1 MISS: ...; sending phase-2 RegisterClientSignature
[ClientDispatcher] Handshake phase-2 REGISTERED: clientHash=0x... (47 methods) -> serverHash=0x..., annotation: ...
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

### `[GeneratedFromMetaMethod]` — IDE-tool linkback (0.16.0+)

Every method emitted as a mirror of a `[MetaMethod]` is stamped with `[GeneratedFromMetaMethod(typeof(IFoo), "Bar")]`. The attribute appears on:

* `*ApiClient.{Name}Async` / `{Name}Sync` / `{Name}Signal`
* `*EntityQueryApi.{Name}Async`
* `{I}EntityCaller.{Name}Async` (the cross-entity caller interface)
* `{Service}EntityRecorder.{Name}Async`, `{Service}EntityReplayer.{Name}Async`, `{Service}LocalEntityCaller.{Name}Async` (its three runtime implementations)

It captures `(ServiceInterface : Type, MethodName : string)` — a `typeof()` reference rather than a string FQN, so it follows refactor-rename of the interface type.

```csharp
[global::SharedMeta.Core.GeneratedFromMetaMethod(typeof(global::CardGame.Shared.ICardGameService), "RegisterPlayer")]
public Task RegisterPlayerAsync(string playerName, string profileEntityId) { ... }
```

**Tooling that consumes this attribute:**

* **Rider plugin** at [`SharedLibs/RiderPlugin`](https://github.com/CoreGameIO/SharedLibs/tree/main/RiderPlugin) — extends Find Usages and Go to Declaration so a single action covers the user-authored `[MetaMethod]` and every generated counterpart. See the project README for install instructions.

The runtime ignores the attribute. It is purely a marker for downstream tooling.

---

## 21. Attribute Reference

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[MetaService]` | Interface | Marks shared service for code generation |
| `[MetaMethod]` | Method | Configures execution mode, alias, versioning |
| `[MetaServiceImpl]` | Class | Marks service implementation for context injection |
| `[MetaInit]` | Method | State initialization/migration. One- or two-arg form: `Init(int version)` or `Init(int version, int target)` |
| `[MetaStateVersion(N, "M.m", typeof(TConfig))]` | Class (state) | Schema migration breakpoint — schema N requires `TConfig >= M.m`. Multiple per state form an AND gate. (0.19.0+) |
| `[NoMigrate]` | Method | Skip lazy migration; pin `Context.Config` to the schema-floor branch. (0.19.0+) |
| `[MinStateVersion(N)]` | Method | Cap migration target at schema N for this method. (0.19.0+) |
| `[MetaConfig]` | Class | Marks a class as static game configuration |
| `[ServiceConfig(typeof(TConfig), "Name")]` | Interface | Independently-versioned config, repeatable, all symmetric (no privileged "primary") — resolves synchronously in every execution mode; replaces `[MetaService].ConfigType`/`DefaultConfig` (obsolete but functional). (0.33.0+) |
| `[MetaConfigVersion(Client = …, Config = …)]` | Class (config) | Per-client config branch routing rule. Pattern grammar: literal / `x` capture / `N+` range / `*` wildcard. Multiple per class. (0.19.0+) |
| `[SharedState]` | Class | Marks shared state entity |
| `[Tracked]` | Field | Push-based change tracking property for UI binding (client-only) |
| `[Trigger]` | Method | Auto-execute after condition on another method |
| `[Subscribe]` | Event | Declare method subscription |
| `[ServerMetaService]` | Interface | Server-only service (generates replayer) |
| `[StatelessMetaService(typeof(TConfig))]` | Interface | No-entity service resolving only a linked `[MetaConfig]`. (0.33.0+) |
| `[StatelessMetaServiceImpl(typeof(IThing))]` | Class | Impl for `[StatelessMetaService]` — injects only a typed `Config` property. (0.33.0+) |
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
| `GenerateClientApi` | bool | true | Generate API client method. When `false`: client API not generated **and** the server rejects forged client RPCs to this method (cross-entity / sibling calls still work). |
| `SkipServerOnFalse` | bool | false | Skip server if local returns false/default |
| `ForcePersist` | bool | false | Always persist after execution |
| `Query` | bool | false | Callable without subscribing (read-only, no broadcast/replay) |
| `OpenAccess` | bool | false | Bypass EntityAccessPolicy for query methods |

### MetaService Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StateType` | Type | required | State class type |
| `ConfigType` | Type | null | Explicit config type for this service |
| `DefaultConfig` | bool | false | Use config class with `[MetaConfig(Default = true)]`. Also opts the service into the generator's auto-`StaticConfigProvider<T>(new T())` fallback on the client (0.17.0+); without this flag, an explicit `RegisterConfigProvider<T>` is required and a missing one throws at first subscribe |
| `AccessPolicy` | EntityAccessPolicy | Open | Subscribe access control |

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

### Mux Transport — High-Fanout Stress Tests (0.22.0+)

`SharedMeta.Debug.Mux` is a debug-only transport that lets one physical SignalR socket carry **N logical client sessions** identified by a per-session tag. Use it when you want to drive thousands of simulated players from a single client process without exhausting socket / thread-pool resources.

**Server side** — map the hub alongside the regular `/meta`:

```csharp
using SharedMeta.Debug.Mux;

var app = builder.Build();
app.MapHub<MetaHub>("/meta");           // production / real clients
app.MapMetaMuxHub("/meta-mux");          // stress-test transport, opt-in
```

`MuxHub` reuses your existing `IMetaConnectionHandlerFactory` — every tag gets its own `IMetaConnectionHandler` keyed in `HubCallerContext.Items`, so all server-side machinery (sessions, capability negotiation, broadcasts, persistence) works exactly the way it does for a regular per-connection client.

**Client side** — build a channel pool once, hand each simulator a logical connection:

```csharp
using SharedMeta.Debug.Mux;

// 10 physical SignalR sockets shared across 1000 simulators (100 sessions/socket).
var channels = new MuxChannel[10];
for (int i = 0; i < channels.Length; i++)
    channels[i] = new MuxChannel("http://localhost:5050/meta-mux");
await Task.WhenAll(channels.Select(c => c.StartAsync()));

for (int player = 0; player < 1000; player++)
{
    IConnection connection = channels[player % channels.Length].CreateConnection(tag: player);
    var client = new MetaClient(connection, new MemoryPackMetaSerializer(), new MetaClientOptions
    {
        PlayerId = $"player-{player}",
        ClientAppVersion = "1.0.0",
    });
    // ... usual MetaClient flow ...
}
```

`MuxConnection` implements `IConnection`, so generated `*ApiClient`, dispatcher, replay, and capability gates work without changes.

**Trade-offs:**
- One physical socket = shared head-of-line behavior. SignalR's protocol serializes frames on the wire; per-tag frames interleave but don't parallelize beyond what SignalR can chunk.
- Capability handshake runs per tag, so the registry sees the same `ClientSignatureHash` from many sessions — cheap.
- Disconnect tears down all tags riding the channel; `GracefulDisconnect(tag)` removes a single session without affecting siblings.
- `SetDebugOptions` / `SendDesyncReport` are intentionally not bridged — use the regular `/meta` hub when you need deep-desync tracing.

**When to use:** load-testing the server's throughput and broadcast fan-out machinery without paying the socket cost of "one WebSocket per simulator." Reference: `examples/ClanWars/ClanWars.Client.Common/StressTestRunner.cs` — pass `--mux-channels 10` to fan 1000 players across 10 sockets.

### Observability (0.23.0+)

SharedMeta instruments the server-side hot paths with built-in `System.Diagnostics.Metrics` meters and `ActivitySource` traces. **No OpenTelemetry NuGet dependency in the framework packages** — hosts opt-in by subscribing to the static `Meter` and `ActivitySource` instances:

| Source | Where defined | What it produces |
|---|---|---|
| `SharedMeta` (Meter + ActivitySource) | `SharedMeta.Server.Core.Telemetry.SharedMetaMeters` / `.SharedMetaActivities` | Server-side instruments: RPC duration, broadcast fan-out, persistence, cross-entity, grain lifecycle, force-patch, sessions |
| `SharedMeta.Client` (Meter + ActivitySource) | `SharedMeta.Client.Telemetry.SharedMetaClientMeters` / `.SharedMetaClientActivities` | Client-side instruments: RPC round-trip, replay duration, connection state transitions, desync detection, config cache |

#### Server-side wire-up (host)

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "MyGame.Server", serviceVersion: "1.0.0"))
    .WithMetrics(m => m
        .AddMeter(SharedMeta.Server.Core.Telemetry.SharedMetaMeters.MeterName)
        .AddRuntimeInstrumentation()        // .NET GC, ThreadPool, JIT, exceptions
        .AddPrometheusExporter())           // scrape endpoint at /metrics
    .WithTracing(t => t
        .AddSource(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.SourceName)
        .AddOtlpExporter());                 // or AddJaegerExporter, AddConsoleExporter, etc.

var app = builder.Build();
app.MapPrometheusScrapingEndpoint();        // GET http://server/metrics
```

NuGets required (host project only, NOT the SharedMeta packages): `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Exporter.Prometheus.AspNetCore` (or `OpenTelemetry.Exporter.OpenTelemetryProtocol`).

Reference: `examples/ClanWars/ClanWars.Server/Program.cs`.

#### Client-side wire-up (.NET client host)

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter(SharedMeta.Client.Telemetry.SharedMetaClientMeters.MeterName)
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());
```

On Unity hosts the same `Meter` / `ActivitySource` is reachable via `MeterListener` / `ActivityListener` directly — OpenTelemetry SDK is optional. If you don't subscribe at all, every instrumented call is a single volatile-flag check with no allocation.

#### Metric catalog (server-side `SharedMeta` meter)

| Metric | Type | Tags | Notes |
|---|---|---|---|
| `sharedmeta.session.connect.duration` | Histogram (ms) | `result` | Handshake including version-gate + signature lookup |
| `sharedmeta.session.active` | UpDownCounter | — | Currently connected sessions |
| `sharedmeta.session.terminated.count` | Counter | `reason` | Disconnects grouped by reason |
| `sharedmeta.entity.subscribe.duration` | Histogram (ms) | `state_type`, `result` | Includes grain activation when cold |
| `sharedmeta.entity.subscribe.count` | Counter | `state_type` | Subscribe events |
| `sharedmeta.entity.subscribers.active` | UpDownCounter | `state_type` | Currently subscribed player-entity pairs |
| `sharedmeta.entity.rpc.duration` | Histogram (ms) | `service`, `method`, `result` | Per-method server processing |
| `sharedmeta.entity.rpc.request_bytes` | Histogram (bytes) | `service`, `method` | Incoming payload size |
| `sharedmeta.cross_entity.call.duration` | Histogram (ms) | `to_service`, `kind`, `result` | Cross-entity hops |
| `sharedmeta.cross_entity.call.count` | Counter | `to_service`, `kind` | `kind = normal | notification` |
| `sharedmeta.broadcast.fan_out_size` | Histogram | `state_type` | Subscribers per broadcast |
| `sharedmeta.broadcast.payload_bytes` | Histogram (bytes) | `state_type`, `kind` | `kind = replay | patch | state` |
| `sharedmeta.broadcast.tailored.count` | Counter | `state_type`, `path` | `path = patch | replay` — per-subscriber routing |
| `sharedmeta.persistence.write.duration` | Histogram (ms) | `state_type` | `WriteStateAsync` |
| `sharedmeta.compat.force_patch.applied` | Counter | `service`, `method`, `kind` | Force-patch decisions on RPC |
| `sharedmeta.grain.activation.count` | Counter | `state_type` | Cold starts |
| `sharedmeta.grain.active` | UpDownCounter | `state_type` | Currently active entity grains |

Plus the standard runtime instrumentation (GC, ThreadPool, JIT) provided by `OpenTelemetry.Instrumentation.Runtime`.

#### Cardinality budget

`service`, `method`, `state_type`, `kind`, `result`, `reason` are all schema-bounded. Typical project: 5–20 services × 5–30 methods + a handful of state types = 100–600 unique series total. Entity IDs are NOT used as metric tags — they go on `Activity` tags instead (no aggregation cost there).

#### Distributed tracing

Server-side spans nest naturally via in-process `Activity.Current` propagation through Orleans grain calls:

```
sharedmeta.session.connect
sharedmeta.entity.subscribe   [state_type=ProfileState, player=p1]
sharedmeta.entity.rpc         [service=IProfileService, method=GainPoints]
└─ sharedmeta.cross_entity.call [to=IClanService, kind=normal]
sharedmeta.persistence.write
```

Client → server wire-level W3C trace propagation (i.e., `traceparent` on `RpcCallRequest`) is **not yet implemented** — client and server traces are independent for now. Planned follow-up.

---

## 23. Capability Overview

| Category | Capabilities |
|----------|-------------|
| **Core** | Shared state definitions, source-generated dispatchers/API clients, context injection, 6 execution modes (Local, Optimistic, Server, CrossOptimistic, ServerPatch, ServerReplace) |
| **Networking** | SignalR (WebSocket), HTTP Long-Polling, BestHTTP (Unity all platforms incl. WebGL), InProcess (testing). All transports implement `IConnection` — swappable at configuration time |
| **Session** | Per-player session management, reconnection with missed packet replay, request idempotency via RequestId, session supersede (single active session per player), optional server-side RPC reordering with stall notifications (0.8.0+) |
| **Broadcast Ordering** | Per-entity sequence ordering, RPC broadcast bundling, deferred responses for gap filling |
| **Authentication** | JWT (device-based), platform auth (Google Play Games / Apple / Steam), account linking and unlinking. Entity access policies (Open, OwnerOnly, UserOwned, Authorized) |
| **Advanced** | Cross-entity calls via Orleans grains, server-side triggers (`[Trigger]`), framework contracts inherited by a service (e.g. `ILobbyListener`), argument transformers (stateless and state-aware), per-method `ForcePersist` |
| **Deterministic Random** | `Context.Random` (optimistic, xoshiro128**) — identical on client and server. `Context.ServerRandom` — server-only with replay. ScrollId delta for desync detection |
| **Time Sync** | `Context.ServerTimeTicks` — synchronized UTC ticks for deterministic time-based mechanics (cooldowns, timers, regeneration) |
| **Desync Detection** | Three layers: Result mismatch, Random scroll mismatch, **Patch CRC** (deep desync, 0.7.0+) — field-level state mutation tracking that catches "return value matched but state diverged". Optional server-side reporting + JSON renderer with `PatchSchema` for human-readable diff (0.7.0+) |
| **Push-Based Reactive** | `[Tracked]` field annotations generate per-property change tracking; clients subscribe to specific field changes via generated `Tracked{State}` API |
| **Serialization** | MemoryPack (transport) + Orleans GenerateSerializer (persistence). MessagePack alternative via `IMetaSerializer` |
| **Persistence** | FileGrainStorage, configurable persistence policy (5 modes), per-method ForcePersist override |
| **Code Generation** | Service dispatchers, typed API clients, context injection, DI registration, MetaProvider routing, PatchWrapper / PatchApplier / PatchSchema per state — all generated at compile time |

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

    [MetaMethod(Mode = ExecutionMode.LocalQuery)]
    int GetItemCount(string itemId);   // 0.29.0+: client-side read over replicated State, no RPC
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

#### Optional: Outgoing-payload pool (0.23.0+)

`PooledPayloadRegistry` is a silo-scoped ref-counted byte-buffer pool. When enabled, broadcast and response payloads serialize into pool slots instead of fresh `byte[]` per call — fan-out reuses one buffer across N receivers. Default OFF; opt in once profiling shows the pool pays off for your workload (it favours high-fan-out broadcasts; single-receiver paths see no win).

```csharp
silo.AddStartupTask<PooledPayloadRegistryStartupTask>()   // assigns unique SiloId via cluster-singleton coordinator grain
    .ConfigureServices(services =>
    {
        services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
        services.Configure<PooledPayloadOptions>(o =>
        {
            o.UsePoolPath   = true;   // route broadcast/response through ref-counted slots
            o.EnableHistory = false;  // per-slot Acquire/IncrementRef/Release stack capture (debug only)
        });
        services.AddSingleton<PooledPayloadRegistry>();
        services.ConfigureMeta();
    });
```

When OFF, `MetaProviderBase.PackBroadcastVariant` falls back to a fresh `byte[]` wrapped as `PooledPayload(bytes, 0)` — GC-managed, no pool plumbing involved at runtime. The wire shape is identical either way; the difference is just where the bytes live.

> **Note.** Intermediate serializations (dispatcher result, patch tree, state pack) always route through the per-grain `GrainScopedSerializer` scratch buffer, regardless of `UsePoolPath`. The pool toggle only affects the outgoing wire payload. ADR: [docs/adr/0.23.0-meta-operation-unification.md](adr/0.23.0-meta-operation-unification.md).

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
- `GenerateClientApi = false` — methods reserved for cross-entity / sibling calls only. The client API is not generated **and** the server rejects forged client RPCs at `EntityGrain.HandleCallAsync` / `HandleQueryAsync` / `HandleSignalAsync` via the generated `IsClientCallable` override. Cross-entity (`HandleCallFromEntityAsync`) and sibling-bypass paths are server-internal and remain available — the trust boundary is the public method on the *calling* entity, which authorized the originating client through its own access policy. A modified client cannot bypass this by hand-crafting an `RpcCallRequest` packet.
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

Orleans `[GenerateSerializer]` + `[Id(n)]` are required on `ISharedState` and nested DTOs when the server uses any standard Orleans storage provider (Azure Tables, Redis, ADO.NET, or `FileGrainStorage` with `UseOrleansSerializer = true` — the default). Unity-side compiles via `Orleans.Stubs`. Skipping them is only safe with `FileGrainStorage(UseOrleansSerializer = false)`.

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
