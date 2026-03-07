# SharedMeta

Framework for shared game meta-logic between Client and Server with deterministic replay.

Write game logic **once** in C# — it runs on the server (Orleans grains) and replays on the client (Unity / .NET) with optimistic execution, automatic rollback, and desync detection.

## What You Can Build

**Player profiles and progression** — experience, levels, inventory, currencies. State is persisted per-player, changes are optimistic (instant on client, validated on server).

**Turn-based and card games** — shared game rules execute identically on both sides. Matchmaking, lobbies, multi-entity game sessions with deterministic random for shuffles and draws.

**Cooperative and async multiplayer** — one player's action modifies another player's state via cross-entity calls. Energy systems, trading, expeditions that span multiple entities.

**Economy and resource systems** — crafting, shops, timers, regeneration. Server-only random for loot drops and rewards (client can't predict or cheat). ServerPatch mode for bandwidth-efficient state diffs.

**Live-ops and admin tools** — server-side triggers push events to clients. Subscribers react to state changes. Hot-swappable transport (WebSocket or HTTP polling) and serializer (MemoryPack or MessagePack).

## Quick Start (Unity)

### 1. Install the Package

Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.coregame.sharedmeta": "https://github.com/CoreGameIO/SharedMeta.git#upm"
  }
}
```

### 2. Open the Project Wizard

**SharedMeta > Project Wizard** in Unity menu.

Configure:
- **Project name** — your shared namespace (e.g. `MyGame.Shared`)
- **State name** — entity state class (e.g. `PlayerProfile`)
- **Transport** — SignalR (WebSocket, real-time) or HTTP Polling (universal, no extra DLLs)
- **Serializer** — MemoryPack (default) or MessagePack

The **Dependencies** section auto-detects and installs required packages (serializer, transport).

### 3. Generate Projects

Use the three generation tabs:

| Tab | Generates | Output |
|-----|-----------|--------|
| **Shared Project** | State class, service interface, implementation, .csproj | Unity folder + .NET mirror with linked sources |
| **Server Project** | ASP.NET Core server with Orleans, transport, auth | Standalone .NET project |
| **Client Scripts** | `MetaGameClient.cs` MonoBehaviour + logger | Unity Assets folder |

### 4. Run

Start the server from Unity: **SharedMeta > Server Runner** — click **Start**.

Or from terminal:
```bash
cd MyGame.Server
dotnet run
```

Press Play in Unity — `MetaGameClient` connects automatically.

### Quick Start (.NET Client — Godot, Console, etc.)

Add NuGet packages to your `.csproj`:
```xml
<ItemGroup>
  <PackageReference Include="CoreGame.SharedMeta.Core" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Client" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Serialization.MemoryPack" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Transport.SignalR.Client" Version="0.2.0" />
  <PackageReference Include="CoreGame.SharedMeta.Generator" Version="0.2.0"
                    PrivateAssets="all" OutputItemType="analyzer" />
</ItemGroup>
```

Client transport packages have no server dependencies (no Orleans, no ASP.NET). Works with Godot (`Godot.NET.Sdk`), console apps, or any `net8.0+` project.

For MessagePack SignalR protocol (optional, better performance):
```xml
<PackageReference Include="CoreGame.SharedMeta.Transport.SignalR.MessagePack" Version="0.2.0" />
```

### Quick Start (examples)

```bash
dotnet run --project examples/CardGame_TheFool/CardGame.Server
dotnet run --project examples/CardGame_TheFool/CardGame.Client
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Meta Layer (SharedMeta.Core, YourGame.Shared)                  │
│  Business logic: services, state, [MetaService] / [MetaMethod]  │
│  Code generation: dispatchers, API clients, context injection   │
└─────────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Middleware Layer (SharedMeta.Client, SharedMeta.Server)        │
│  MetaContext, replay mechanism, execution modes                 │
│  (Optimistic / Server / Local / CrossOptimistic / ServerPatch)   │
└─────────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Serialization Layer (SharedMeta.Serialization.*)               │
│  IMetaSerializer, MemoryPack / MessagePack implementations      │
└─────────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Transport Layer (SharedMeta.Transport.*)                       │
│  IConnection: SignalR WebSocket, HTTP long-polling, InProcess   │
└─────────────────────────────────────────────────────────────────┘
                              ↕
┌─────────────────────────────────────────────────────────────────┐
│  Server Backend (SharedMeta.Server.Core, Orleans)               │
│  IMetaProvider<TState>, EntityGrain, SessionManager             │
└─────────────────────────────────────────────────────────────────┘
```

Each layer depends only on the layers above it. Swap serializers, transports, or backends without changing game logic.

## Key Concepts

### Define Shared State

```csharp
[MemoryPackable, MessagePackObject]  // transport serialization (pick one or both)
[GenerateSerializer]                  // Orleans grain persistence
public partial class GameState : ISharedState
{
    [Id(0), Key(0), MemoryPackOrder(0)] public int Score { get; set; }
    [Id(1), Key(1), MemoryPackOrder(1)] public List<string> Items { get; set; } = new();
}
```

- `[MemoryPackable]` — MemoryPack serializer (default, zero-copy)
- `[MessagePackObject]` + `[Key(n)]` — MessagePack serializer (cross-platform, schema-flexible)
- `[GenerateSerializer]` + `[Id(n)]` — Orleans grain state persistence

Choose one serializer or use both. The wizard configures this automatically.

### Define a Service

```csharp
[MetaService("IGameService")]
public interface IGameService
{
    [MetaMethod(ExecutionMode.Optimistic)]
    void AddItem(string itemId);

