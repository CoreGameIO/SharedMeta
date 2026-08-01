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
- Transport (wire + replay) is driven by `IMetaSerializer` (MemoryPack/MessagePack), so the attributes above are mandatory. Server persistence is a **separate** channel that goes through whichever Orleans storage provider the host registers. Real providers (Azure Tables, Redis, ADO.NET, the bundled `FileGrainStorage` in its default Orleans mode) use the Orleans serializer and require `[GenerateSerializer]` on every type in persisted grain state plus `[Id(n)]` on each member — including your `ISharedState` and nested DTOs. The UPM package ships `Orleans.Stubs` with no-op `[GenerateSerializer]` / `[Id]` attributes so Unity compiles the same source.
- Orleans attributes are only optional when the server uses `FileGrainStorage` with `UseOrleansSerializer = false` (persistence then runs through `IMetaSerializer`).
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

**Independent streams per state — `[NamedRandom]`:** when several mechanics (combat, loot, map gen) must not share scroll position, declare each on the state:

```csharp
[SharedState]
[NamedRandom("Combat")]
[NamedRandom("Loot")]
public partial class GameState : ISharedState { ... }

// Generator emits typed property on every service Context partial:
int dmg  = CombatRandom.Next(100);
int item = LootRandom.Next(drops.Count);
```

Same semantics as `Context.Random` (shared deterministic stream, per-entity seed from `entityId + ":" + Name`, transported on subscribe). `Seed = "literal"` pins a fixed seed across all entities. Server reports per-index scroll deltas; client catches up on `ServerPatch` / broadcast replay. Reordering attributes reseeds the affected slots — positional storage.

**Seed is server-only and replay-safe to override.** The seed string is consumed locally by `MetaRandom.FromString` on the server and never sent over the wire — clients receive the post-seed `MetaRandom` internal state via `SubscribeResponse`. To make recreated entities (profile reset, recycled grain id) produce different streams, mix in entropy via `EntityGrainOptions.FreshRandomSeedFactory` (0.19.1+):

```csharp
services.Configure<EntityGrainOptions>(o =>
{
    o.FreshRandomSeedFactory = (entityId, streamName) =>
        $"{entityId}:{streamName}:{DateTime.UtcNow.Ticks:x}:{Random.Shared.NextInt64():x}";
});
```

Invoked only on first activation (no persisted bytes yet). `[NamedRandom(Seed = "literal")]` bypasses the factory by design.

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

### LocalQuery (0.29.0+; replaces `Local`)

Read-only client-side compute over locally replicated state, no RPC. Method body returns a value computed from `State`; never executes on the server.

**Synchronous API by default (0.29.2+):** LocalQuery defaults to `Sync = SyncApi.OnlySync` — the generator emits only a synchronous `{Method}Sync(...)` on `{Service}ApiClient`, run over the local `State` snapshot in the calling frame (no `await`, no round-trip). An explicit `Sync` is honoured: `None` → only `{Method}Async`, `Generate` → both. The LocalQuery async wrapper completes synchronously (`Task.FromResult`, still no RPC) — it lets a caller `await {Method}Async(...)` and keep compiling if the method later switches to a server-backed mode. Impl must return a non-`Task` value.

**Contract (generator enforces what it can):**
- Must return a value (no `void` / bare `Task`).
- Must not mutate State — divergence between clients would be permanent (server never sees the write).
- Must not call cross-entity services — those round-trip the server.
- Must not consume `Context.Random` — would advance scroll position only on the calling client.
- Requires the entity to be subscribed at call time.

**vs `Query`:** `Query` is server-roundtrip read for entities the caller has NOT subscribed to (authoritative source on server). `LocalQuery` reads the already-replicated client snapshot without RPC.

Pre-0.29.0 `Local` allowed client-only writes which was a divergence anti-pattern. Removed. UI-state mutations belong in a ViewModel / POCO outside SharedMeta.

### CrossOptimistic

**Split-profile mode.** When a player's data is split across multiple `ISharedState` entities owned by the same player, `CrossOptimistic` lets the client execute methods that touch more than one of those states locally without waiting for a server round-trip.

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

**Invariant:** the cross-call target must be owned by the same player as the caller — no other client and no independent server-side mutator writes to it concurrently. Two framework behaviors depend on this:

- `EntityGrain.HandleCallFromEntityAsync` excludes the originating caller from `DistributeBroadcasts` when the outer call's `IsCrossOptimistic` flag is true. The cross-call's effect on the target is inlined in the outer call's replay payload, so a duplicate broadcast back to the caller would double-apply.
- `SessionManagerGrain.ExecuteOneCallAsync` reserves the target's sequence slot for the cross-call via a `CrossCallSlotMarker` in `HeldBroadcasts`. If a concurrent third-party server-side write to the target slipped between our `KnownEntitySequence` and the cross-call's sequence, the marker waits in the gap so the intermediate broadcast can drain through normally instead of being silently dropped as old/duplicate.

**Do not** use `CrossOptimistic` against shared multi-writer entities (clans, lobbies, markets, two-player trades). Those need `Server` mode — the caller relies on broadcasts to learn the target's state change, and the cross-call's broadcast suppression here would block that.

### ServerPatch

Server-only execution. Instead of replay payload, server sends a state diff patch. Client applies patch directly.

**Use case:** Hotfixing server logic when clients can't be updated.

**Patch-tracking copy (0.24.2+):** to produce a diff the server runs a generated `{Impl}_PatchTracked` copy of the service where `State` is rebound to the typed `{State}PatchWrapper` — so ordinary `State.X = …` writes track without manual `PatchState`. The copy is auto-generated (decoupled from `DeepDesync`) for any **force-patch-able** service: a client-callable `Optimistic`/`Server`/`CrossOptimistic` method with `Version > MinCompatibleVersion`, or a `[MetaConfigStructureBoundary]` config, or any `ServerPatch` method. Generation is **per state, including siblings** — every service on a force-patch-able state gets the copy, and `ResolveSiblingByType` returns the sibling's copy under patch tracking, so a force-patched call fanning out to a sibling (`BuyEnergy → EnergyService.AddPurchasedEnergy`) tracks the sibling's mutations too. Opt out with `[MetaService(PatchTracking = false)]`: no copy, and force-patch clients are **rejected** at negotiation (method-level → `Rejected`) or subscribe (config-boundary → `FeatureRequirement`) instead of being served an empty patch. Bodies must be copy-compatible (mutate via `State`, wrapper-typed helpers, no `wrapper→raw` collection leaks — the type system enforces it; see *DeepDesync*).

### ServerReplace

Server-only execution. Server sends the full serialized state. Client replaces state wholesale.

**Use case:** When state is fully regenerated (map generation, full reset) and full state is smaller than a patch diff. `OnStateRefreshed` event fires on client.

```csharp
[MetaMethod(Mode = ExecutionMode.ServerReplace)]
void GenerateMap(int seed);
```

### Query Calls (No Subscription)

Lightweight read-only RPC to any entity without subscribing. No state sync, broadcasts, replay, or persistence.

```csharp
[MetaMethod(Mode = ExecutionMode.Query)]
Task<PlayerBriefInfo> GetBriefInfo();

[MetaMethod(Mode = ExecutionMode.Query, OpenAccess = true)]  // bypasses EntityAccessPolicy
Task<PlayerBriefInfo> GetPublicInfo();
```

**Client:** Generated `{Service}QueryApi` class with `entityId` bound at creation:
```csharp
// Create once
var profileQuery = new ProfileServiceQueryApi(connection, serializer);
// Per-entity proxy
var api = profileQuery.EntityApi("player-123");
var info = await api.GetBriefInfoAsync();
```

**Server route:** `SessionManager.QueryEntityAsync` → `EntityGrain.HandleQueryAsync` → `DispatchCall` (read-only).

- `Mode = ExecutionMode.Query` — callable without subscription, must return a value
- `OpenAccess = true` — skip EntityAccessPolicy check for public data
- Query methods are not generated in the regular ApiClient
- Cannot be overridden at runtime via `IExecutionModeProvider` — Query is structural, not routing
- Legacy `[MetaMethod(Query = true)]` bool is deprecated (`CS0618`); migrate to `Mode = ExecutionMode.Query`

### Signal Methods (Fire-and-Forget)

Sibling to Query but void return and no response on the wire. Used for heartbeat, telemetry, bridge-driven notifications.

```csharp
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Signal)]
    void NotifyHeartbeat(long clientTicks);
}
```

**Client:** generated `{Method}Signal(params)` — synchronous void, fires through `INetwork.SendSignalAsync`, returns immediately. No RequestId tracking, no auto-retry interaction, no connection-health impact.

```csharp
api.NotifyHeartbeatSignal(DateTime.UtcNow.Ticks);  // returns instantly
```

**Server route:** `Handler.SignalCallAsync` → `SessionManager.SignalEntityAsync` → `[OneWay] EntityGrain.HandleSignalAsync` → generated `{Service}SignalDispatcher.Dispatch` → impl method. Read-only: no sequence increment, no broadcasts, no persistence, no response. AccessPolicy is still enforced (same as regular methods). Errors are logged server-side and never propagated.

**`[ServerMetaService]` bridges** may be called from inside signal bodies — `ServerMetaContext.SignalMode` is flipped for the call, and Recorder's writes go into `NullServerRecordContext` (no replay payload is produced since there's nothing to replay).

**Constraints (validated via `#error`):**
- Return type must be `void`
- Cannot combine with `Query`, explicit `Mode`, `Sync`, `SkipServerOnFalse`, `ForcePersist`
- Cross-entity calls throw `NotSupportedException` — use `Mode = Server` for chained calls
- State mutations are a contract violation (like Query); compile-time check is planned but not yet enforced

**Transport shape:** InProcess dispatches directly to the grain; SignalR uses `HubConnection.SendAsync`; HttpPolling POSTs to `/meta-http/signal` and responds `202 Accepted` before execution completes.

### Calling a Service from Server Code — `{Service}ServerApi` — 0.35.0+

For server code that is not itself running inside a meta call: admin tooling, framework grains, background jobs. Generated per `[MetaService]` that declares a `StateType`.

