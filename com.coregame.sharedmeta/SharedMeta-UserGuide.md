# SharedMeta User Guide

Quick-start reference for projects using the SharedMeta framework.

## Installation

### NuGet Packages

Shared project (game logic):
```xml
<PackageReference Include="CoreGame.SharedMeta.Core" Version="0.1.0" />
<PackageReference Include="CoreGame.SharedMeta.Generator" Version="0.1.0" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Server project:
```xml
<PackageReference Include="CoreGame.SharedMeta.Server" Version="0.1.0" />
<PackageReference Include="CoreGame.SharedMeta.Server.Core" Version="0.1.0" />
<PackageReference Include="CoreGame.SharedMeta.Orleans" Version="0.1.0" />
<PackageReference Include="CoreGame.SharedMeta.Transport.SignalR" Version="0.1.0" />
<PackageReference Include="CoreGame.SharedMeta.Serialization.MemoryPack" Version="0.1.0" />
```

Client project (.NET console):
```xml
<PackageReference Include="CoreGame.SharedMeta.Client" Version="0.1.0" />
<PackageReference Include="CoreGame.SharedMeta.Transport.SignalR" Version="0.1.0" />
<PackageReference Include="CoreGame.SharedMeta.Serialization.MemoryPack" Version="0.1.0" />
```

### Unity (UPM)

Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.coregame.sharedmeta": "https://github.com/CoreGame-IO/SharedMeta.git#v0.1.0"
  }
}
```

Use **SharedMeta > Project Wizard** in Unity to scaffold server/client projects.

---

## Step 1: Define Shared State

State classes need dual serialization attributes and must implement `ISharedState`:

```csharp
[MemoryPackable]       // For client-server transport
[GenerateSerializer]   // For Orleans grain persistence
public partial class GameState : ISharedState
{
    [Id(0)] public int Score { get; set; }
    [Id(1)] public List<string> Items { get; set; } = new();
    [Id(2)] public string CurrentPlayerId { get; set; } = "";
}
```

Rules:
- Class must be `partial` (required for code generation)
- Every property needs `[Id(n)]` for Orleans schema evolution
- All nested types also need `[MemoryPackable]` + `[GenerateSerializer]`

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

## Execution Modes

| Mode | Behavior | Use For |
|------|----------|---------|
| **Optimistic** | Client executes immediately, server validates. Rollback on mismatch. | UI-responsive actions (move, play card, buy item) |
| **Server** | Client waits for server response. | Loot drops, matchmaking, hidden state, ServerRandom |
| **Local** | Client-only, no RPC sent. | UI state, local previews, client-side filtering |
| **CrossOptimistic** | Like Optimistic but targets a different entity. | Cross-entity interactions (trading, attacking) |
| **ServerPatch** | Server sends state diffs instead of full state. | Large state optimization, bandwidth savings |

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

---

## Server Time

```csharp
long now = Context.ServerTimeTicks;  // UTC ticks, synced with server
```

Available in all execution modes. Use for time-based mechanics (cooldowns, timers).

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

## Checklist: Adding a New Method

1. Add method to `[MetaService]` interface with `[MetaMethod(Mode = ...)]`
2. Implement in `[MetaServiceImpl]` class
3. If using new argument/return types — add `[MemoryPackable]` + `[GenerateSerializer]` + `[Id(n)]`
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
