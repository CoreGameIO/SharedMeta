# SharedMeta

Framework for shared game meta-logic between Client and Server with deterministic replay.

## Features

- **Shared business logic** — write game services once, run on both client and server
- **Deterministic replay** — optimistic execution on client, server-authoritative confirmation
- **Code generation** — compile-time dispatchers, typed API clients, context injection
- **Multiple execution modes** — Optimistic, Server, Local, CrossOptimistic, ServerPatch
- **Deterministic random** — `Context.Random` (optimistic) and `Context.ServerRandom` (server-only with replay)
- **Push-based change tracking** — `[Tracked]` fields for reactive UI binding (client-only, zero server overhead)
- **Cross-entity calls** — call methods on other entities from server-side logic
- **State patching** — ServerPatch mode for efficient partial state updates
- **Pluggable transport** — SignalR, HTTP polling, in-process (testing)
- **Pluggable serialization** — MemoryPack, MessagePack

## Installation

### Unity (UPM)

Add via Package Manager using a local path or git URL:

```
https://github.com/CoreGameIO/SharedMeta.git?path=com.coregame.sharedmeta
```

### .NET (NuGet)

```
dotnet add package CoreGame.SharedMeta.Core
dotnet add package CoreGame.SharedMeta.Client
dotnet add package CoreGame.SharedMeta.Serialization.MemoryPack
```

## Quick Start

### 1. Define state and service

```csharp
[MemoryPackable]
public partial class PlayerState : ISharedState
{
    [MemoryPackOrder(0)] public string Name { get; set; } = "";
    [MemoryPackOrder(1)] public int Level { get; set; }
}

[MetaService(StateType = typeof(PlayerState), AccessPolicy = EntityAccessPolicy.UserOwned)]
public interface IPlayerService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    Task SetName(string name);
}
```

### 2. Implement service

```csharp
[MetaServiceImpl(typeof(IPlayerService))]
public partial class PlayerService : IPlayerService
{
    public Task SetName(string name)
    {
        State.Name = name;
        return Task.CompletedTask;
    }
}
```

### 3. Use on client

```csharp
var client = new MetaClient(connection, serializer, options);
await client.ConnectAsync();
client.Resolver.RegisterAllServices();

var api = await client.GetServiceAsync<PlayerServiceApiClient>("player-1");
await api.SetName("Alice");
```

## Requirements

- Unity 6000.0+ (UPM) or .NET 8.0+ (NuGet)

## Documentation

See the [full guide](https://github.com/CoreGameIO/SharedMeta) for detailed documentation.