```csharp
await grainFactory.GetServerApi<IProfileService>(playerId).AddResourcesAsync("gold", 500);
```

Routes through `IEntityGrainBase.HandleCallFromEntityAsync` — the same server-internal entry cross-entity calls use. The call is an ordinary dispatch: replay recording, broadcast to every subscriber, persistence, sequence advance. No subscriber is required; a cold entity activates, migrates and persists. `Mode = Notification` methods route to the `[OneWay]` entry instead and return a Task that completes on dispatch.

Errors surface as `InvalidOperationException` — a server-originated call has no client response channel to carry them.

**Admin methods:** declare them like any other meta method and add `GenerateClientApi = false`. The server API includes them precisely because clients can't reach them:

```csharp
[MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
void AddResources(string resource, int amount);
```

Authorization is the caller's responsibility — reaching this class already means running inside the silo. Emitted into shared projects behind `#if SHAREDMETA_SERVER` and into server projects from referenced assemblies.

### Inheriting a Service Contract — `[MetaMethod]` on the Implementation — 0.35.0+

Method ids and client broadcast replay are built per compilation from method **declarations**, so a `[MetaService]` interface that merely inherits a contract from a referenced assembly would generate nothing. Put `[MetaMethod]` on the implementing class instead and the method joins the service surface normally:

```csharp
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileLobbyService : IMetaService, ILobbyRequester { }   // empty

[MetaServiceImpl(typeof(IProfileLobbyService), typeof(ProfileState))]
public partial class ProfileLobbyService : IProfileLobbyService
{
    [MetaMethod(Mode = ExecutionMode.Notification)]
    public void OnMatchFound(MatchFoundEvent evt) { State.MatchId = evt.MatchId; }
}
```

The base interface still guarantees the signature: the class must implement the inherited member, so argument drift is a compile error, not a silent protocol break. Generated code calls through the interface-typed variable and the compiler resolves the inherited member.

### Notification Methods (Entity → Entity Fire-and-Forget) — 0.22.0+

Peer of Signal on the cross-entity axis — Signal is "client → entity, no wait", Notification is "entity → entity, no wait". Use when the caller doesn't need to block on the target's completion.

```csharp
[MetaService(StateType = typeof(ClanState))]
public interface IClanService : IMetaService
{
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
            GetIClanService(S.ClanId).AddPower(amount);  // void call, no await
        return Task.CompletedTask;
    }
}
```