    [MetaMethod(ExecutionMode.Server)]
    void GrantReward(int amount);
}
```

### Implement the Service

```csharp
[MetaServiceImpl(typeof(IGameService))]
public partial class GameServiceImpl : IGameService
{
    // Context is injected by source generator
    public void AddItem(string itemId)
    {
        State.Items.Add(itemId);
    }

    public void GrantReward(int amount)
    {
        // ServerRandom only generates on server; client replays from payload
        int bonus = Context.ServerRandom!.Next(10);
        State.Score += amount + bonus;
    }
}
```

### Execution Modes

| Mode | Client | Server | Use Case |
|------|--------|--------|----------|
| **Optimistic** | Executes immediately, rolls back on mismatch | Authoritative execution | UI-responsive actions (move, play card) |
| **Server** | Waits for server response | Executes with ServerRandom | Loot drops, matchmaking, secrets |
| **Local** | Local-only, no RPC | — | UI state, client-side filtering |
| **CrossOptimistic** | Executes on own state | Routes to target entity's grain | Cross-entity interactions |
| **ServerPatch** | Receives state diff from server | Sends patch instead of full state | Large state, bandwidth optimization |

### Deterministic Random

```csharp
// Optimistic random — same algorithm & seed on both sides
int roll = Context.Random!.Next(6) + 1;

// Server random — generated on server, replayed on client
int loot = Context.ServerRandom!.Next(100);
```

### Client Usage

```csharp
var client = new MetaClient(connection, serializer);
await client.ConnectAsync("player-123", "entity-456");

// Generated typed API client
var gameApi = client.GetService<IGameServiceApiClient>();
gameApi.AddItem("sword_01");

// Subscribe to state changes
client.OnStateChanged += state => UpdateUI((GameState)state);
```

## Running the Server

### From Unity (recommended)

Open **SharedMeta > Server Runner** in the Unity menu. This opens an Editor window where you can:

- **Select your server .csproj** — auto-detected from Wizard settings, or pick manually
- **Start / Stop** the server with one click
- **View console output** with search, filtering, and color-coded log levels (errors in red, warnings in yellow)
- **Open in IDE** or **Reveal** in file explorer
- **Pass extra arguments** via the "Extra Args" field (e.g. `-- 5001` for a different port)

The server process survives Unity domain reloads (script compilation) and is automatically stopped when the Editor quits. The Runner tracks the process PID across reloads so it can re-attach to a running server.

### From Terminal

```bash
cd YourGame.Server
dotnet run
```

By default the server listens on `http://localhost:5000`. Pass a port as argument: `dotnet run -- 5001`.

### Multiple Clients

To test multiplayer locally (e.g. matchmaking), you need two Unity clients connecting to the same server:

- **Editor + Build**: Press Play in the editor, then Build & Run a standalone player
- **Two builds**: build twice and run both executables
- **ParrelSync**: clone the project for a second editor instance

All clients connect to the same server URL. The server handles session management and entity routing via Orleans grains.

## Examples

### CardGame "The Fool"

Multiplayer turn-based card game with matchmaking lobby. Two players, attack/defend mechanics, trump suit. Demonstrates: `Optimistic` execution for card plays, `Server` mode for deck shuffle, lobby system with triggers, multi-entity game state.

### Expedition

Single-player dungeon exploration with procedural map generation. Demonstrates: `CrossOptimistic` calls between expedition and profile entities, energy/money economy, `ServerPatch` mode (optional), deterministic random for map generation, session reconnection.

## Project Structure

| Directory | Description |
|-----------|-------------|
| `Runtime/Core/` | Core framework: attributes, interfaces, meta context, random |
| `Runtime/Client/` | Client-side dispatcher, message buffer, MetaClient |
| `Runtime/Transport/` | Conditional transport assemblies: HTTP Polling, SignalR client |
| `Runtime/Serialization/` | MemoryPack serialization |
| `Runtime/Orleans.Stubs/` | Stub attributes for Unity (no Orleans dependency) |
| `Editor/` | Project Wizard, Server Runner, pre-built source generator DLL |
| `src/SharedMeta.Generator/` | Source generator: dispatchers, API clients, context injection |
| `src/SharedMeta.Server/` | Server-side meta context and cross-entity calls |
| `src/SharedMeta.Server.Core/` | EntityGrain, MetaProvider, file storage, session management |
| `src/SharedMeta.Orleans/` | Orleans grain integration |
| `src/SharedMeta.Transport.SignalR/` | SignalR transport — server (MetaHub) + MessagePack protocol |
| `src/SharedMeta.Transport.SignalR.Client/` | SignalR client-only (JSON default, no server deps) |
| `src/SharedMeta.Transport.SignalR.MessagePack/` | MessagePack protocol extension for SignalR |
| `src/SharedMeta.Transport.HttpPolling/` | HTTP polling transport — server endpoints |
| `src/SharedMeta.Transport.HttpPolling.Client/` | HTTP polling client-only (HttpClient, no server deps) |
| `src/SharedMeta.Auth/` | JWT authentication middleware |
| `examples/` | CardGame_TheFool, Expedition — full working examples |
| `tests/` | Integration and unit tests |

## License

[MIT](LICENSE)
