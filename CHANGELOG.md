# Changelog

## [0.2.0] - 2026-02-28

### Unity Project Wizard
- Add transport selection: SignalR (WebSocket) or HTTP Polling
- Add serializer selection: MemoryPack or MessagePack
- Add dependency management UI with auto-detection and install buttons
- Generate complete Shared, Server, and Client projects from wizard
- Conditional transport assemblies via `defineConstraints` — no hard dependencies
- HTTP transport (`UnityHttpConnection`) compiles only when `com.unity.nuget.newtonsoft-json` is installed
- SignalR transport compiles only when `HAS_SIGNALR` scripting define is present
- Auto-add `HAS_SIGNALR` define when SignalR DLLs are detected

### ServerPatch
- Fix optimistic random not advancing on client after patch application
- Fix `PatchBytes` not forwarded to broadcast subscribers via `SessionManagerGrain`
- Add `MetaRandom.Skip(count)` for advancing PRNG state without producing values

### Generator
- Ship pre-built generator DLL in UPM package (`Runtime/Analyzers/`)
- Generated client code now supports transport selection in `MetaGameClient.cs`
- MessagePack serializer support in generated code (`[MetaSerializer(SerializerType.MessagePack)]`)

### MessagePack
- Cross-assembly serialization via `CompositeResolver` — resolvers from all referenced assemblies are composed at startup
- `MetaMessagePackOptions.Configure(params Assembly[])` discovers per-assembly source-generated resolvers via reflection
- Auto-generated `GeneratedMetaMessagePackConfiguration.Configure()` — source generator scans referenced assemblies for `[GeneratedAssemblyMessagePackResolverAttribute]` and emits a single startup call
- Dual serialization attributes: `[MemoryPackable, MessagePackObject, GenerateSerializer]` with `[Id(n), Key(n), MemoryPackOrder(n)]`

### Other
- Remove hard `com.unity.nuget.newtonsoft-json` dependency from `package.json`
- Fix `ConnectResponse` properties: `init` -> `set` to resolve MsgPack017 warning

## [0.1.0] - 2026-02-26

- Initial public release