**Generator emit:** EntityCaller method is `void {Name}(args)` — not `Task {Name}Async(args)`. Recorder fires `_context.CallEntityOneWay(...)` without recording a result. Replayer is a no-op for this call site (server didn't record anything).

**Server route:** caller-grain → `IEntityGrain.HandleCallFromEntityOneWayAsync` (marked Orleans `[OneWay]`) → `EntityGrain.HandleCallFromEntityAsync` internally → result discarded. Source grain doesn't await; target grain processes normally, broadcasts to its own subscribers, persists per impl method's `ForcePersist`.

**Constraints (validated via `#error`):**
- Return type must be `Task` or `void` — no `Task<T>` (no value to return)
- Implicit `GenerateClientApi = false` — clients never originate Notifications. Enforced on both sides since 0.35.0: no client API is generated, the dispatcher rejects a client-originated call, and negotiation marks the method `Rejected` while still mapping server→client so broadcasts replay. (Before 0.35.0 only the client-side suppression happened — a forged packet reached the body.)
- Cannot be overridden at runtime — structural trait

**Caller-side effects you lose:**
- Errors in the target are logged server-side, never propagated to caller
- Caller cannot observe target's state after the call (no observable order)
- No transactional consistency (if target fails, caller's mutation still committed)

**Use when:** caller doesn't read target state after the call AND target's broadcasts independently reach its subscribers. Textbook fit: `ProfileService.GainPoints → ClanService.AddPower` — profile never reads clan state, clan broadcasts power change to clan subscribers independently. Removes one grain-to-grain await from the latency path.

**Performance impact (ClanWars stress, 1000+1000 simulators, single dev machine):** cold-path RPCs (Connect, ResolveProfile, CreateClan) p99 dropped 8–21× after flipping `AddPower` to `Notification`. High-volume RPCs gained +20% throughput at the same p99 wall under CPU saturation.

### Runtime Execution Mode Override

Override the `[MetaMethod]` default at runtime without recompilation. 0.23.0+ keyed by `ushort MethodId` from the generator-emitted `{RootNamespace}.Generated.GameMethodIds` const table — string-keyed overloads were removed:

```csharp
var modeProvider = client.ModeProvider as ExecutionModeProvider;

// Override specific method (string overloads + SetServiceMode + LoadManifest gone in 0.23.0)
modeProvider.SetMode(GameMethodIds.IProfileService_SetName_v0, ExecutionMode.Server);

// Reset to attribute defaults
modeProvider.Clear();
```

**Priority:** specific method override → attribute default.

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

    [MetaMethod(Mode = ExecutionMode.LocalQuery)]
    int CardsInHand();   // client-side read over State; client calls api.CardsInHandSync()

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

Use `[MetaInit]` on a method in your `[MetaServiceImpl]` class to initialize or migrate state. Two signatures supported (generator picks by parameter count):

```csharp
[MetaInit] public Task<int> Init(int version) { ... }                  // legacy
[MetaInit] public Task<int> Init(int version, int target) { ... }      // 0.19.0+
```

The two-arg form pairs with `[MetaStateVersion]` migration breakpoints — `target` is the schema version this step wants to reach:

```csharp
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState))]
public partial class ProfileServiceImpl : IProfileService
{
    [MetaInit]
    public Task<int> InitState(int version, int target)
    {
        if (version < 1 && target >= 1) {
            State.Energy = Config.StartEnergy;   // Config pinned to 1.x branch
            State.Money  = Config.StartMoney;
        }
        if (version < 2 && target >= 2) {
            State.NewField = Config.NewFieldDefault;   // Config pinned to 2.0 transition
        }
        return Task.FromResult(Math.Max(version, target));
    }
}
```

**When `[MetaInit]` runs (changed in 0.19.0):**
- **Activation no longer drives migration.** First-time init and lazy migration are deferred to `SubscribeAsync` (subscriber-driven) or to the first RPC call (`HandleCallAsync` / `HandleQueryAsync`). Both paths cap migration to the connecting client's resolved config branch.
- For services with `[MetaInit]` but no `[MetaStateVersion]`, base init runs exactly once on first interaction.
- Returned version is saved to `EntityGrainState.Version`.
- Grain is **not** persisted after init alone — only when a player interacts (`_isDirty` guard).

**Available during `[MetaInit]`:**
- `Context.Random` / `Context.ServerRandom` — deterministic randomness
- `Config` — pinned to the appropriate branch for this step (see [Per-Client Config Branches & State Migration](#per-client-config-branches--state-migration))
- `Context.Version` / `Context.ConfigVersion` — current schema and config version (0.19.0+)
- `State` — entity state to mutate

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
- `CurrentVersion` — current published `MetaConfigVersion` (3-part `Major.Minor.Patch` since 0.19.0)
- `GetConfig(MetaConfigVersion version)` — return config for a specific version (called repeatedly during migration steps; cache it)
- `ResolveLatestMatching(int major, int minor)` — materialize an `x` capture in a `[MetaConfigVersion]` rule's `Patch` slot
- `GetDownloadUrl(MetaConfigVersion version)` — optional URL for client download

> **0.28.0**: `services.ConfigureConfigs(o => { o.UseBootstrapper<ConfigBootstrapper>(); o.Strategy = ConfigSeedStrategy.LoadIfNew; });`. `IConfigBootstrapper` methods are typed generic (`GetVersionAsync<TConfig>` / `GetBytesAsync<TConfig>`), dispatched per `[MetaConfig]` by the generator-emitted `GeneratedConfigCatalog : IConfigCatalog`. Wizard emits a project-side `ConfigBootstrapper.cs` with one typed `if (typeof(TConfig) == typeof(GameConfig))` branch per template config. Built-in `DirectoryConfigBootstrapper` (read-only `{root}/{Type.Name}/{M.m.p}.bin` scan) stays for image-baked delivery. Project pre-bootstrap work (YAML compile etc.) registers as its own `IHostedService` before `ConfigureConfigs`. `IConfigByteSource.Configs` + `ConfigTypeEntry` + `DefaultInstanceConfigBootstrapper` + `UseDefaultInstances` were removed — catalog replaces them. `IConfigRegistry` + `IConfigAdminGrain` ship typed `<TConfig>` extensions for compile-time-safe call sites; wire protocol stays string-based. Admin tools call `IConfigAdminGrain` directly from a connected Orleans client — operations: `ListConfigsAsync` / `GetConfigAsync` / `DownloadAsync` / `UploadAsync` / `UnpublishAsync` + client-version control (`Get` / `Set{Current,Min,Max}ClientVersionAsync`). `ClientVersionOptions` (`appsettings.json "ClientVersion"` section: `Current`/`Min`/`Max`/`Server`) seeds `ICurrentClientVersionGrain` + `MetaTransportOptions`; the auto-registered `DefaultClientVersionService` (`IConfigVersionResolver` + `IHostedService`) tracks runtime grain overrides via 30-s poll for cross-silo propagation.

> **Changed in 0.19.0**: config is no longer pinned per entity. `EntityGrainState.ConfigVersion` was removed (`Id(6)` is a tombstone). Config is resolved **per RPC call** from the connecting client's `RpcCall.CallerClientVersion`, so a single grain serves multiple branches. See [Per-Client Config Branches & State Migration](#per-client-config-branches--state-migration). `IConfigVersionResolver` is still supported for A/B tests on top of the resolved version.

Client access: `client.GetEntityConfig<GameConfig>(entityId)` — returns resolved config after subscribing.

#### Client config provider — required setup (0.17.0+)

The client materializes config via an `IClientMetaConfigProvider<TConfig>` registered on the resolver. **Failing to register one for any service that declares `ConfigType = typeof(X)` (without `DefaultConfig = true`) throws `InvalidOperationException` at the first subscribe.** Pre-0.17.0 the generator silently auto-registered `StaticConfigProvider<T>(new T())` for everyone — that hid wiring bugs and was removed; auto-register is now opt-in via `[MetaService(..., DefaultConfig = true)]`.

> **Critical mental model.** The server reports the `MetaConfigVersion` it pinned for the entity; the client provider then materialises that version. `StaticConfigProvider` **ignores the requested version and always returns the instance passed at construction** — it does NOT consult the server. Picking (A) freezes the client to build-time values; server-side config changes do NOT reach a (A)-only client without a new build. For live-ops, use (B) or (C).
>
> | Provider | Server can push a new config without rebuilding the client? | Offline / first-launch | Best for |
> |---|---|---|---|
> | `StaticConfigProvider` | **No** — version arg ignored, bundled instance always returned. | Always uses the bundled instance. | LocalBackend, single-player, bundled snapshot, tests. |
> | `DownloadingConfigProvider` | **Yes** — fetches bytes for the pinned version, optional disk cache. | Throws if no network and no cache hit. | Live-ops, balance-driven games, content-team-driven config. |
> | `CompositeConfigProvider(downloading, static)` | **Yes** on the primary path; falls back to bundled when primary throws. | Bundled snapshot when offline. | Shipping clients — default recommendation. |

Three built-in providers cover all flows:

```csharp
// (A) Preloaded instance — LocalBackend, single-player, bundled snapshot
client.Resolver.RegisterConfigProvider<GameConfig>(
    new StaticConfigProvider<GameConfig>(loadedConfig));

// (B) Server-pushed bytes — real-server live ops, with optional disk cache
client.Resolver.RegisterConfigProvider<GameConfig>(new DownloadingConfigProvider<GameConfig>(
    urlResolver: client.ConfigDownloadUrlResolver,      // keyed by config type, not state type
    downloader:  UnityConfigDownloader.DownloadAsync,   // Unity-friendly UnityWebRequest
    serializer:  client.Serializer,
    cache:       new FileConfigCache<GameConfig>(cacheDir, client.Serializer)));

// (C) Composite — try server, fall back to bundled when offline
client.Resolver.RegisterConfigProvider<GameConfig>(new CompositeConfigProvider<GameConfig>(
    primary:  new DownloadingConfigProvider<GameConfig>(/* ...as in (B)... */),
    fallback: new StaticConfigProvider<GameConfig>(bundledSnapshot)));
```

`RegisterConfigProvider<T>` (without "Try") clobbers any auto-emitted default — order relative to `client.Resolver.RegisterAllServices()` doesn't matter. One provider per `TConfig` type covers all services that share that config.

#### Server-side download endpoint (0.26.2+)

`DownloadingConfigProvider` on the client asks the server for a URL per version via `GetConfigDownloadUrl`; the host must answer **and** serve bytes at that URL. Two paired helpers in `SharedMeta.Server` (from `CoreGame.SharedMeta.Transport.SignalR`) drop ~25 lines of boilerplate:

```csharp
// ASP.NET DI — NOT inside siloBuilder.ConfigureServices(...)
builder.Services.AddMetaConfigPublicUrl(publicBaseUrl, routePrefix: "/meta/config");
app.MapMetaConfigDownload();   // non-generic — recommended; serves every [MetaConfig]
```

`AddMetaConfigPublicUrl` registers an `IConfigDownloadUrlResolver` that emits `{publicBaseUrl}{routePrefix}/{configType}/{major}.{minor}.{patch}`. The non-generic `MapMetaConfigDownload()` uses the generator-emitted `IConfigByteSource` (auto-routing `configType` → right `IMetaConfigProvider<TConfig>`), so adding a new `[MetaConfig]` type later requires zero endpoint changes — just `AddSharedMetaConfigProvider<NewConfig>()` on `builder.Services`.

**Generic overload** `MapMetaConfigDownload<TConfig>(routePrefix)` is kept for single-config dedicated routes; multiple generic calls require distinct route prefixes to avoid collisions. Prefer the non-generic form for typical multi-config setups.

**DI-container boundary trap.** `MetaHub` and minimal endpoints resolve from **ASP.NET DI** (`builder.Services`). The silo's `services.ConfigureMeta(svc => ...)` block populates the **Orleans silo container** — invisible to ASP.NET-side resolution. If you need the same provider on both sides, register on both: `builder.Services.AddSharedMetaConfigProvider<T>()` AND inside `ConfigureMeta(...)`.

**0.26.2 unblocks host-overrides for free.** Generator-emitted `GeneratedConfigDownloadUrlResolver` is now registered with `TryAddSingleton`, so a host-side `AddSingleton<IConfigDownloadUrlResolver>(...)` wins without `RemoveAll`/ordering tricks. Pre-0.26.2 hosts that customized the resolver had to `services.RemoveAll<IConfigDownloadUrlResolver>()` first.

Set `DefaultConfig = true` on `[MetaService]` only when `new TConfig()` produces gameplay-correct fallback values; for typical configs (item registries, balance numbers, level data) leave it off and register an explicit provider.

### Multiple Configs (`[ServiceConfig]`) — 0.33.0+

A service can declare N configs, each independently versioned/published (own
`IMetaConfigProvider<TConfig>` / `IClientMetaConfigProvider<TConfig>`, own `[MetaConfigVersion]`
rules) — all symmetric, no privileged "primary". Unlike multi-config siblings
(`Get{Iface}SiblingAsync()`) or `[StatelessMetaService]`'s Path 1, these resolve
**synchronously in every execution mode**, including `Optimistic`/`CrossOptimistic` where the
client also executes the method body predictively. A service can declare zero, one, or many.

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

Positional: declaration order = wire/storage index — same deliberate-reseed-on-reorder contract
`[NamedRandom]` already has. Generator emits one named typed accessor per entry:
`protected {TConfig} {Name} => ({TConfig})Context.Configs![i]!;`.

**Legacy `[MetaService(ConfigType=...)]` / `Context.Config`:** `[Obsolete]` (compiler warning)
but fully functional — existing services keep working unchanged, no forced migration. When a
service declares both the legacy `ConfigType` and one or more `[ServiceConfig]` entries (a
migration-in-progress mix), the legacy config occupies wire index 0 and `[ServiceConfig]`
entries follow at the remaining indices; `Context.Config`/`Context.Configs` are independent
lists. New services should use `[ServiceConfig]` exclusively.

**Wire shape:** the framework carries config versions as a **list**, not a scalar — every
per-op / subscribe DTO (`MetaOperation.ExecutedConfigVersions`, `CallResponse<T>` family,
`SubscribeResponse`/`SessionConnectResponse`'s `ConfigVersions`) is `List<MetaConfigVersion>`
(index 0 = legacy config when declared). This replaced the pre-0.33 single-scalar
`ExecutedConfigVersion` / `ConfigMajor/Minor/PatchVersion` triple outright — a wire-breaking
change, not additive.

**Full parity with the legacy primary** — `[ServiceConfig]` entries get the same mechanics the
legacy `ConfigType` path has:
- **Pin support** — `GetCachedServiceConfigsForClient`/`GetCachedServiceConfigVersionsForClient`
  check `TryGetConfigPin` first (set by the generated `EstablishConfigPinsFromClientVersion`
  override, which now loops declared `[ServiceConfig]` types the same way it already loops the
  legacy primary and migration-only secondaries).
- **`[EntityScope(Global)]` substitution** — both methods substitute
  `IConfigVersionResolver.CurrentClientVersion` for Global-scope states, mirroring
  `GetCachedConfigForClient`'s Global branch.
- **`[MetaStateVersion]` schema-floor migration** — `GetSchemaFloorServiceConfigsForClient`/
  `GetSchemaFloorServiceConfigVersionsForClient` (per-type, parallel to
  `GetSchemaFloorConfig`/`Version` for the legacy primary) feed `[NoMigrate]` calls; `RunInitAsync`
  resolves `[MetaInit]` steps for [ServiceConfig]-linked conditions the same way it already does
  for the primary/secondary; `IsClientConfigCompatible`/`ComputeSchemaCapForClient` fold every
  declared type into the AND-gate, not just the primary's.
- **Cross-entity propagation** — `ICrossEntityResolver.GetEntityConfigs` (the `Configs`
  counterpart to `GetEntityConfig`) is threaded into the generated `LocalEntityCaller`'s
  `CrossOptimisticMetaContext`, so cross-entity/`CrossOptimistic` calls see the target's
  `[ServiceConfig]` entries, not just its legacy `Config`.
- **Multi-config sibling resolution** — the generated `[ServiceConfig]` accessor has a settable
  backing field (mirroring the legacy `Config` property's `_config ?? Context.Config` shape);
  `Get{Iface}SiblingAsync()` resolves each sibling's own declared entries independently and pins
  them to that instance's field, without touching the caller's shared `Context`.

**Known limitation:** when multiple services share one state and declare *different*
`[ServiceConfig]` sets, they're aggregated into one list on the shared `Context.Configs`
(state-wide collapsing) — same simplification the legacy `ConfigType` already uses for
multi-service states (`services.Select(s => s.ConfigTypeFullName).FirstOrDefault(...)`).

### Per-Client Config Branches & State Migration

> **Added in 0.19.0**: route each connecting client to its own config branch and migrate entity state schema gradually as live config advances. Without this, a 1.x client connecting after the server published 2.0 either gets locked out or sees a state shape it can't reason about.

**Three attributes drive the system. Use them together.**

#### `[MetaConfigVersion]` — client → config routing (on the config class)

```csharp
[MetaConfig(Default = true)]
[MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]   // 1.x clients → 1.x configs
[MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]   // 2.x clients → 2.x configs
public partial class GameConfig { … }
```

**Pattern grammar** (`Major.Minor.Patch`):
- `2.0.5` — literal, exact match
- `x` — capture, propagates from `Client` to `Config` (so `1.x.*` → `1.x.*` routes 1.5.0 → 1.5.* and 1.6.2 → 1.6.* with one rule)
- `2.2+` — range, matches 2.2 or higher within the same major
- `*` — wildcard (terminal, no propagation)

Resolution picks most-specific (literal > capture > range > wildcard). Multiple attributes allowed. The framework calls `IMetaConfigProvider.ResolveLatestMatching(major, minor)` to materialize `x` captures in the `Patch` slot.

#### `[MetaStateVersion]` — schema migration breakpoints (on the state class)

```csharp
[SharedState]
[MetaStateVersion(2, "2.0", typeof(GameConfig))]   // schema 2 needs GameConfig >= 2.0
[MetaStateVersion(3, "3.0", typeof(GameConfig))]   // schema 3 needs GameConfig >= 3.0
public partial class ProfileState : ISharedState { … }
```

Multiple `[MetaStateVersion(N, ...)]` with the same `N` form an **AND gate** (e.g. schema 3 needs `GameConfig >= 3.1` AND `SeasonConfig >= 1.4`).

The framework runs `[MetaInit]` once per applicable step, with `Context.Config` pinned to that step's transition version (not the latest). Sequential migrations get one call per unprocessed step in order.

#### `[NoMigrate]` and `[MinStateVersion(N)]` — per-method control

```csharp
[MetaMethod(Mode = ExecutionMode.Server)]
[NoMigrate]
void DepositGift(GiftItem item);              // skip lazy migration; pin Config to schema-floor

[MetaMethod(Mode = ExecutionMode.Server)]
[MinStateVersion(2)]
void UseSeasonalAbility(int abilityId);       // cap migration target at schema 2
```

- **`[NoMigrate]`** — method skips lazy migration entirely. Use for cross-entity "administrative" calls (gift sending) where forcing the recipient to upgrade is wrong. Method body must be schema-tolerant.
- **`[MinStateVersion(N)]`** — caps migration at N. If state < N, migrate up to N and stop; if state ≥ N, no migration runs.

#### When migration runs

| Entry point | Cap source |
|---|---|
| `EntityGrain.SubscribeAsync` | `ComputeSchemaCapForClient(clientVersion)` |
| `MetaProviderBase.HandleCallAsync` | `min(method's [MinStateVersion], ComputeSchemaCapForClient(call.CallerClientVersion))` |
| `MetaProviderBase.HandleQueryAsync` | same |
| `OnActivateAsync` | **not driven by activation** — only loads persisted state |

A 1.x client subscribing to a fresh entity gets schema 1 (base init only). A 2.x client subscribing later triggers lazy migration to schema 2. Cross-entity calls propagate the originating client's version through `MetaContext.CallerClientVersion` → next entity's `RpcCall.CallerClientVersion`, so the migration cap follows the chain.

The per-entity `IsClientConfigCompatible` gate rejects subscribes from clients whose resolved config branch can't satisfy the entity's persisted schema (clear error message, "your app version is too old for this entity's current state").

#### `MaxClientVersion` + downgrade tracking

`MetaTransportOptions.MaxClientVersion` bounds the supported client range. `IPlayerVersionGrain` records the highest version a player has connected with — subsequent connects from a *lower* version are rejected.

```csharp
builder.Services.AddSingleton(new MetaTransportOptions
{
    ServerVersion    = "2.0.0",
    MinClientVersion = "1.1.0",
    MaxClientVersion = "2.0.*",
    RequireAuthentication = true,
});
```

### Entity Scope (`[EntityScope]`) — 0.21.0+

Declares the sharing model of an entity on its state class. The framework derives subscribe rules, runtime config-version pinning, and dispatch behaviour from this single attribute. Default (no attribute) = `Private`.

```csharp
[SharedState]
[EntityScope(EntityScope.Private)]   // default — owner only
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
| `Private` | Owner only (others may cross-entity-call without subscribing) | Established on owner's first connect; survives grain's active lifetime; dropped on Orleans idle-deactivation | From pin | Safe |
| `Shared` | First subscriber pins; subsequent joiners validated against it (patch downgrade OK; `Major.Minor` mismatch rejects via `EntityAccessDeniedException`) | First subscriber's resolved versions | From pin | Safe |
| `Global` | Open subscribe gated on `IsClientConfigCompatible` | **Never pinned** — resolved fresh from `IConfigVersionResolver.CurrentClientVersion` on every call (throws if not configured) | Always under `CurrentClientVersion`-resolved version | **Not safe** under mid-session config rollout — use `Server` / `ServerPatch` / `ServerReplace` / `Query` / `Signal` / `Local` |

The pin is *runtime grain state*, not persisted. Idle-deactivation drops it; next first-subscriber re-establishes from scratch (Shared session "next day" naturally picks up newer configs and migrates state forward via `[MetaStateVersion]`).

**Cold calls into a deactivated Private entity** fall back to project policy via `IConfigVersionResolver` (returns `default(MetaConfigVersion)` if not registered, transitional permissive behaviour pending strict-throw follow-up).

**Admin force-migrate (0.21.0+):** drop support for an old config branch by sweeping entity IDs (from your player DB / storage) and calling `entityGrain.ForceMigrateToFloorAsync("3.0.0")` on each. Runs the full `[MetaStateVersion]` migration ladder up to the floor's required schema and persists. No subscriber required.

### `IConfigVersionResolver` (0.21.0+)

Required in DI when any `IMetaConfigProvider<>` is registered or any state declares `[EntityScope(EntityScope.Global)]`:

```csharp
public class MyResolver : IConfigVersionResolver
{
    public string CurrentClientVersion => "2.0.0";   // default for server-internal callers + Global entities
    public MetaConfigVersion ResolveVersion(string stateTypeName, string entityId, MetaConfigVersion defaultVersion)
        => defaultVersion;                            // override for A/B tests / staged rollouts
}
services.AddSingleton<IConfigVersionResolver>(new MyResolver());
```

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
- `Context.Version` — current state schema version (during a migration step, the source version of the step) — 0.19.0+
- `Context.ConfigVersion` — `MetaConfigVersion` matching `Context.Config` — 0.19.0+
- `Context.CallerClientVersion` — originating client app version; propagated across cross-entity boundaries — 0.19.0+

### Async Rules

- Service interface methods can be `void`, return a value, or return `Task`/`Task<T>` for CrossOptimistic
- Service implementations are synchronous (modify state directly)
- Server-side provider methods (`HandleCallAsync`) are always async

---

## Cross-Entity Calls

Declare the target service as a dependency in `[MetaServiceImpl]` — the source generator
injects a typed `GetI{Service}(entityId)` method into the service's partial class. **This
is the only supported way to call another entity from a meta method.**

```csharp
[MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), typeof(IProfileService))]
public partial class ExpeditionService : IExpeditionService
{
    // Generator injects: GetIProfileService(string entityId) into this partial class.

    public async Task<MoveResult> Move(int dx, int dy)
    {
        var profile = GetIProfileService(State.ProfileEntityId!);
        bool spent = await profile.SpendEnergyAsync(Config.MoveCost);
        if (!spent) return MoveResult.NoEnergy;
        // ...
    }
}
```

**On server:** `MetaProviderBase.EntityCallHandler` resolves target grain, calls `HandleCallFromEntityAsync`. Target entity executes, broadcasts to ITS subscribers, returns result.

**On client (CrossOptimistic):** Uses `CrossOptimisticMetaContext<TState>` for local execution on cached target state.

**Broadcast Suppression:** When Entity A calls Entity B, SessionManager prevents duplicate broadcasts for players subscribed to both.

> **Do not use `Context.GetEntityApi<T>(id)`.** That method no longer exists on `MetaContext` (removed in 0.12.4). Cross-entity access goes strictly through declared dependencies and the generated `GetI{Service}` accessor — the typed name makes the dependency explicit in `[MetaServiceImpl]`, which is required for the generator to wire up real/replay/cross-optimistic routing.

### Sibling-Service Calls (0.20.0)

A "sibling" is another `[MetaServiceImpl]` hosted on the same `TState` — both impls live in the same entity grain. Sibling calls dispatch in-process (typed C# call, no serialization, no grain RPC). Both implicit and explicit getters are available; the implicit one fixes the gift-to-self deadlock that pre-0.20.0 versions had.

```csharp
// Implicit — same getter as cross-entity, but with self-detect
public async Task SendGift(string targetEntityId, int itemId) {
    // Self-id → in-process sibling call. Other id → real cross-grain RPC. Same code.
    await GetIInventoryService(targetEntityId).GrantItemAsync(itemId);
}

// Explicit — typed sibling on this entity, returns the original interface
public async Task ApplyDailyBonus() {
    var inv = await GetIInventoryServiceSiblingAsync();  // resolves typed Config async
    inv.GrantItem("daily_bonus", 1);
}
```

**Preserved across sibling boundary:** state, randoms, `PatchWrapper`, `ChangeTracker`, by-reference args.
**Not preserved:** `[Transformer]` Box/Unbox (serialization-boundary concern, skipped on in-process call), implicit rollback on exception (sibling shares outer's mutation pipeline).
**Multi-config siblings:** different `[MetaConfig]` types on the same state are supported via `Get{Iface}SiblingAsync()`. The async getter resolves each service's typed Config through its own `IMetaConfigProvider<TConfig>`. Direct dispatch of a secondary-config service falls back to `Context.Config` (primary type) and crashes — secondary services should always go through the sibling-async accessor.
**Required:** every dep declared in `[MetaServiceImpl(..., typeof(IDep))]` MUST carry `[MetaService(StateType = typeof(...))]` on the dep interface — generator emits `#error` if missing.
**Hiding sibling-only / cross-entity-only methods from clients:** add `[MetaMethod(..., GenerateClientApi = false)]`. 0.20.0 enforces this server-side: forged client RPCs (modified clients crafting packets that bypass the un-generated API) are rejected at `EntityGrain.HandleCallAsync` / `HandleQueryAsync` / `HandleSignalAsync` via the generated `IsClientCallable` override. `HandleCallFromEntityAsync` (cross-entity) and sibling-bypass paths are server-internal and remain available.

### Read-Only State Access

Read another entity's state without calling a method on it:

```csharp
var otherState = await Context.GetState<ShardState>("shard_north");
if (otherState != null)
{
    var borderTiles = otherState.SouthBorder;
}
```

- System method on `MetaContext` — no dependency injection needed
- Server: calls `[AlwaysInterleave]` grain method (deadlock-safe), records bytes for replay
- Client: reads pre-recorded bytes from replay payload (deterministic)
- Returns `null` if entity type is unknown
- Not supported in `CrossOptimistic` mode

---

## Server-Only Services (`[ServerMetaService]`)

Bridge to the server-only world: non-deterministic sources (RNG, wall clock, external HTTP), Orleans grain-to-grain calls (lobby, matchmaker, map allocator). **Not** an entity — no `[SharedState]`, no subscribe, no wire API, no dispatcher. The server executes the real call, the Recorder writes the return value into the replay payload, the client-side Replayer reads it back during replay instead of re-executing.

### Pattern (mandatory shape)

```csharp
// Shared — interface
[ServerMetaService]
public interface IMapManager
{
    Task<string> RequestMap(MapRequest request);
}

// Server project — plain POCO, NO [MetaServiceImpl]
public class MapManager : IMapManager
{
    private readonly IGrainFactory _grainFactory;
    public MapManager(IGrainFactory gf) => _grainFactory = gf;
    public Task<string> RequestMap(MapRequest r) =>
        _grainFactory.GetGrain<IMapAllocatorGrain>(0).AllocateAsync(r);
}

// Server DI
services.AddTransient<IMapManager, MapManager>();

// Consumer — declare as dependency
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState), typeof(IMapManager))]
public partial class ProfileService : IProfileService
{
    public async Task<string> JoinMap(JoinMapRequest r)
    {
        var mapId = await Context.MapManager.RequestMap(new MapRequest { ... });
        State.CurrentMapId = mapId;
        return mapId;
    }
}
```

### `[ServerMetaService]` vs `[MetaService]` — when to pick which

| Question                                | `[MetaService]` | `[ServerMetaService]` |
|-----------------------------------------|-----------------|-----------------------|
| Has its own `[SharedState]`?            | Yes             | No                    |
| Client can subscribe / receive broadcasts? | Yes          | No                    |
| Client-callable over the wire?          | Yes             | No                    |
| Impl class has `[MetaServiceImpl]`?     | Yes (required)  | No (plain class)      |
| How is it consumed from a meta method?  | Declared as dependency in `[MetaServiceImpl(..., typeof(IT))]`; used as `GetIT(entityId)` | Declared as dependency in `[MetaServiceImpl(..., typeof(IBridge))]`; used as `Context.Bridge` |
| Generated code                          | `{Iface}Dispatcher.g.cs` + `{Iface}ApiClient.g.cs` | `{Iface}Recorder.g.cs` + `{Iface}Replayer.g.cs` |

### Anti-pattern — DO NOT DO THIS

```csharp
// ❌ Category error — the generator emits a #error naming the class
[ServerMetaService]
public interface IMapManager { ... }

[MetaServiceImpl(typeof(IMapManager), typeof(MapManagerState))]  // wrong
public class MapManager : IMapManager { ... }
```

`[ServerMetaService]` says "no state, no dispatcher, bridge only". `[MetaServiceImpl(..., stateType)]` says "state-ful entity service". Pick one:
- **Bridge**: drop `[MetaServiceImpl]`, make impl a plain class, persist real state in Orleans grains
- **Entity**: drop `[ServerMetaService]`, switch interface to `[MetaService(StateType = ..., AccessPolicy = ...)]` (mind that clients can then call it directly — choose access policy)

### Checklist for `[ServerMetaService]`

- [ ] Return value is authoritative on the server
- [ ] No client subscribe / broadcast needed
- [ ] No `[SharedState]` representing this service's data
- [ ] Impl is a plain POCO in server DI
- [ ] All callers use `Mode = Server` (Recorder output is only populated on server execution)

Call-order contract: replay is positional. Callers must make the same sequence of bridge calls on both sides — generally automatic for deterministic method bodies, but avoid non-deterministic branching (e.g. `DateTime.Now`) before bridge calls.

---

## Stateless Meta Services (`[StatelessMetaService]`) — 0.33.0+

A service with **no entity, no state** whose resolution requires only materializing a linked `[MetaConfig]` — e.g. a pricing/formula service backed by a balance config. Different from `[ServerMetaService]`: a stateless service IS client-callable (via two paths below), just never entity-bound.

### Pattern

```csharp
[MetaConfig]
public class ShopConfig { public int BaseCost { get; set; } = 42; }

[StatelessMetaService(typeof(ShopConfig))]
public interface IPricingService
{
    int ComputeCost(int quantity);
}

// Impl gets ONLY a typed Config property — no Context, no Random, no ServerTimeTicks,
// no dependencies. Pure function of (Config, method args) by design.
[StatelessMetaServiceImpl(typeof(IPricingService))]
public partial class PricingService : IPricingService
{
    public int ComputeCost(int quantity) => Config.BaseCost * quantity;
}
```

### Path 1 — resolve from inside any `[MetaServiceImpl]` (sibling-like)

Declare it as a dependency; the generator emits `GetIPricingServiceAsync()`:

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

**Server-only** — same constraint as multi-config sibling resolution (`Get{Iface}SiblingAsync()`): config materialization is async DI-backed (`IMetaConfigProvider<TConfig>.ResolveForClient` + `GetConfigAsync`) and cannot run during client-side synchronous replay. Only callable from methods running `Mode = Server` / `ServerReplace` / `ServerPatch` — calling it from an `Optimistic`/`CrossOptimistic` method throws `NotSupportedException` on the client.

### Path 2 — resolve directly from `MetaClient`, no entity subscribe

```csharp
IPricingService pricing = await client.GetIPricingServiceAsync();
int cost = pricing.ComputeCost(qty);
```

No entity to pin a `MetaConfigVersion` from, so this path resolves the version via a lightweight non-entity RPC (`IConnection.ResolveStatelessConfigVersionAsync`, mirrors `GetConfigDownloadUrlAsync`), then materializes it through the registered `IClientMetaConfigProvider<TConfig>` (same Static/Downloading/Composite providers as everything else). Requires the host to opt in server-side:

```csharp
// ASP.NET DI — pairs with app.MapMetaConfigDownload() / AddMetaConfigPublicUrl
builder.Services.AddGeneratedStatelessConfigVersionSource();
```

Only implemented for SignalR and HttpPolling transports (client + server). Unity BestHttp variants and the Debug Mux/InProcess transports fall back to `IConnection`'s default, which throws `NotSupportedException` rather than silently no-op'ing.

### Checklist for `[StatelessMetaService]`

- [ ] No `[SharedState]`, no entity, no subscribe
- [ ] Impl class is `partial` and carries `[StatelessMetaServiceImpl(typeof(IThing))]`
- [ ] Interface carries `[StatelessMetaService(typeof(TConfig))]` — ConfigType is required
- [ ] Every caller of Path 1's `GetI{Iface}Async()` runs under `Mode = Server/ServerReplace/ServerPatch`
- [ ] For Path 2, host called `services.AddGeneratedStatelessConfigVersionSource()` and registered an `IClientMetaConfigProvider<TConfig>` client-side

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

### Framework Contracts (Lobby / Matchmaking)

`ILobbyListener` is an ordinary interface — nothing about it is special-cased by the
dispatcher. Wire it up in two places:

```csharp
// 1. Inherit on the service interface — dispatch and the generated APIs are typed on this.
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileService : IMetaService, ILobbyListener { }

// 2. [MetaMethod] on the implementation. The declarations live in the framework assembly and
//    carry no syntax here, so the attribute has to sit on the impl — that is what assigns a
//    method id, emits the dispatcher case and enables client-side replay.
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState))]
public partial class ProfileService : IProfileService
{
    [MetaMethod(Alias = "OnMatchFound", Mode = ExecutionMode.Server, GenerateClientApi = false)]
    public void OnMatchFound(MatchFoundEvent e) => State.CurrentGameId = e.MatchId;
}
```

`Notification` makes them server-originated only — no client API, and the dispatcher rejects a
client packet carrying their id.

`ILobbyListener` carries `[MetaServiceContract]`, so the generator emits the awaitable mirror
`ILobbyListenerServerApi` into the contract's assembly — that is how `LobbyGrain` calls a
service it cannot name:

```csharp
// entity id is an argument, so one injected instance serves every target
public LobbyGrain(ILogger<LobbyGrain> logger, ILobbyListenerServerApi? players = null) { ... }
await _players.OnMatchFoundAsync(entityId, evt);
```

The implementation is generated into the assembly of the service that inherits the contract and
registered in DI (declare it nullable — a game need not use it). Delivery is a normal dispatch
(state, broadcast, persistence, replay) and works on a cold entity — an offline player finds the
result waiting on next subscribe.

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

## Push-Based Change Tracking

Push-based change tracking for UI binding. Client-only — `ChangeTracker` is null on server (zero overhead).

**Tracked fields** — add `[Tracked]` to private backing fields:
```csharp
[MemoryPackable]
public partial class GameState : ISharedState
{
    [Key(0), MemoryPackOrder(0), MemoryPackInclude, Tracked] private int _gold;
    [Key(1), MemoryPackOrder(1), MemoryPackInclude, Tracked] private int _health = 100;
}
// Generator creates: public int Gold { get; set; } with tracking setter
// No formatter registration needed — backing field serializes directly as T
// MessagePack: use [MessagePackObject(true)] (AllowPrivate) when class has [Tracked] fields
```

Generated: `TrackingProperty` enum, `TrackedGameState` static subscription class, partial class with tracking properties.

**Subscribing to changes:**
```csharp
TrackedGameState.Register();  // once at startup
TrackedGameState.OnChanged += args =>
{
    var leaf = args.FindLeaf((int)TrackingProperty.GameState_Health);
    if (leaf != null) healthBar.value = leaf.Value.NewValue.IntValue;
};
```

**How it works:** `ChangeTracker.Activate()` → generated property setters call `RecordFieldChange` → `FlushAndNotify()` walks tree, notifies subscribers, returns pooled list. Changes stored as `ChangeNode` structs in pooled flat list (tree via indices). `ChangeValue` avoids boxing for int/long/float/double/bool/string.

**OnStateMutated event:** Generated API clients fire `OnStateMutated` after every state mutation (Optimistic / CrossOptimistic / Server / ServerPatch / ServerReplace local execution, incoming broadcasts including foreign-service ones, subscriber events, reconnect). Sourced from `EntityStateContainer.OnMutated` since 0.14.0 — fires on every API client subscribed to the entity in lock-step. Use as a push-based "state changed" signal: `api.OnStateMutated += () => UpdateUI(api.State);`. For polling, see `MutationCount`.

**MutationCount property (0.13.1+, redesigned 0.14.0):** Generated API clients expose `int MutationCount` — local counter bumped on every state-mutating op (Optimistic, CrossOptimistic, Server, ServerPatch, ServerReplace, broadcast, subscriber-event broadcast, reconnect). **Shared per entity since 0.14.0** — every API client on the same entity returns the same value, sourced from `EntityStateContainer<TState>.MutationCount`. Polling: `if (api.MutationCount != lastSeen) { lastSeen = api.MutationCount; Invalidate(); }`. For polling without an ApiClient: `resolver.GetStateContainer<TState>(entityId)`.

**Multi-service-on-entity (0.14.0+):** Multiple `[MetaService]` interfaces can target the same `ISharedState`. All API clients on an entity share one `EntityStateContainer<TState>` — `apiInventory.State` and `apiShop.State` are the same instance, even after `ServerReplace` swaps the wholesale state. Foreign-service broadcasts (a method on a different service that targets the same state) update local state via the entity-level handler in `MetaServiceResolver` for ALL execution modes via three paths: **`ServerReplace`** → wholesale `IEntityStateContainer.ReplaceObject(newState)`; **`ServerPatch`** → `MetaServiceConfig.PatchApplier` wrapped in an active `ChangeTracker` so `[Tracked]` setters notify; **`Optimistic`/`Server`/`CrossOptimistic`** → `MetaServiceConfig.EntityReplayDispatcher` spins up the foreign service's impl class, sets up `ClientMetaContext` with replay context, activates `ChangeTracker`, runs the method, `FlushAndNotify`s. All paths bump `MutationCount` and fire `OnStateMutated`; ServerPatch and replay also fire `Tracked{State}.OnChanged`. **No matching ApiClient required on the receiver.** Caveat: methods that make cross-entity calls inside still need the matching service subscribed locally — entity-level dispatch doesn't chase cross-entity records.

**Service error handling:** Generated API clients catch exceptions during shared method execution at the framework level. On exception: (1) log via `MetaLog.Error`, (2) set `HasError = true` / `ErrorException`, (3) fire `OnServiceError` event, (4) re-throw. Subsequent calls throw `ServiceErrorStateException` until `ClearError()` or reconnect. Subscribe: `api.OnServiceError += (svc, ex) => Debug.LogError(ex);`

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
// Auto — Vector3Transformer is discovered from the compilation
[MetaMethod]
void Step(Vector3 delta);

[MetaMethod]
void Move([Transform(typeof(Vector3Transformer))] Vector3 position);

[MetaMethod]
void RawMove([SkipTransform] Vector3 position); // No transformation
```

No registration call — the generator discovers `[Transformer]` classes at compile time and both
ends of the wire derive the same decision from the same compilation. A transformer must be visible
to the assembly declaring the `[MetaService]` interface, be a non-generic class with a public
parameterless constructor, and not carry `NoAutoRegister` or `UseResolver`.

A transformed argument travels as its boxed type in exactly one wire member. Before running the
method locally the client substitutes `Unbox(Box(arg))`, so the body sees the same object the
server will see — for a state-aware transformer, the one resolved out of local state. `LocalQuery`
and sibling-bypass never serialize, so transformers do not run there.

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
3. When match forms: awaits `_players.OnMatchFoundAsync(entityId, evt)` per player
4. That dispatches `OnMatchFound` on the profile entity, updating state with match info
5. All subscribers receive the match notification as a broadcast

**Client-side notification:**
```csharp
client.Resolver.OnMethodReplayed<MatchFoundEvent>(
    profileEntityId, GameMethodIds.IProfileService_OnMatchFound_v0,
    e => Console.WriteLine($"Match found! ID: {e.MatchId}")
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

**UserOwned** generates no-arg convenience (`Get{ServiceName}Async()`, auto PlayerId); Open/Authorized/OwnerOnly generate the entityId-arg form (`Get{ServiceName}Async(entityId)`). Generic `GetServiceAsync<TApiClient>(entityId)` always works.

**`TryGetService` (0.29.3+) — synchronous, allocation-free hot-path accessor.** `GetServiceAsync` allocates a `Task` per call even when already subscribed. For frame-critical reads use `bool TryGetService<TApiClient>(entityId, out api)` (on resolver / `MetaClient`) — returns the cached client when the entity is subscribed AND the client was already created by a prior `GetServiceAsync`, else false (no subscribe, no throw, no alloc; hot path = lock + 2 dict lookups). Generated typed convenience mirrors `Get{Service}Async`: `TryGet{Service}(out api)` (UserOwned) / `TryGet{Service}(entityId, out api)` (others). Combine with a synchronous `LocalQuery` read (`{Method}Sync()`) for a zero-alloc per-frame path.

**UserOwned service** — entityId is always the player's own ID:
```csharp
// Generated convenience method (no entityId needed):
var profileApi = await client.GetProfileServiceAsync();
var profileState = client.GetProfileState();

// Hot path (already subscribed): no Task allocation
if (client.TryGetProfileService(out var p))
    var n = p.CardsInHandSync();   // sync LocalQuery → zero-alloc
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

// Client (cross-platform — works on Unity and .NET)
var login = await MetaAuth.LoginAsync($"{serverUrl}/meta/auth", deviceId: "unique-device-id");
var connection = new SignalRConnection($"{serverUrl}/meta", accessToken: login.Token);
var client = new MetaClient(connection, serializer, new MetaClientOptions { PlayerId = login.PlayerId });

// With token caching (reuse token across sessions)
// Unity; pass deviceId so multi-instance / random-deviceId dev builds get isolated token slots.
// Implement ITokenStorage for other platforms.
ITokenStorage storage = new PlayerPrefsTokenStorage(deviceId);
var login = await MetaAuth.EnsureAuthenticatedAsync($"{serverUrl}/meta/auth", deviceId, storage);
MetaAuth.ClearToken(storage); // logout

// Reset device binding (0.10.1+) — force-unlinks deviceId from current player.
// Next login creates a new player profile. Works even when device is the only auth key.
await MetaAuth.ResetDeviceAsync($"{serverUrl}/meta/auth", deviceId, accessToken, storage);
```

**Unity**: `UnityMetaAuth` auto-registers via `[RuntimeInitializeOnLoadMethod]` — sets `MetaAuth.LoginFunc` to `UnityWebRequest` implementation. Unity-dependent code (`PlayerPrefsTokenStorage`, `UnityMetaAuth`) is in `SharedMeta.Auth.Client` asmdef (`noEngineReferences: false`).

### Refresh tokens (0.30.0+)

Login returns a long-lived **refresh token** with the short access JWT. Server stores active sessions in a per-player `IRefreshTokenGrain` (SHA-256-hashed; key = PlayerId). Each `/refresh` **rotates** the token; replaying a used one trips **reuse detection** and revokes the whole session family. `MetaAuthOptions`: `AccessTokenLifetime` (30 min) + `RefreshTokenLifetime` (30 days); `TokenLifetime` is an `[Obsolete]` alias.

- Endpoints: `POST /refresh { refreshToken }` → new access + rotated refresh (401 on invalid/expired/reuse); `POST /logout { refreshToken }`. `reset-device`/`unlink` revoke that credential's sessions.
- `EnsureAuthenticatedAsync` auto-refreshes (rotating) when access expired + refresh valid, else full login. `MetaAuth.RefreshAsync(authUrl, refreshToken)` for explicit refresh. `CachedToken.RefreshValid`; storage persists the refresh token.
- Mid-session: `MetaTokenManager` hands out a valid token via `GetTokenAsync()` (single-flight on-demand refresh) + `RefreshNowAsync()` (reactive) + `StartAutoRefresh()` (proactive). Pass `tokens.GetTokenAsync` to any transport as an access-token provider so reconnect picks up a fresh token automatically:

```csharp
var tokens = new MetaTokenManager(authUrl, deviceId, storage);
var connection = new SignalRConnection($"{serverUrl}/meta", tokens.GetTokenAsync);            // provider ctor
// HTTP: new HttpPollingConnectionOptions { ServerUrl = ..., AccessTokenProvider = tokens.GetTokenAsync }
```

Custom `IMetaAuthProvider` must implement `RefreshAsync`; custom `ITokenStorage`/`MetaLoginResult` gained refresh fields. Fixed-string `accessToken` ctors/options remain for back-compat.

**Rejected-token recovery (0.30.1+):** a cached, not-yet-expired token can still be rejected by the server (e.g. JWT signing key changed) → `ConnectAsync` throws `Authentication is required`. Set `MetaClientOptions.AccessTokenSource = tokens` (a `MetaTokenManager`, which implements `IAccessTokenSource`) and the client **auto-recovers** — invalidates the token and retries the connect once on auth-type failures. Requires a provider-based connection (`tokens.GetTokenAsync`), not a fixed token. Override the policy with `MetaClientOptions.OnConnectAuthFailedAsync`; the underlying primitive is `MetaTokenManager.Invalidate()`.

**Custom auth providers (0.9.3+)**: implement `IMetaAuthProvider` and assign to `MetaAuth.Provider` to replace the HTTP auth flow entirely. Used by `SharedMeta.Backend.Local`'s `LocalMetaAuthProvider` to derive deterministic PlayerIds without any network call, so `MetaAuth.EnsureAuthenticatedAsync` works identically in local and remote modes. Priority: `Provider` → legacy Func hooks → built-in HTTP fallback.

**Enforcing auth:** `MetaTransportOptions.RequireAuthentication = true` rejects anonymous connections at SessionConnect. Additionally, you can add `[Authorize]` on a hub subclass or `.RequireAuthorization()` on endpoint mapping for middleware-level protection.

**Identity validation (0.37.1+):** a JWT is stateless, so wiping the auth store leaves valid tokens in client hands — the transport trusts `sub` and entity grains lazily create empty state for a PlayerId that can never be logged into again. `AddMetaAuth` registers an `IPlayerIdentityValidator` (`SharedMeta.Server.Core.Transport`) backed by `IAuthIndexGrain.HasKeysAsync()`; `SessionConnect` consults it before touching any grain and rejects with `SessionConnectFailureReason.IdentityUnknown` + an `"Authentication rejected: ..."` message, which routes the client through its auth-failure path (`OnConnectAuthFailedAsync` → drop cached token → full login → real PlayerId). Gate requires all of: a validator in DI (`TryAdd` — a host registration before `AddMetaAuth` wins), `RequireAuthentication = true` (otherwise PlayerId is client-supplied, not claim-derived), and `MetaTransportOptions.ValidatePlayerIdentity` (default `true`; set `false` for service accounts / externally minted tokens). Custom validators must already answer true for a just-created player — `AuthGrain.LoginAsync` writes the index entry before the token is minted.

**Client version enforcement (0.13.0+):** `MetaTransportOptions.ServerVersion` and `MinClientVersion` enforce version compatibility at connect time. Major version mismatch → always rejected. Minor/patch mismatch → rejected if client is below `MinClientVersion`. `MinClientVersion` can be overridden cluster-wide at runtime via `IVersionPolicyGrain` (Orleans grain singleton, key `"global"`) — cached per silo with a 60-second TTL. Clients pass `Application.version` via `SignalRConnection(url, token, clientVersion: Application.version)` or `HttpPollingConnectionOptions.ClientVersion`. On rejection, the `SessionConnectResponse` carries `ServerVersion` and `MinClientVersion` so the client can surface an actionable upgrade prompt.

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

### Mid-Method Persistence (Context.SaveStateAsync)

Force-persist state at an explicit point during method execution:

```csharp
await Context.SaveStateAsync();
```

- **Server**: persists state + random bytes to Orleans storage immediately
- **Client**: no-op (`Task.CompletedTask`)
- Use for pseudo-transactional cross-entity patterns: mutate state → `SaveStateAsync()` → send ACK to another entity
- Unlike `ForcePersist` (saves after method returns), `SaveStateAsync` checkpoints at the call site

---

## Code Generation

The SharedMeta source generator (`CoreGame.SharedMeta.Generator`) produces:

| Input | Generated Output |
|-------|-----------------|
| `[MetaService]` interface | `*Dispatcher.g.cs` — server-side method routing |
| `[MetaService]` interface | `*ApiClient.g.cs` — typed client API with async methods |
| `[MetaService]` interface | `*ServerApi.g.cs` — typed server-side API for calling the service from server code |
| `[MetaService]` interface | `*ServiceExtensions.g.cs` — DI registration helpers |
| `[MetaServiceImpl]` class | `*.Context.g.cs` — Context/State/dependency injection |
| `ISharedState` class | `*PatchWrapper.g.cs` — change tracking for ServerPatch mode (nested-object fields have get+set via implicit operator; collections have get+set for reassignment) |
| `ISharedState` class | `*PatchApplier.g.cs` — client-side patch application |
| All `[MetaService]` in assembly | `ServerMetaConfiguration.g.cs` — MetaProvider + service registration |

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
| `[ServiceConfig(typeof(TConfig), "Name")]` | Interface | Independently-versioned config, repeatable, all symmetric (no privileged "primary") — resolves synchronously in every execution mode; replaces `[MetaService].ConfigType`/`DefaultConfig` (obsolete but functional). (0.33.0+) |
| `[Tracked]` | Field | Push-based change tracking — generates property with tracking setter |
| `[Trigger]` | Method | Auto-execute after condition on another method |
| `[ServerMetaService]` | Interface | Server-only service (generates replayer) |
| `[StatelessMetaService]` | Interface | No-entity service resolving only a linked `[MetaConfig]` |
| `[StatelessMetaServiceImpl]` | Class | Impl for `[StatelessMetaService]` — injects only a typed `Config` property |
| `[Transformer]` | Class | Declare argument transformer (discovered at compile time) |
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
| `GenerateClientApi` | bool | true | Generate API client method. When `false`: client API not generated **and** the server gates direct client RPCs (forged packet → "not callable from clients"). Cross-entity and sibling calls still work because they don't traverse the client-RPC boundary. |
| `SkipServerOnFalse` | bool | false | Skip server call if local returns false/default |
| `ForcePersist` | bool | false | Always persist state after execution |

### MetaService Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StateType` | Type | required | State class type |
| `ConfigType` | Type | null | Explicit config type for this service |
| `DefaultConfig` | bool | false | Use config class with `[MetaConfig(Default = true)]`. Also opts the service into the generator's auto-`StaticConfigProvider<T>(new T())` fallback on the client (0.17.0+); without this flag, an explicit `RegisterConfigProvider<T>` is required and a missing one throws at first subscribe |
| `AccessPolicy` | EntityAccessPolicy | Open | Subscribe access control |
| `PatchTracking` | bool | true | (0.24.2+) Allow the auto-generated `{Impl}_PatchTracked` copy for force-patch. `false` = opt out: no copy, force-patch clients rejected at negotiation/subscribe instead of served an empty patch. Set false only when the body can't be copy-compatible. See *ServerPatch → Patch-tracking copy* |

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
| `CoreGame.SharedMeta.Transport.SignalR` | SignalR WebSocket — **server-side `MetaHub`** (needs ASP.NET / Server.Core) |
| `CoreGame.SharedMeta.Transport.SignalR.Client` | SignalR WebSocket — **client `SignalRConnection`** for .NET / Godot / console (no server deps; Unity uses the UPM SignalRConnection instead) |
| `CoreGame.SharedMeta.Transport.SignalR.MessagePack` | Optional MessagePack protocol extension for SignalR |
| `CoreGame.SharedMeta.Transport.HttpPolling` | HTTP long-polling — server endpoints + client (.NET) |
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

### Mux Transport (Stress Tests, 0.22.0+)

`SharedMeta.Debug.Mux` — debug-only transport where N logical client sessions share one physical SignalR socket. Map `app.MapMetaMuxHub("/meta-mux")` on the server (alongside `/meta`); on the client build a pool of `MuxChannel` instances and call `channel.CreateConnection(tag)` per simulator. Each `MuxConnection` implements `IConnection`, so the rest of the MetaClient stack is unchanged.

Use when you want 1000+ simulated players from one client process without burning a WebSocket per simulator. See `examples/ClanWars/ClanWars.Client.Common/StressTestRunner.cs` for a runner pattern with channel-pool construction and round-robin tag assignment, and [docs/GUIDE.md § Mux Transport](../docs/GUIDE.md#mux-transport--high-fanout-stress-tests-0220) for the full API + trade-offs.

### Observability (0.23.0+)

SharedMeta exposes two static `Meter` + `ActivitySource` pairs — server-side `"SharedMeta"` (in `SharedMeta.Server.Core.Telemetry.SharedMetaMeters`) and client-side `"SharedMeta.Client"` (in `SharedMeta.Client.Telemetry.SharedMetaClientMeters`). Hosts subscribe via OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(b => b
        .AddMeter(SharedMetaMeters.MeterName)         // "SharedMeta"
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(t => t
        .AddSource(SharedMetaActivities.SourceName)); // also "SharedMeta"
app.MapPrometheusScrapingEndpoint();
```

No OpenTelemetry NuGet dependency in framework packages — meters use only built-in `System.Diagnostics.Metrics`. When no listener is attached, every `Counter.Add` / `Histogram.Record` is a volatile-flag check, no allocation.

Server-side instrumentation covers: `session.connect.duration`, `session.active`, `entity.subscribe.duration`, `entity.rpc.duration` (per service+method+result), `entity.rpc.request_bytes`, `cross_entity.call.duration` (kind = `normal | notification`), `broadcast.fan_out_size`, `broadcast.payload_bytes` (kind = `replay | patch | state`), `broadcast.tailored.count`, `persistence.write.duration`, `compat.force_patch.applied`, `grain.activation.count`, `grain.active`. Plus distributed-tracing spans nested via in-process `Activity.Current`.

Client → server W3C `traceparent` propagation on RPC envelopes is **not yet implemented** — client and server traces are independent for now.

Reference wire-up: `examples/ClanWars/ClanWars.Server/Program.cs` (Prometheus exporter on `/metrics`). Full catalog: [docs/GUIDE.md § Observability](../docs/GUIDE.md#observability-0230).

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
    void OnPatchDesync(string serviceName, string methodName,
        uint serverCrc, uint localCrc);  // deep desync (0.7.0+)
    void OnCrossEntityResult(string entityId, string serviceName,
        string methodName, byte[]? resultBytes);
    Task<StateComparisonResult> CompareFullStateAsync(string entityId);
}
```

### Desync Message Values

`DesyncException.Message` and the `[Desync]` log line print member values, not type names —
the generator emits a `MetaDescribe` formatter per result type into `DesyncFormatters.g.cs`
(compile-time, so IL2CPP stripping cannot break it). Types overriding `ToString()` (records
included) keep their own rendering; nesting stops at 3 levels, collections at 8 elements.

```
server=SellCargoResult { Gold = 10, Item = "ore", Line = CargoLine { Quantity = 5 }, Ids = [1, 2, 3] }
```

Shorten project-wide (nested types → `<TypeName>`, collections → `[N items]`):

```csharp
[assembly: SharedMetaDiagnosticsOptions(DesyncValues = DesyncValueDetail.Short)]
```

---

## Granular Collection Patches (0.9.0+)

When a service has a `{Impl}_PatchTracked` copy (via `[MetaServiceImpl(DeepDesync = true)]` **or**, since 0.24.2, because it's force-patch-able — see *ServerPatch → Patch-tracking copy*), list-typed fields use a fine-grained patch representation instead of dumping the whole list on every mutation. The compile-time tracking guard below (wrapper-typed helpers, no reverse `wrapper→raw` operator) applies to both.

### List of sub-wrappable elements

If `T` in `List<T>` has its own `[MemoryPackOrder]` properties (i.e. it's "sub-wrappable"), the generator emits a specialized `{Element}PatchableList` nested inside the parent `{State}PatchWrapper`. The indexer hands out `{Element}PatchWrapper` bound to a per-element subtree node:

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
        // hero is HeroPatchWrapper in the _PatchTracked copy.
        // The += writes a Field child terminal at Heroes/[heroIndex]/Exp.
        // Other heroes are NOT touched in the patch.
        state.Heroes[heroIndex].Exp += amount;
    }

    public void EquipItem(int heroIndex, Item item)
    {
        // Two-level nesting: outer indexer creates an element subtree at
        // Heroes/[heroIndex], inner Equipment.Add records a structural Insert op
        // on Heroes/[heroIndex]/Equipment. The rest of the party is untouched.
        state.Heroes[heroIndex].Equipment.Add(item);
    }
}
```

### Compile-time tracking guard

Helper methods that look up an element must be **typed as `{Element}PatchWrapper`**, not raw `{Element}`. The same source compiles in both the regular service and the generated `_PatchTracked` copy thanks to a one-way implicit conversion from raw → wrapper. The reverse direction is intentionally absent — returning raw `Hero` from a helper compiles fine in the regular branch but fails the `_PatchTracked` copy with `CS0029`, catching the silent-loss-of-tracking bug at compile time.

```csharp
private PartyStatePatchWrapper.HeroPatchWrapper? FindById(int heroId)
    => state.Heroes.FirstOrDefault(h => h.Id == heroId);

public void AwardExpById(int heroId, int amount)
{
    var hero = FindById(heroId);   // var = HeroPatchWrapper in _PatchTracked
    if (hero != null) hero.Exp += amount;
}
```

If you write `Hero? FindById(...)` instead, the regular branch compiles fine (raw `Hero` is what `FirstOrDefault` returns from `List<Hero>`), but the `_PatchTracked` branch fails:
```
error CS0029: Cannot implicitly convert type
'PartyStatePatchWrapper.HeroPatchWrapper' to 'Hero'
```
Use `var` for locals to avoid having to spell out the wrapper type.

### Structural ops

`Add` / `Insert(idx, ...)` / `RemoveAt(idx)` / `Remove(item)` / `Clear()` and indexer assignment all record individual `PatchListOp` entries on the collection node's `StructuralOps` list. Phase 2 supports:

| Method | Op kind | Notes |
|--------|---------|-------|
| `Add(item)` | `Insert` | Index = `Count - 1` after the add |
| `Insert(idx, item)` | `Insert` | Sender shifts existing element children's indices forward |
| `RemoveAt(idx)` | `RemoveAt` | Sender drops element child at `idx` and shifts higher indices down |
| `Remove(item)` | `RemoveAt` | Resolved to index via `IndexOf` |
| `list[i] = value` | `Set` | Drops in-place mutations for that index (element wholesale replaced) |
| `Clear()` | `Clear` | Drops all element children and prior structural ops |
| `Sort` / `Reverse` / `AddRange` / `RemoveAll` / etc. | `FullReplace` | Falls back to packing the whole list |
| `state.Heroes = newList` | `FullReplace` | Wholesale field reassignment records a `FullReplace` op (clears prior ops/element children on the node first) |

All applied in submission order on the receiver via `CollectionPatchApplier.Apply<T>(...)`.

**Invariant:** list-typed patch nodes never carry a terminal `Value`. Wholesale replacement is just another op in the `StructuralOps` stream, which means **assign-then-mutate in the same call is supported**:

```csharp
state.Cells = new List<byte>(totalCells);   // FullReplace op (empty list)
for (int i = 0; i < totalCells; i++)
    state.Cells.Add(0);                      // chained Insert ops
state.Cells[5] = wallValue;                  // chained Set op
```

The receiver applies `FullReplace` → `Insert` × N → `Set` in order, so the final list matches the sender. Dict/HashSet/Array fields use the simpler terminal-Value path (each mutation writes a fresh snapshot wholesale, no op stream needed).

### Mixed structural + element mutations

```csharp
public void MixedShift(int firstIdxToRemove, int targetHeroId, int expDelta)
{
    var target = FindById(targetHeroId);
    if (target != null) target.Exp += expDelta;   // element subtree at current index
    state.Heroes.RemoveAt(firstIdxToRemove);       // shifts target's element child down
}
```

`RemoveAt` automatically calls `ShiftElementChildren` on the collection node, so the element subtree for `target` ends up at the correct post-removal index. The receiver applies the structural op first, then the element subtree at the now-canonical index.

### Scalar lists also benefit

`PatchableList<T>` (for `List<int>`, `List<byte>`, `List<string>`, etc.) also uses op-based recording in 0.9.0. A single `state.Cells[5] = newValue` writes one `Set` op instead of dumping the entire array — important for things like `List<byte> Cells` map data in Expedition.

---

## Session Health & RPC Ordering

### Server-side RPC Reordering (0.8.0+)

Some transports (HTTP polling, custom UDP, anything with intermediate `Task.Run`) do not preserve the order in which the client invoked RPCs by the time those calls reach the entity grain on the server. Concurrent Optimistic calls then race on the threadpool and cause phantom patch desyncs.

`SessionManagerOptions.EnforceRpcOrder = true` opts in to a per-session reordering gate inside `SessionManagerGrain`. The gate parks out-of-order calls in a fixed-capacity ring buffer and drains them in monotonic `RequestId` order when their predecessor arrives. All results from the inline call + drained stash are bundled into a single `SessionResponse` so the client sees them atomically.

```csharp
services.Configure<SessionManagerOptions>(o =>
{
    o.EnforceRpcOrder = true;            // default: false
    o.StashCapacity = 256;               // default
    o.SoftStallNotifyTimeout = TimeSpan.FromMilliseconds(500);
    o.HardStallNotifyTimeout = TimeSpan.FromSeconds(10);
    o.MaxStallDuration = TimeSpan.FromMinutes(5);
});
```

- **SignalR over a single hub connection**: ordering is preserved by the protocol; you can leave `EnforceRpcOrder = false`.
- **HTTP polling / multi-channel transports**: should set `EnforceRpcOrder = true`.

### Stall Notifications

When an ordering gap stays open beyond `SoftStallNotifyTimeout`, the server pushes a `StallNotification` to the client through the existing observer channel. The client routes it to `ISessionHealthListener`:

```csharp
public interface ISessionHealthListener
{
    void OnSessionStalled(StallNotification notification);    // Stalled or TimeoutPending
    void OnSessionRecovered(StallNotification notification);  // gap closed
}

new MetaClient(connection, serializer, new MetaClientOptions
{
    SessionHealth = new MyStallUiListener(),  // shows "syncing…" / prompt
});
```

Stages:
1. **`StallStage.Stalled`** — first notification (after `SoftStallNotifyTimeout`). UI shows a low-key indicator.
2. **`StallStage.TimeoutPending`** — second notification (after `HardStallNotifyTimeout`). UI may prompt the user.
3. **`StallStage.Recovered`** — gap closed; hide UI.

After `MaxStallDuration` the server **terminates the session** (`ISessionObserver.OnSessionTerminated`) and the client must reconnect — the assumption is that the missing predecessor was lost permanently and continuing risks desync. Stash overflow (more than `StashCapacity` simultaneously parked requests) also terminates immediately.

> **Note (0.10.0+):** Server-side stall notifications are now **lazy** — pushed on the next incoming request, not via periodic grain timers. Client-side auto-retry handles recovery.

### Client-Side Connection Health (0.10.0+)

`IConnectionHealthListener` monitors pending RPC age on the client. Works even when server is unreachable.

```csharp
new MetaClient(connection, serializer, new MetaClientOptions
{
    ConnectionHealth = myHealthListener,       // IConnectionHealthListener
    ConnectionHealthOptions = new()            // SoftTimeoutMs=1000, HardTimeoutMs=5000, RetryIntervalMs=2000
});
```

- **Auto-retry**: client resends all pending requests every `RetryIntervalMs` when oldest exceeds `SoftTimeoutMs`. Primary packet-loss recovery — no server dependency.
- **`ResumeSessionAsync()`**: restore session without restart (same `sessionId`, missed packet recovery).
- **`DebugConnectionWrapper`**: wraps `IConnection` for latency/loss/disconnect simulation. `PacketLossMode.ConnectionDrop` (SignalR) vs `RequestHang` (HTTP polling). `SimulateTemporaryDisconnectAsync(ms)` for metro-style testing.
- **`DiagnosticsLog`**: `Action<string>` on `ClientDispatcher` for file-based request lifecycle tracing (SEND/RECV/CONFIRMED/AUTO_RETRY/RESEND).

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

---

## Generated-Code Marker (`[GeneratedFromMetaMethod]`, 0.16.0+)

Every method emitted as a mirror of a `[MetaMethod]` is stamped with `[GeneratedFromMetaMethod(typeof(IFoo), "Bar")]`. Appears on `*ApiClient.{Name}Async/Sync/Signal`, `*EntityQueryApi.{Name}Async`, the `{I}EntityCaller` cross-entity proxy interface, and its `*EntityRecorder` / `*EntityReplayer` / `*LocalEntityCaller` runtime implementations. Carries `(Type ServiceInterface, string MethodName)` — a `typeof()` reference so it survives interface rename. Runtime-inert; consumed only by tooling such as the [Rider plugin in `SharedLibs/RiderPlugin`](https://github.com/CoreGameIO/SharedLibs/tree/main/RiderPlugin), which uses it to bridge Find Usages and Go to Declaration between the user-authored `[MetaMethod]` and every generated counterpart.
