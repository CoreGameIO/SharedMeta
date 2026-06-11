# Changelog

## [0.26.7] - 2026-06-07

### Added — defaults so the Wizard doesn't need to emit them

- `MetaClient` auto-logs `OnConnectionStatusChanged` transitions to `MetaLog` (Reconnecting/Disconnected → Warning, Failed → Error). Opt out with `MetaClientOptions.LogConnectionStatusToMetaLog = false`. Game-level handlers still cleanly subscribe alongside.
- `MetaClient.RegisterDownloadingConfigProvider<TConfig, TState>(downloader?, cache?)` extension — one-liner around `DownloadingConfigProvider<TConfig>` + the connection's URL resolver. Defaults to `UnityConfigDownloader.DownloadAsync` on Unity builds.

### Fixed — clean import on a fresh Unity project

- `Shimz.cs` extended with `MemoryPackAllowSerializeAttribute`, `GenerateType` enum, and `MemoryPackable(GenerateType)` ctor so SharedMeta compiles in Unity projects without the NuGet `MemoryPack` package.
- `ClientDispatcher.cs` / `DispatcherNetworkAdapter.cs` no longer reference `System.Collections.Immutable` (not shipped in Unity by default).

## [0.26.6] - 2026-06-07

### Added — deep state check debugging

Per-method opt-in CRC comparison between client and server state, with full serialized binaries surfaced to a user callback on mismatch.

```csharp
[MetaMethod(Alias = "Move", Mode = ExecutionMode.Optimistic,
            DeepStateCheck = SnapshotTiming.After)]   // Before | After | Both
bool Move(int dx, int dy);
```

The generator emits the snapshot/CRC wrap only for annotated methods — unannotated methods compile with zero overhead.

On mismatch the framework invokes:

```csharp
void IDesyncDiagnostics.OnDeepStateDesync<TState>(
    string entityId,
    byte[] clientStateBytes,
    byte[] serverStateBytes,
    SnapshotTiming timing,
    long timestampTicks)
    where TState : class;
```

`TState` is typed at compile time so user diff code can `Unpack<TState>` both binaries directly. Default impl is empty — existing `IDesyncDiagnostics` impls compile unchanged.

See `docs/GUIDE.md` for usage.

## [0.26.5] - 2026-06-06

- **Critical bug fix**: `MetaHub`, `HttpPollingEndpoints`, and `MuxHub` `GetConfigDownloadUrl` handlers dropped `request.ConfigPatchVersion` when constructing the `MetaConfigVersion` for the `IConfigDownloadUrlResolver` — a client asking for `v0.1.870` ended up downloading `/meta/config/{state}/0.1.0` (silently stale bytes). Now passes all three components. `InProcessServer` was already correct; only the wire transports leaked the bug.

## [0.26.4] - 2026-06-06

- New `IConfigRegistry.PublishIfChangedAsync(configType, version, bytes, failOnDrift)` extension — diagnostic publish that compares against existing bytes and returns `PublishedNew` / `Unchanged` / `OverwrittenAfterDrift`. `failOnDrift: true` throws `ConfigContentDriftException` (typed) — use in CI/prod to enforce "content changes require a version bump". Raw `PublishAsync` still silently overwrites; this helper makes drift visible.
- `FileConfigCache<TConfig>` filename now includes `Patch`: `{Name}.v{Major}.{Minor}.{Patch}.bin` (was `v{Major}.{Minor}.bin`). Pre-0.26.4 cache collapsed every patch of the same Major.Minor into one file — broken for hotfix patches and for content-derived dev-version workflows. Old-format files auto-evicted by the existing `v*.bin` cleanup glob on next `Put`.

## [0.26.3] - 2026-06-03

- `ConnectionStatus.Failed` now fires when the transport gives up retrying (BestHTTP "No more reconnect attempt!"). Previously the dispatcher only emitted `Disconnected`, leaving no terminal signal for UI to gate a "Reconnect" button on.
- `BestHttpSignalRConnection` no longer hardcodes `TransportDisconnectReason.ClientRequested` on `OnClosed` — distinguishes user-initiated disconnect from transport give-up via a `_disconnectRequested` flag. Also emits `OnDisconnected(NetworkError)` from the `OnError` path when the hub state is already `Closed` — some BestHTTP versions don't fire `OnClosed` after retry-budget exhaustion, only `OnError`, leaving the UI hung in `Reconnecting`.

## [0.26.2] - 2026-06-01

Server-side config-download wiring shrinks to 2 lines, supports multi-config out of the box; generator-emitted default resolver no longer fights user registrations.

- New `IConfigByteSource` + generator-emitted `GeneratedConfigByteSource` — non-generic source that auto-routes `(stateType, version)` → bytes by picking the right `IMetaConfigProvider<TConfig>`.
- New `app.MapMetaConfigDownload()` (non-generic, recommended) — one endpoint serves every `[MetaConfig]` declared in the assembly. Generic `MapMetaConfigDownload<TConfig>(routePrefix)` overload kept for single-config dedicated-route cases.
- New `builder.Services.AddMetaConfigPublicUrl(publicBaseUrl, routePrefix)` — registers an `IConfigDownloadUrlResolver` that emits URLs matching the paired endpoint. Removes the need for custom URL-builder classes.
- Generator switched `IConfigDownloadUrlResolver` and `IConfigByteSource` registrations to `TryAddSingleton` — host-side `AddSingleton<...>(...)` wins without `RemoveAll` or ordering tricks.
- New extensions live in `SharedMeta.Server` namespace, shipped from `CoreGame.SharedMeta.Transport.SignalR`.
- Docs: `StaticConfigProvider` ignores the requested version and never consults the server — clarified in `GUIDE.md`, `SharedMeta-AI.md`, `SharedMeta-UserGuide.md` with a Static / Downloading / Composite comparison table.

## [0.26.1] - 2026-06-01

- Client telemetry (`SharedMetaClientMeters` / `SharedMetaClientActivities`) gated behind `SHAREDMETA_CLIENT_TELEMETRY`, off by default. Unity builds without `System.Diagnostics.DiagnosticSource` no longer fail to compile.

## [0.26.0] - 2026-06-01

Generator cleanup + cross-entity OneWay fix. Source-breaking for server-side cross-entity calls.

- All generated API methods always end in `Async` (client `ApiClient` + cross-entity `EntityCaller`), with dedup. Migration: rename `GetIService(id).X(...)` → `XAsync(...)` in server-side service code.
- `Mode = Notification` cross-entity caller now returns `Task` awaiting the Orleans `[OneWay]` send-flush. Migration: previously-discarded calls produce `CS4014` — add `await` or `_ =`.
- Cross-entity handlers on `ServerMetaContext` / `MetaProviderBase` switched to named delegates: `EntityCallHandler`, `EntityCallOneWayHandler`, `EntityStateHandler`.
- `EntityGrain.OnActivateAsync` cross-entity lambdas extracted to instance methods.

## [0.25.1] - 2026-05-30

- Generated typed convenience helper for **non-UserOwned** services. Was: only UserOwned services got the no-arg `client.Get{Service}Async()` shortcut; every other service forced callers into the generic `client.GetServiceAsync<{Service}ApiClient>(entityId)`. Now Open / Authorized / OwnerOnly services also get `client.Get{Service}Async(string entityId)` — same name pattern, just typed at the call site (no generic, no ApiClient class name). Emitted by `ClientServiceAggregateGenerator` next to the existing UserOwned helper.

## [0.25.0] - 2026-05-30

Removed the legacy client `SignalRConnection` from `CoreGame.SharedMeta.Transport.SignalR`. **Wire-compatible, NuGet-API-breaking** for consumers that referenced the server SignalR package for their *client* transport.

- `src/SharedMeta.Transport.SignalR/SignalRConnection.cs` and `MetaHubProxy.cs` are deleted. The server package now contains only the server-side `MetaHub` (+ `MetaMessagePackServerExtensions`). The duplicate client `SignalRConnection` had drifted behind the `.Client` variant for several releases (no `configureBuilder`, no `clientVersion`, missing the 0.24.1 phase-2 `RegisterClientSignatureAsync` override — `NotSupportedException` on connect).
- **Migration** for non-Unity .NET clients (Godot, console, load tests) that used `CoreGame.SharedMeta.Transport.SignalR` as their client transport: switch the package reference to **`CoreGame.SharedMeta.Transport.SignalR.Client`**. Same namespace (`SharedMeta.Transport.SignalR`), same class name (`SignalRConnection`), same basic constructor (`new SignalRConnection(url, accessToken)`) — typically a one-line `.csproj` change. The `.Client` constructor additionally accepts `configureBuilder` and `clientVersion` (use them for MessagePack/version-gate). Unity clients are unaffected (their `SignalRConnection` comes from the UPM package). Server hosts are unaffected (they referenced the package only for `MetaHub`).

## [0.24.2] - 2026-05-29

Version-fallback for `MinCompatibleVersion` — server-side only, no wire change.

- A client whose method `Version` isn't declared on the server now falls back to the highest arg-compatible `(Service, Alias)` body: at/above `MinCompatibleVersion` → force `ServerPatch`, below → reject. Exact-version matches still run locally (floor no longer gates them). Lets a single server declaration force old clients to patch and auto-stop once they update — no per-method override list.
- `MinCompatibleVersion` semantics redefined accordingly (was: force-patch on exact match below floor).
- `MetaClient.ClearConfigCaches()` debug command — wipes registered config providers' caches (e.g. on-disk `FileConfigCache`) so the next subscribe re-downloads. Handy after re-publishing a config under the same version in dev. New opt-in markers `IClearableConfigProvider` / `IClearableConfigCache`.
- Fix (regression since 0.24.0): `[MetaMethod(Version = N)]` with `N != 0` broke the server build — generated `ServerMetaConfiguration.g.cs` referenced a non-existent `GameMethodIds.I..._v0` constant (`CS0117`). `ServerMetaConfigurationGenerator` now reads `Version` for the dispatch/signal/migration switches, matching the emitted `GameMethodIds`.
- Patch-tracking copy auto-generation decoupled from `DeepDesync`. The `{Impl}_PatchTracked` copy (where `State` writes route through the patch wrapper) is now emitted for any force-patch-able service — a client-callable `Optimistic`/`Server`/`CrossOptimistic` method with `Version > MinCompatibleVersion` or a `[MetaConfigStructureBoundary]` config, or any `ServerPatch` method. (All three of those modes run the body on the client — `Server` replays it from the recorded buffer — so all diverge from a changed server body.) Previously only `DeepDesync` services got the copy, so force-patched clients silently received empty patches. Service bodies must be copy-compatible (wrapper-typed helpers, no `wrapper → raw` collection leaks — see `PartyService`); incompatible bodies opt out via `[MetaService(PatchTracking = false)]`. Opt-out rejects force-patch clients instead of mis-serving them, at both force-patch entry points: method-level version-fallback (negotiation → `Rejected`) and service-level `[MetaConfigStructureBoundary]` (subscribe rejected with a `FeatureRequirement` before any state mutation). Copy generation is per-**state**: every service on a force-patch-able state gets the copy (and `ResolveSiblingByType` hands out the copy under patch tracking), so a force-patched call that fans out to a sibling service on the same state (e.g. `BuyEnergy` → `EnergyService`) tracks the sibling's mutations too instead of writing the raw state and dropping them from the diff.

## [0.24.1] - 2026-05-28

Unity BestHTTP/JSON compatibility patch for the 0.24.0 handshake. No wire change — interoperates with 0.24.0.

- Client signature auto-wired into `ClientSignatureDefault.Value` (via `RegisterAllServices()` / net5+ module initializer); `MetaClientOptions.ClientSignature` no longer required. New `DisableClientSignatureNegotiation` opt-out.
- Phase-2 `RegisterClientSignatureAsync` wired across all transports (BestHTTP SignalR + HTTP-polling clients) plus a new `/register-client-signature` polling endpoint — was unimplemented → `NotSupportedException`.
- LitJson fixes: `SessionConnectMode` enum byte→int; `ReadOnlyMemory<byte>` base64 converter; `long`→`ulong` importer for signature hashes.

## [0.24.0] - 2026-05-27

Two redesigns and a server-side allocation rework. Wire-breaking: 0.23.x ↔ 0.24.0 do not mix.

- **Signature handshake** — annotated client signature replaces `ClientCapabilities`. O(1) gate, < 100 B steady-state connect (vs ~5 KB). Client-side `IServerAnnotationCache` (in-memory + Unity PlayerPrefs); cache invalidation via `serverHash`.
- **Client-owned subscriptions** — server no longer persists subscription list; client claims its set on Resume via `SessionConnectRequest.ClaimedSubscriptions`, entity grain verdicts `Continued` / `Refreshed` / `Failed` per claim. Any `Failed` routes through `IMetaSessionRecoveryHandler`.
- **Session recovery** — explicit `SessionConnectMode.{Resume,StartNew}`; `IMetaSessionRecoveryHandler` game-level callback on `SessionUnknown`; unified `TriggerRecovery` (transport-reconnect + server-pushed). Persisted RPC-ordering baseline (`LastDispatchedRequestId` + `LastCompletedRequestId`) eliminates infinite retry loops after silo restart; stale + cache-miss returns an error op instead of silent re-execution.
- **Server-side allocation rework** — `PooledPayloadRegistry` (0.23.1 opt-in, never engaged) removed (~1500 LOC, 9 files). Replaced by cached `IPayloadWriter` per grain returning `ReadOnlyMemory<byte>` over pool-rented scratch (`IServerRecordContext.AcquireWriter()`); `Immutable<ROM>` kept only at Orleans grain boundaries; `SessionManagerGrain` hot-path wrappers flipped class → struct; `_pendingPackets` aliased to persisted state; `CleanupPendingPackets*` rewritten (prefix-scan + single `RemoveRange`).
- **Fixes** —
  - `ReclaimSubscriptionAsync` truth source is `EntitySequenceNumber` alone; missing `Subscribers` entry is repaired in place (no more state-rollback after silo restart with offline-applied RPCs).
  - Client `_lastKnownEntitySeq` advances on every `CrossEntityOperations[i]`, not just the outer entity (no more spurious Refreshed after cross-entity activity).
  - `ResendPendingRequestsAsync` now awaits `Task.WhenAll(resends)` — was fire-and-forget, racing with new user actions during recovery.
  - `CleanupPendingPacketsByCount` — leftover empty `for` loop caused N-times `RemoveRange` → broadcasts lost. Fixed.
  - Unity Expedition example — server projects switched from NuGet to `ProjectReference`.

See [docs/GUIDE.md](docs/GUIDE.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for integration details and rationale.

## [0.23.1] - 2026-05-22

First actual release of the 0.23 content. The earlier `v0.23.0` tag was pushed against the v0.22.0 commit by mistake (release prep ran before the wire-refactoring branch was merged) and NuGet/UPM versions are immutable once published — bumped to 0.23.1 to ship the real changes.

Allocation-pressure overhaul on the server hot path + `ushort MethodId`-only wire (service/method/version strings removed end-to-end) + refreshed compatibility negotiation. Wire-breaking on both axes: `byte[]?` → `ReadOnlyMemory<byte>` and the method-addressing string triple gone.

Highlights:
- **Pool-backed broadcast payloads** — `PooledPayload` + `PooledPayloadRegistry` (silo-scoped, ref-counted). DI-driven `PooledPayloadOptions` (default OFF). Cluster-singleton coordinator grain assigns unique `SiloId` per silo.
- **`GrainScopedSerializer`** — per-grain scratch buffer; `IMetaSerializer.Pack<T>(T)` returns `ReadOnlyMemory<byte>` over pool-rented memory. Primitive-return cache (`DispatchResult.True/False/Int/Void`) skips serialization entirely.
- **`ushort MethodId` dispatch** — flat `switch (methodId)` jump table replaces nested string/version switches. `GameMethodIds` const table per assembly. Force-patch refcounts indexed by `MethodId` (~200 B vs ~6 KB per entity at 100 methods).
- **Compatibility negotiation** — `MetaClientOptions.ClientSignature`, `MetaTransportOptions.RequireClientSignature`, two-phase handshake. `MetaConnectionHandler` rejects un-negotiated clients on RPC/Query/Signal.
- **Fixes** — cross-entity broadcast filter on outer-caller's client; CrossOptimistic broadcast race with third-party mutators; SessionManager pool-ref leak paths on transport drop; cluster-wide signature dedup under stress; Newtonsoft `RomByteJsonConverter` on Unity HTTP polling.

References:
- [docs/ARCHITECTURE.md §4.6](docs/ARCHITECTURE.md) — hot-path allocation strategy
- [docs/ARCHITECTURE.md §4.7](docs/ARCHITECTURE.md) — wire method addressing
- [docs/GUIDE.md](docs/GUIDE.md) — opt-in pool config, serializer contract
- [com.coregame.sharedmeta/SharedMeta-AI.md](com.coregame.sharedmeta/SharedMeta-AI.md) — AI-assistant context

## [0.22.0] - 2026-05-16

Backwards-compatible multi-version operation. Old clients keep working against newer servers; new clients keep working against entities pinned at older config branches. Plus `ExecutionMode.Notification` for entity → entity fire-and-forget, and `SharedMeta.Debug.Mux` for stress tests that need many simulated players on few sockets.

### Added — Compatibility negotiation pipeline

Two-phase handshake (`ClientSignatureHash` → `RegisterClientSignature` if missing) populates a per-build `ClientCapabilities` blob; generated `*ApiClient` consults it before every call and the server-side `MetaConnectionHandler` enforces the same on receive. Covers four kinds of drift: structural state breaks, config-structure breaks, method-body version drift, method-signature drift.

- `[MetaStateVersion(Breaking = bool)]` — `Breaking = true` rejects clients below threshold with structured `IncompatibleFeatureException`.
- `[MetaMethod(Version, MinCompatibleVersion, Alias)]` — `(Alias, Version)` dispatch with `case 0` legacy fallback.
- `[MetaConfigStructureBoundary("X.Y", Reason)]` — asymmetric force-patch trigger: fires iff `clientCode < V && pinned >= V`. New client on old entity runs natively.
- `[assembly: SharedMetaCompatibilityOptions(Enabled = false)]` opt-out for projects that don't use negotiation.
- Per-subscriber broadcast tailoring: legacy subscribers get `PatchBytes`, modern subscribers get `ReplayPayload` — single execution, dual-format fan-out.
- Per-entity overlay (`EntityAugmentedCapabilities`) stacks on session-level caps; computed at subscribe time from the entity's pinned config version.

Full design in [docs/GUIDE.md § Per-Client Config Branches & State Migration](docs/GUIDE.md#per-client-config-branches--state-migration) and [com.coregame.sharedmeta/SharedMeta-AI.md](com.coregame.sharedmeta/SharedMeta-AI.md). Reference example: `examples/ClanWars` (v1 + v2 clients sharing a pinned-2.0 clan).

### Added — `ExecutionMode.Notification`

Entity → entity fire-and-forget — peer of `Signal` on the cross-entity axis. Source grain dispatches via Orleans `[OneWay]` and continues; no result recorded into replay payload. Generator emits `void {Name}(args)` on the EntityCaller (any pre-0.22 `await GetIFoo(id).BarAsync(...)` site compile-errors and migrates).

```csharp
[MetaMethod(Mode = ExecutionMode.Notification)]
Task AddPower(int delta);
```

Implicit `GenerateClientApi = false`. Removes one grain-to-grain await from the latency path when caller doesn't read target state after the call. Full contract + perf in [docs/GUIDE.md § Notification Methods](docs/GUIDE.md#notification-methods-entity--entity-fire-and-forget--0220).

### Added — `SharedMeta.Debug.Mux` transport

Debug-only transport where N logical client sessions share one physical SignalR socket. Map `app.MapMetaMuxHub("/meta-mux")` server-side; build a `MuxChannel` pool client-side and call `channel.CreateConnection(tag)` per simulator. Each `MuxConnection` implements `IConnection`, so the rest of MetaClient is unchanged. Useful for stress tests driving thousands of simulated players from one process. See [docs/GUIDE.md § Mux Transport](docs/GUIDE.md#mux-transport--high-fanout-stress-tests-0220).

### Changed — `PlayerVersionGrain` moved into `SharedMeta.Server.Core`

The default version-gate grain (consulted by `MetaConnectionHandler` on connect when the client transmits `ClientVersion`) now ships with `SharedMeta.Server.Core` instead of `SharedMeta.Auth` — no transitive ASP.NET/JWT dependency needed for the base version-gate.

### Changed — `ValidateClientCompatibleWithPins` is now permissive

Cross-version subscribe to a shared-scoped entity is **allowed** by default. Open-Closed config evolution + `[MetaConfigStructureBoundary]`-driven force-patch handle compatibility — the pre-0.22 strict reject was redundant once boundary-driven force-patch landed.

### Fixed — Query method local sync wrapper now establishes `MetaContext`

`SimplifiedApiClientGenerator.GenerateLocalQueryMethod` now wraps the body with `SetupQueryContext()` / `RestoreQueryContext(prev)` so `Context.State` / `Context.EntityId` reads always succeed (previously worked only when AsyncLocal happened to carry a context from a prior async op).

### Fixed — Query single-primitive-arg framing

`QueryClientGenerator` now uses `_serializer.CreateWriter()` / `writer.Complete()` for all arg counts and serializers, matching the server-side dispatcher's length-prefixed framing (previously raw `MemoryPackSerializer.Serialize(int)` produced 4 raw bytes that the dispatcher's `ReadWithAutoUnbox<int>` mis-interpreted).

## [0.21.1] - 2026-05-13

### Changed — `FileGrainStorage` defaults to the Orleans serializer

`FileGrainStorageOptions.UseOrleansSerializer` (new, defaults to `true`) selects how grain state is persisted on disk. The default mode matches every real Orleans storage provider (Azure Tables, Redis, ADO.NET) and works for any grain state type with `[GenerateSerializer]` — including types like `ConfigStoreGrainState` / `ConfigDirectoryGrainState` that previously failed because they had no `[MemoryPackable]` / `[MessagePackObject]` attribute. Set `UseOrleansSerializer = false` to keep the prior behaviour (state routed through `IMetaSerializer`).

**Migration:** the two modes produce incompatible byte formats. Existing `./data` directories written by 0.21.0 (MemoryPack/MessagePack bytes) cannot be read by the new default — either delete the directory, or opt back into the old format with `UseOrleansSerializer = false`.

### Added — Orleans serialization attributes on built-in and example grain states

- `ConfigStoreGrainState` and `ConfigDirectoryGrainState` now carry `[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]` — they work in both `FileGrainStorage` modes and through any standard Orleans storage provider.
- Example `ISharedState` types and nested DTOs (`GameState`, `ProfileState`, `Card`, `TablePair`, `Player`, `ExpeditionState`, Expedition `ProfileState`, `ResumeExpeditionResult`) now also carry `[GenerateSerializer]` + `[Id(n)]`. This is the recommended pattern for any project that uses a production Orleans storage provider.

### Docs — clarified transport vs persistence serialization

[docs/GUIDE.md](docs/GUIDE.md), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [com.coregame.sharedmeta/SharedMeta-UserGuide.md](com.coregame.sharedmeta/SharedMeta-UserGuide.md), and [com.coregame.sharedmeta/SharedMeta-AI.md](com.coregame.sharedmeta/SharedMeta-AI.md) now document the two channels: `IMetaSerializer` (MemoryPack/MessagePack) carries wire payloads and replay; Orleans storage providers persist grain state via the Orleans serializer and need `[GenerateSerializer]` + `[Id(n)]` on `ISharedState` and nested DTOs. The Unity UPM package already ships `Orleans.Stubs` (no-op attributes) for client-side compilation, so no client-side changes are needed.

## [0.21.0] - 2026-05-13

Server-side config-version handling reshaped in two coherent halves: a standardized versioned-config subsystem (registry + observer-driven hot reload) and a three-scope entity taxonomy (`Private` / `Shared` / `Global`) with runtime config-version pins driving per-scope semantics across subscribe, dispatch, and migration. Full design rationale and reference live in [docs/GUIDE.md § Entity Scope](docs/GUIDE.md#entity-scope-entityscope) and [§ Per-Client Config Branches & State Migration](docs/GUIDE.md#per-client-config-branches--state-migration).

### Breaking — `IMetaConfigProvider<TConfig>.CurrentVersion` removed

The pre-0.21.0 ambient "current version" property is gone — it was the framework's silent fallback for unresolved client versions, and gave migrations a way to advance state on a branch old clients couldn't handle. Per-call resolution is now always driven by the caller's client app version (real, propagated, or substituted via `IConfigVersionResolver.CurrentClientVersion` for server-internal callers). `BroadcastingConfigProvider` loses `CurrentVersion` / `SetCurrentVersion`; `ResolveLatestMatching(major, minor)` throws when no patch in the branch is published.

Generator changes: migration step conditions no longer read `_configProvider.CurrentVersion`. `CheckAndRunLazyMigrationAsync(callerClientVersion, schemaCap)` threads the caller's version into `ComputeRequiredStateSchema`, which resolves per-step via `ResolveForClient`. Redundant inner `_cv == CurrentVersion` AND-checks removed (already gated by `_migrationCap`).

### Added — `[EntityScope]` lifecycle taxonomy

`EntityScope` enum + `EntityScopeAttribute` on the state class. Default (no attribute) is `Private` — existing code unchanged. See [docs/GUIDE.md § Entity Scope](docs/GUIDE.md#entity-scope-entityscope) for per-scope semantics (subscribe model, runtime pin, optimistic-mode applicability).

### Added — `IConfigVersionResolver.CurrentClientVersion`

```csharp
public interface IConfigVersionResolver
{
    string CurrentClientVersion { get; }                // 0.21.0+ — required when configs are used
    MetaConfigVersion ResolveVersion(string stateTypeName, string entityId, MetaConfigVersion defaultVersion);
}
```

`defaultVersion` now derives from `CurrentClientVersion` via the config class's `[MetaConfigVersion]` rules. Register a single implementation in DI; required when any `IMetaConfigProvider<>` is registered or any state declares `[EntityScope(EntityScope.Global)]`.

### Added — `MetaClientOptions.ClientAppVersion` + transport plumbing

Client-side `ClientAppVersion` is now stamped on `SessionConnectRequest.ClientVersion` at `MetaClient.ConnectAsync` and re-used on auto-reconnect. Server already extracts it into the connection-scoped `CallerClientVersion` for every RPC and subscribe. `IConnection.SessionConnectAsync(..., string? clientAppVersion = null)` and `IClientDispatcher.ConnectSessionAsync(..., string? clientAppVersion = null)` overloads add the parameter (optional — existing callers continue to work). All built-in transports updated (SignalR / HttpPolling / BestHttp variants / InProcess).

### Added — `ExecutedConfigVersion` on responses and broadcasts

`RpcResponse` (`[Id(9), Key(9)]`) and `EntityBroadcast` (`[Id(11)]`) carry the `MetaConfigVersion` the server actually executed under, populated from `MetaContext.ConfigVersion`. Propagated through `CallResponse<T>` / `VoidCallResponse` / `ByteCallResponse` / `NetworkBroadcast`. `SubscribeResponse.ConfigVersion` reflects the scope-aware effective version (pin for Private/Shared, `CurrentClientVersion`-resolved for Global) so the client materializes the same config the server will dispatch under. **Client replay path** (both `MetaServiceResolver`'s foreign-service `EntityReplayDispatcher` and generated `*ApiClient.DispatchServiceBroadcast`) consumes `broadcast.ExecutedConfigVersion` and resolves the right `TConfig` for replay via `EntityConnection.ResolveConfigForBroadcast` — a lazy per-version cache backed by fire-and-forget `IClientMetaConfigProvider.GetConfigAsync`. The first broadcast at a drifted version replays under session config (one-time debug log); subsequent broadcasts at the same drifted version use the cached entry.

### Added — `IEntityGrain<TState>.ForceMigrateToFloorAsync(string floorClientVersion)`

Admin API for dropping support for an old config branch. Iterate entity IDs (sourced from the project's player DB / storage) and force-migrate each entity to the schema floor required by `floorClientVersion` (resolved via `[MetaConfigVersion]` rules). Returns `true` when migration ran. No subscriber required — works on cold or active entities. Existing active pins are not overwritten; only `state.Version` advances and is persisted.

### Added — server-side versioned config subsystem

Standardized server-side handling of multi-version configs with hot reload across silos. Closes the gap between `[MetaConfigVersion]` per-client branch rules (already in 0.19.0) and serving infrastructure, which until now was 100% user-side and reliably error-prone (cache staleness, missing fallback, no reload story without a silo restart).

**Shape:**

```csharp
// SharedMeta.Server.Core — Orleans-free contract, easy to mock in unit tests.
public interface IConfigRegistry
{
    Task<byte[]?> GetAsync(Type configType, MetaConfigVersion version);
    Task<IReadOnlyList<MetaConfigVersion>> ListVersionsAsync(Type configType);
    Task PublishAsync(Type configType, MetaConfigVersion version, byte[] configBytes);
    Task UnpublishAsync(Type configType, MetaConfigVersion version);
}
// + ConfigRegistryExtensions: typed GetAsync<TConfig> / PublishAsync<TConfig> via IMetaSerializer.
```

```csharp
// SharedMeta.Orleans — grain-backed impl.
//   IConfigStoreGrain     — one per (configType.FullName, MetaConfigVersion); holds the bytes
//   IConfigDirectoryGrain — one per configType.FullName; tracks the published version set + observer list
//   IConfigUpdateObserver — observer interface ([OneWay] callbacks)
//   GrainConfigRegistry   — IConfigRegistry façade that delegates to the grains
//   BroadcastingConfigProvider<TConfig> — IMetaConfigProvider<TConfig> with in-memory cache + observer-driven invalidation
```

**Wiring on the silo:**

```csharp
siloBuilder.ConfigureServices(services =>
{
    services.AddSharedMetaConfigVersioning();
    services.AddSharedMetaConfigProvider<ExpeditionConfig>();
    services.AddSharedMetaConfigProvider<ProfileConfig>();
});
// On startup: await serviceProvider.WarmUpConfigProvidersAsync(typeof(ExpeditionConfig), typeof(ProfileConfig));
```

**Multi-silo.** Each silo runs its own `BroadcastingConfigProvider<TConfig>` DI singleton and registers as a separate `IConfigUpdateObserver` with the per-type `ConfigDirectoryGrain`. On `PublishAsync` / `UnpublishAsync` the grain fans out `[OneWay]` callbacks to every subscriber. Dead observer references (from silo restarts before unsubscribe) are pruned lazily during fan-out. The cache-miss path always falls back to `IConfigRegistry.GetAsync`, so a dropped observer notification cannot cause stale serving — at worst the cache is briefly colder.

**Persistence.** `ConfigStoreGrain` and `ConfigDirectoryGrain` use `[PersistentState("ConfigStore", "Default")]` / `[PersistentState("ConfigDirectory", "Default")]`. The silo host picks which Orleans storage provider backs the `"Default"` name (in-memory for tests, Azure Tables / Redis / Postgres for production). Observer references are NOT persisted — they're cluster-runtime data, dropped on silo restart.

**Grain addressing.** Per-(type, version) keying for the store grain (`"{typeFullName}::{Major}.{Minor}.{Patch}"`) keeps writes contention-free and lets the cluster lazily activate only versions that are actually queried. Listing the version set is the directory grain's job.

Tests: `tests/SharedMeta.IntegrationTests/ConfigVersioningTests.cs` — 5 tests covering publish/get/list round-trip, cache hit/miss, observer-driven cache invalidation on publish, republish-invalidates-cache, and unpublish-drops-from-known-versions. Each test uses its own config type so the shared `TestCluster` fixture doesn't pollute per-test grain state.

### Added — strict `CallerClientVersion` contract

`IMetaConfigProvider.ResolveForClient(null, _)` now throws — every code path that resolves config for a client must supply a non-empty version. Server-internal callers (timers, triggers, server-only services, cross-entity calls from a non-client context) substitute `IConfigVersionResolver.CurrentClientVersion` automatically: `EntityGrain.EntityCallHandler` falls back when `MetaContextAccessor.Current` is null, and generator-emitted `GetCachedConfigForClient` for Private/Shared cold calls substitutes when no pin is set. Misconfigured server-internal callers (no resolver registered AND no client version passed) fail-loud with an actionable message instead of silently routing to `default(MetaConfigVersion)`.

### Added — `EntityScope` integration test coverage

`tests/SharedMeta.IntegrationTests/EntityScopeTests.cs` — 5 core scenarios:
- **Private + cross-entity call from a higher-version client**: pin locks both config dispatch and migration; target's schema doesn't advance.
- **Shared + new subscriber at a different patch** (same `Major.Minor`): joiner downgrades to the pinned patch; `RecordConfig` returns the pinned patch, not the joiner's.
- **Shared + incompatible `Major.Minor` joiner**: subscribe rejected with `EntityAccessDeniedException` ("Cannot join this shared session — your app version is on a different config branch").
- **Global + supported client**: subscribe succeeds; state migrates under `IConfigVersionResolver.CurrentClientVersion`; `[MetaInit]` records the server-side config at the migration step.
- **Global + unsupported (old) client**: compat gate rejects with "Your app version is too old / Please update your app".

`tests/SharedMeta.IntegrationTests/EntityScopeAdvancedTests.cs` — 4 edge-case scenarios:
- **Shared lifecycle on grain deactivation**: `ForceActivationCollection` drops the runtime-only pin; next first-subscriber re-establishes at their version (no stale pin from a previous activation).
- **Multi-config `[MetaStateVersion]` AND-gate**: schema 2 requires both `MultiConfigA ≥ 2.0` AND `MultiConfigB ≥ 2.0`. Client at "1.0.0" (ConfigA=2.0 ✓ via fixed mapping, ConfigB=1.0 ✗) does NOT migrate; client at "2.0.0" (both ✓) does. Surfaced a generator bug — see Fixed below.
- **`ForceMigrateToFloorAsync` over active pin**: admin force-migrate advances `state.Version` even when the entity has a live pin; pin itself is not touched.
- **Global + resolver flip mid-session**: server's `IConfigVersionResolver.CurrentClientVersion` changes between calls → next call dispatches under the new version, migration runs, `ExecutedConfigVersion` on the response reflects the new branch.

### Fixed — pin survives subscriber churn (now correctly drops on count→0)

`EntityGrain.UnsubscribeAsync` now calls `ClearConfigPins` when the last subscriber leaves. The earlier behaviour kept pins alive across subscriber churn for the whole grain activation, which silently made admin-published patches invisible to reconnecting clients until the grain idle-deactivated or the silo restarted. Spec from the original design discussion: "pin lives while active subscriptions exist."

- **Private** (single owner) — disconnect + reconnect within the same grain activation now re-pins at the latest available patch. Hot-fix patches via `IConfigRegistry.PublishAsync` apply on every fresh reconnect.
- **Shared** — pin holds as long as at least one subscriber remains; all-leave + re-join is a fresh session (consistent with "Shared = natural update on the next session").
- **Global** — never pinned, unaffected.

Regression test: `EntityScopeAdvancedTests.Shared_AllSubscribersLeave_PinDropsAndRePinsOnNextSubscriber`.

### Changed — async config materialization on grain activation

Generator-emitted `InitializeConfigAsync` now uses `IMetaConfigProvider.GetConfigAsync` (replacing the sync `GetConfig` call which threw on `BroadcastingConfigProvider` cache miss because synchronous fetch from a grain-backed registry isn't possible). `EntityGrain.SubscribeAsync` awaits the async path; no sync-over-async on the grain activation thread. The sync `InitializeConfig` virtual remains for back-compat — `InitializeConfigAsync` default-forwards to it.

### Changed — `MetaConfigVersion.ToString` always 3-component

`ToString` now always returns `"Major.Minor.Patch"` (e.g. `"0.1.0"` instead of dropping the zero patch as `"0.1"`). The short-form was a footgun in logs and error messages — `"no cached config for 0.1"` was indistinguishable from a hypothetical 2-component version. `Parse` still accepts both forms for backward compatibility with user-typed strings in `[MetaConfigVersion]` attributes.

### Fixed — `[MetaStateVersion]` AND-gate bypass on multi-config migrations

The generator emitted `_migrationCap = schemaCap ?? int.MaxValue` in `CheckAndRunLazyMigrationAsync`, where `schemaCap` is the method/client cap that doesn't consider the AND-gate's secondary-config side. The inner per-step guard in `RunInitAsync` only checks `targetSchema <= _migrationCap`, so a step whose primary config crossed its threshold but whose secondary did NOT would still run — exactly the case `EntityScopeAdvancedTests.MultiConfig_AndGate_OnlyMigratesWhenAllConfigsCrossThreshold` exercises. The cap now uses `required` (the AND-gate-aware value from `ComputeRequiredStateSchema`); per-step guards naturally stop at the AND-gate boundary.

### Known limitations (deferred to follow-ups)

- **No compile-time `SHMETA_OPT_GLOBAL` diagnostic.** Optimistic / CrossOptimistic on `[EntityScope(Global)]` is unsafe and currently signaled only via runtime desync. Generator needs a `DiagnosticDescriptor` infrastructure (none today). Tracked separately.
- **Sweep helper for `ForceMigrateToFloorAsync`.** The per-entity API is in place (`IEntityGrain<TState>.ForceMigrateToFloorAsync`); iterating entity IDs is project-side (Orleans doesn't expose "all grains of type"). A reusable scanner that walks the storage layer for a given state type ships in a follow-up.

Full suite: 231 passing + 0 skipped on net8.0 and net10.0, plus 411 patch-fuzz tests.

## [0.20.3] - 2026-05-10

### Added — subscription introspection on the client

Debug helper for "which entities is this client tracking, which config branch got pinned, which services are wired locally?" — the question that comes up when a desync, NRE, or unexpected RPC failure needs context about the client's view of the world.

```csharp
IReadOnlyList<SubscribedEntityInfo> snapshot = client.GetSubscribedEntities();
foreach (var e in snapshot)
    Debug.Log($"{e.EntityId} ({e.StateType.Name}, {e.ConfigType?.Name}@{e.ConfigVersion.Major}.{e.ConfigVersion.Minor})");

// or, one-liner for logs / status panels:
Debug.Log(client.DescribeSubscriptions());
// alice (ProfileState, ExpeditionConfig@2.0, [IProfileService])
// expedition-alice-1 (ExpeditionState, ExpeditionConfig@2.0, [IExpeditionService])
```

`SubscribedEntityInfo` is a read-only record exposing `EntityId`, `StateType`, `ConfigType`, `ConfigVersion`, `ServiceNames` (locally-registered API clients), `State` (live reference), and `Config` (resolved config instance). Available on both `MetaServiceResolver` and `MetaClient` (forwards). `DescribeSubscriptions()` returns a multi-line debug summary — format is intentionally not parseable. Snapshot is taken at call time; do not branch production logic on it (use the existing `GetState<T>` / `OnMutated` for live data).

Coverage: `tests/SharedMeta.IntegrationTests/SubscriptionIntrospectionTests.cs`.

Full suite: 1252 tests pass on net8.0 and net10.0.

## [0.20.2] - 2026-05-10

### Fixed — `[MetaService(DefaultConfig = true)]` cross-assembly resolution

`[MetaService(DefaultConfig = true)]` without an explicit `ConfigType` silently failed to find the `[MetaConfig(Default = true)]` class when it lived in a referenced assembly (typical Models / Services project split). The generator's `FindDefaultConfigType` in both `ServiceRegistrationGenerator` and `PatchTrackedClassGenerator` only walked `compilation.SyntaxTrees`, so cross-assembly configs were invisible. Generated `MetaServiceConfig.ConfigType` stayed null, `MetaServiceResolver.ResolveConfigAsync` short-circuited via its `if (config.ConfigType == null) return null;` guard, `Context.Config` became null at runtime, and user code NRE'd at the first `Config.X` access — identical in shape to the pre-0.17.0 silent zeroed-config bug despite that fail-loud guard being in place.

Both generators now extend the discovery walk to `compilation.References`, mirroring the pattern already used by `ContextInjectionGenerator` and `ResultComparerScanner`. Search precedence in `ServiceRegistrationGenerator`: same-namespace-in-current → anywhere-in-current → same-namespace-in-references → anywhere-in-references. `PatchTrackedClassGenerator` returns the first match (no namespace preference). The reference walk skips BCL / serializer-runtime / Orleans assemblies; SharedMeta framework assemblies are not skipped (they don't carry `[MetaConfig]` types so the namespace scan returns empty cheaply, and the previous broad `StartsWith("SharedMeta")` skip would also exclude legitimate user projects under that namespace).

**Workaround for users on 0.20.1 and earlier:** add `ConfigType = typeof(...)` explicitly on the `[MetaService]` attribute — this bypasses the discovery walk entirely. `DefaultConfig = true` keeps controlling the auto-default `TryRegisterConfigProvider` emission independently.

Regression test: `tests/SharedMeta.IntegrationTests/CrossAssemblyDefaultConfigTests.cs` with split fixture (`SharedMeta.Test.SplitConfig.Models` / `SharedMeta.Test.SplitConfig.Services`) — config in one assembly, consumer service in another, asserts `MetaServiceConfig.ConfigType` resolves to the cross-assembly type. Verified to fail before the fix, pass after.

Full suite: 1248 tests pass on net8.0 and net10.0.

## [0.20.1] - 2026-05-09

### Security — `[MetaMethod(GenerateClientApi = false)]` is now actually enforced

In 0.20.0 the flag was advisory: the typed client API was still generated, and a modified client could forge a raw `RpcCallRequest` directly through the transport and the server would dispatch it.

0.20.1 closes both halves:

1. **Client-side:** the public callable is no longer emitted in `*ApiClient.g.cs` for methods declared `GenerateClientApi = false`. Cross-entity callers (`Get{Iface}(entityId)`) and sibling callers still see them. Replay events and broadcast handlers stay — subscribed clients keep receiving state changes when other entities invoke the method cross-entity.
2. **Server-side:** the generated dispatcher rejects the call at that method's case — `"Method '…' is not callable from clients"` — when it arrived through the client-RPC boundary. Cross-entity and sibling-bypass paths are unaffected.

Tests in `tests/SharedMeta.IntegrationTests/ClientApiSecurityTests.cs`:

- `ForgedClientRpc_GenerateClientApiFalse_IsRejected` — emulates a hacked client sending a raw `RpcCallRequest` directly to `IConnection.RpcCallAsync`; server rejects, state unchanged.
- `CrossEntityCall_GenerateClientApiFalse_StillWorks` and `SiblingBypass_GenerateClientApiFalse_StillWorks` — protected method remains reachable from server-internal paths.
- `GenerateClientApiFalse_PublicMethodAbsentFromApiClient` — reflection check that the typed `*ApiClient` no longer exposes the method.

### Breaking — `IMetaProvider` slimmed

For users with custom `IMetaProvider` implementations (rare — most consume the framework-generated provider):

- Three lookup methods removed from the interface: `IsQueryMethod`, `IsSignalMethod`, `IsOpenAccessQuery`. The framework-generated provider now embeds these decisions inside its own `HandleQueryAsync` / `HandleSignalAsync` overrides.
- `HandleCallAsync` gains a `bool isClientOriginated = true` parameter so server-internal callers can opt out of the client-callable gate.
- Public `MetaContext.IsClientCall` flag (default `true`) carries the same signal to generated dispatcher cases.

## [0.20.0] - 2026-05-09

### TL;DR

- **Fix:** `GetIInventoryService(entityId).GrantItem(...)` no longer deadlocks when `entityId` is the caller's own entity (gift-to-self / send-to-anyone-with-self-id). Sibling services on the same `TState` are now invoked through a typed in-process call path — no serialization, no grain RPC.
- **New:** explicit `Get{Iface}SiblingAsync()` accessor with async per-service typed `Config` resolution. Multi-config siblings (different `[MetaConfig]` types on the same state) each see their own typed config.
- **Fix:** `[MetaInit]` re-running on every grain reactivation (state.Version wasn't seeded into the provider). Already-initialized entities skip re-init now.
- **Architectural:** server hot path on `[MetaServiceImpl]` no longer goes through `MetaContextAccessor` (AsyncLocal) — `Context` is a typed instance property set by the provider on lazy creation. Framework-internal AsyncLocal usage is intentionally retained as the ambient-execution-context primitive.
- **Generator hygiene:** entity-caller helpers (Recorder/Replayer/LocalEntityCaller/SiblingCaller/EntityCaller) emit once per `(namespace, dep)` pair instead of per consumer — multiple `[MetaServiceImpl]` classes in the same namespace can now declare the same dep without CS0111.
- **Required:** every entity-service dep declared on `[MetaServiceImpl]` MUST carry `[MetaService(StateType = typeof(...))]`. Generator emits `#error` if missing.

### Fixed — gift-to-self grain deadlock

Calling another service on the same entity used to route through Orleans cross-entity RPC: `GetIInventoryService(entityId).GrantItem(...)` resolved a grain proxy regardless of `entityId`. When `entityId` happened to be the calling entity itself (user-to-self gift, generic "send to anyone" with caller's id, etc.), Orleans deadlocked — `EntityGrain` is non-reentrant, the outer call held the grain's task scheduler while awaiting the self-call which could never start.

0.20.0 fixes this with a generator-emitted sibling-caller path plus a runtime safety-net. User code is unchanged: `GetIInventoryService(entityId).GrantItem(...)` works whether `entityId` is self or another entity.

**Generated sibling-caller (primary path).** Each `GetI{Iface}(entityId)` accessor gains a runtime self-detect branch: when `entityId == Context.EntityId` and `Context.SiblingServiceResolver` returns an instance for the requested interface, the call is dispatched on the cached sibling impl directly — typed args, no serialization, no grain RPC. Cross-grain targets (different `TState`, same entity id) still flow through the recorder path.

Generator emissions:

- `Get{Iface}(entityId)` accessor — adds self-bypass branch (server + client).
- `{Iface}SiblingCaller` class — implements the async `{Iface}EntityCaller` shape, holds a typed `I{Iface}` impl reference, forwards each method without serialization.
- `MetaProviderBase.ResolveSiblingByType(Type)` override on each generated provider — switch over hosted services, returns the cached `Get{Name}()` instance.

Server side: `MetaProviderBase.Initialize` wires `MetaContext.SiblingServiceResolver = ResolveSiblingByType` so the accessor finds the sibling at runtime.

Client side: `MetaServiceConfig.ClientSiblingFactory` (typed lambda `(ctx) => new {Impl}() { Context = (MetaContext<TState>)ctx }`) is invoked by `ICrossEntityResolver.ResolveSibling(Type, MetaContext)`. The simplified ApiClient wires `ctx.SiblingServiceResolver = type => _crossEntityResolver?.ResolveSibling(type, ctx)` in `SetContext(...)` so client-side replay flows resolve siblings the same way as the server.

**Runtime safety-net.** For paths that bypass the typed accessor (e.g. raw `Context.CallEntityAsync(...)` with a self-id), `EntityGrain.EntityCallHandler` checks `targetEntityId == this.GetPrimaryKeyString()` and routes through a new `MetaProviderBase.HandleNestedCallAsync(...)` — runs `DispatchCall` as a sub-operation under the outer `MetaContext`. New `ServerMetaContext.PushNestedOperation`/`PopNestedOperation` helpers swap the inner replay-buffer and `CrossEntityCalls` list so the outer's recording state survives.

Patch / change-tracking compatibility: sibling-call mutations share the outer's `PatchWrapper` and `ChangeTracker`. `ServerPatch` / `ServerReplace` / `Optimistic` / `CrossOptimistic` modes still ship one patch per outer call. From the client's perspective a sibling-call is indistinguishable from a private helper-method invocation in the outer service.

### Added — explicit `Get{Iface}SiblingAsync()` accessor

For every entity-service dependency declared on a `[MetaServiceImpl]` whose `[MetaService(StateType=...)]` matches the calling impl's TState, the generator emits:

```csharp
// inside ProfileService impl — InventoryService is a sibling on ProfileState
var inv = await GetIInventoryServiceSiblingAsync();
inv.GrantItem("daily_bonus", 1);
```

Returns the **original** `IInventoryService` interface (sync/async methods exactly as declared), not the async `EntityCaller` wrapper. The `await` resolves the callee's typed `Config` through its own `IMetaConfigProvider<TConfig>.GetConfigAsync` (server) — multi-config siblings each see their own typed config branch independently of the calling service.

Use the implicit `GetIInventoryService(entityId)` accessor when the target id is dynamic (potentially self); use `GetIInventoryServiceSiblingAsync()` when the intent is "this entity's own sibling".

Generated body is wrapped in `#if SHAREDMETA_SERVER` for the config-resolution branch (uses `IMetaConfigProvider<TConfig>` from `SharedMeta.Server.Core`, not referenceable from shared client-side assemblies). On the client the getter falls back to typed sibling resolution without async config refresh — adequate for single-config sibling siblings; multi-config siblings on client require server-only outer modes (`Server` / `ServerReplace` / `ServerPatch`).

### Added — `IMetaConfigProvider<TConfig>.GetConfigAsync` default method

```csharp
public interface IMetaConfigProvider<TConfig> where TConfig : class
{
    TConfig GetConfig(MetaConfigVersion version);                          // existing, sync
    Task<TConfig> GetConfigAsync(MetaConfigVersion version)                // NEW
        => Task.FromResult(GetConfig(version));
}
```

Default impl delegates to sync. Existing providers continue to work. Override `GetConfigAsync` to fetch from DB / blob storage / remote service without blocking the entity grain. Consumed by `Get{Iface}SiblingAsync()` for typed per-service config resolution.

### Fixed — `[MetaInit]` re-running on every grain reactivation

`MetaProviderBase.CurrentStateSchemaVersion` was never seeded from the persisted `state.Version` after activation — it stayed at default `0`. The generated fresh-entity-floor rule (`if (CurrentStateSchemaVersion == 0 && ...)`) then re-triggered base `[MetaInit]` on every reactivation, even when the state had been initialized in a previous session. Symptom: a freshly-restarted server would call `GenerateMap(0)` on every entity activation, regenerating the world.

Fix: `MetaProviderBase.SeedSchemaVersion(int)` public method, called from `EntityGrain.OnActivateAsync` immediately after `_provider.Initialize(...)`. Lazy migration is still deferred to subscribe / first call (we don't yet know the client version at activation), but the provider now correctly knows which schema the persisted state is at.

### Changed — `[MetaServiceImpl]` partial: `Context` is now an instance property (server hot path)

Service-impl partial classes — both `[MetaServiceImpl]` and the generated `_PatchTracked` companions — declare `Context` as a settable instance property. The provider's lazy service-getter (`Get{Name}()`) sets `service.Context = MetaContext` immediately after `new`. Dispatched method calls read the field directly instead of indirecting through `MetaContextAccessor.Get<TState>()`.

The getter falls back to `MetaContextAccessor.Get<TState>()` when the instance field hasn't been assigned. Backward-compat for code paths that still set `MetaContextAccessor.Current` (client-side ApiClient flows, `LocalEntityCaller`, signal/trigger dispatchers, server-service recorders).

User code continues to compile unchanged. Direct reads of `MetaContextAccessor.Current` from user code (rare) should migrate to `Context`.

`Context.EntityId` is now also set in client-side `ClientMetaContext` by every `ApiClient.SetContext(...)` path — without this, `entityId == Context.EntityId` self-detect would never fire on the client (regression that bit Optimistic + CrossOptimistic flows).

### Changed — typed per-service `Config` is an instance field with fallback

Each `[MetaServiceImpl]` partial's typed `Config` accessor:

```csharp
private InventoryConfig? _config;
public InventoryConfig Config
{
    get => _config ?? (InventoryConfig)Context.Config!;
    set => _config = value;
}
```

The provider sets `_config` per-call via `Get{Iface}SiblingAsync()` for siblings (each gets its own typed config). The fallback to `Context.Config` keeps Optimistic / CrossOptimistic / replay client paths working unchanged.

### Generator — entity-caller helpers emit per `(namespace, dep)`, not per consumer

Pre-0.20.0, helper classes (`{Iface}EntityCaller` interface + `Recorder` / `Replayer` / `LocalEntityCaller` / `SiblingCaller`) were emitted in every `[MetaServiceImpl]`'s `Context.g.cs`. Two impls in the same namespace declaring the same dep → CS0111 (duplicate class definitions).

0.20.0 splits the pipeline:

- `ContextInjectionGenerator` runs per `[MetaServiceImpl]` and emits **only** consumer-specific bits: `Context` / `State` / `Config` accessors, named-randoms, `PatchState`, server-service getters, `Get{Iface}(entityId)` and `Get{Iface}SiblingAsync()` getters.
- A new pipeline stage in `SharedMetaGenerator` collects unique `(consumerNamespace, depInterfaceFqn)` pairs across all `[MetaServiceImpl]`s, deduplicates, and calls `ContextInjectionGenerator.GenerateHelpersForDep(...)` per pair. Result: one `{Ns}_{Dep}_EntityCallerHelpers.g.cs` file per pair, regardless of how many consumers in that namespace declare the dep.

### Generator — `[MetaService(StateType = typeof(...))]` is required for entity-service deps

When a service interface is declared as a dependency in `[MetaServiceImpl(typeof(IService), typeof(TState), typeof(IDep))]`, the dep interface MUST carry `[MetaService(StateType = typeof(DepTState))]` — otherwise the generator emits `#error` in the consumer's compilation. Without `StateType` the framework cannot route cross-entity calls or decide whether sibling-bypass is safe.

### Architectural — `MetaContextAccessor` (AsyncLocal) is intentionally kept

The user-code hot path (every `[MetaServiceImpl]` method body) no longer goes through `MetaContextAccessor.Get<TState>()`. That was the goal of the AsyncLocal-removal effort: hidden dependencies in user code are bad, instance fields are good.

The `MetaContextAccessor` class itself remains. Framework-internal generated code — signal / trigger / subscriber dispatchers, `EntityReplayDispatcher`, `LocalInvoker`, `OrleansLobbyRequester`, `ServerPatch` / `ServerReplace` appliers, and `EntityGrain.EntityCallHandler`'s cross-entity propagation — continues to use it as the ambient execution-context primitive (analogous to `Activity.Current` / `SynchronizationContext.Current`). Replacing it with method-parameter threading would be API churn for marginal gain on those paths.

The instance `Context` getter retains an AsyncLocal fallback so not-yet-migrated framework paths (e.g. `EntityReplayDispatcher` building a transient impl) keep working. Transient impls now set the instance `Context` directly via object initializer where it's straightforward, incrementally narrowing the surface that depends on the fallback.

### Test coverage

15 new sibling-execution integration tests in `tests/SharedMeta.IntegrationTests/SiblingExecutionTests.cs`:

- Implicit and explicit sibling getters (gift-to-self, by-name)
- Outer modes: `Server`, `ServerPatch`, `ServerReplace`, `CrossOptimistic`, `Optimistic`
- `[Tracked]` field via sibling, `[NamedRandom]` and `Context.ServerRandom` via sibling
- Recursive sibling A → B → A
- Sibling → real cross-entity to a different entity
- Multi-config sibling (`AltConfigService` with `CounterAltConfig`, distinct from `CounterConfig`)
- Multiple sibling calls in the same outer (cumulative mutations)
- Sibling throws after partial mutation (documented: no implicit rollback)
- Complex return type pass-through (`List<CounterOperation>`)
- ServerRandom record/replay symmetry
- By-reference pass-through (proves no serialization on sibling-bypass — also documents why `[Transformer]` doesn't apply to siblings)

Full suite: 1238 tests pass (`dotnet test SharedMeta.slnx`).

### Migration notes

- **No user code changes required** for single-config sibling support. Existing `GetI{Iface}(entityId)` calls continue to work with the new self-detect built in.
- **Multi-config siblings** require explicit `[MetaService(ConfigType = typeof(...))]` on the dep interface and a registered `IMetaConfigProvider<TConfig>` for each config type. Cannot use `DefaultConfig = true` for both services on the same state.
- **Direct `MetaContextAccessor.Current` reads** in user code (rare) should migrate to `Context` — the impl partial's instance property handles both server and client paths and is the documented user-facing API.
- **Cross-entity dep declarations** without `[MetaService(StateType = typeof(...))]` will fail compilation. Add the attribute to existing dep interfaces.

## [0.19.2] - 2026-05-08

### Changed

Version up. (Released by mistake under tag `v0.19.1`. Superseded by 0.20.0.)

## [0.19.2] - 2026-05-08

### Changed

Version up. (Released by mistake under tag `v0.19.1`. Superseded by 0.20.0.)

## [0.19.1] - 2026-05-07

### Added — `EntityGrainOptions.FreshRandomSeedFactory` for entropy-driven seed injection

Pre-0.19.1, `MetaProviderBase.Initialize` seeded fresh entity randoms (`server`, `optimistic`, `[NamedRandom]` slots) from a deterministic string `"{entityId}:{streamName}"`. That meant recreating an entity with the same id (profile reset → expedition counter reused, recycled per-game grain id, etc.) produced the **same random stream** — same map, same shuffle, same drops.

The seed is consumed locally by `MetaRandom.FromString` on the server and never sent to the client (clients receive the post-seed `MetaRandom` internal state via `SubscribeResponse`), so injecting non-deterministic entropy is replay-safe. The framework now exposes a hook:

```csharp
services.Configure<EntityGrainOptions>(o =>
{
    // Mix in non-deterministic entropy when seeding fresh randoms.
    o.FreshRandomSeedFactory = (entityId, streamName) =>
        $"{entityId}:{streamName}:{DateTime.UtcNow.Ticks:x}:{Random.Shared.NextInt64():x}";
});
```

Default behaviour (factory not set) is unchanged — deterministic `"{entityId}:{streamName}"` so existing tests keep passing. `[NamedRandom(Seed = "literal")]` continues to bypass both paths by design (the attribute exists specifically to pin a stream to a fixed seed across all entities).

Internally, `MetaProviderBase` exposes `protected virtual string CreateFreshRandomSeed(string streamName)` which derived providers can override directly when option-based wiring isn't enough.

The Expedition example now opts into entropy-based seeding in `Expedition.Server/Program.cs`.

## [0.19.0] - 2026-05-06

Per-client config versioning and state schema migration. Breaking storage change: `EntityGrainState.ConfigVersion` removed, `MetaConfigVersion` extended from `Major.Minor` to `Major.Minor.Patch`. Existing entities deserialize cleanly because `MetaConfigVersion` is no longer persisted on the entity grain (config is now resolved per-call from the connected client's app version).

### Added — Per-client config branches via `[MetaConfigVersion]`

Declare on a config class which client app version maps to which config version. The framework resolves each connecting client to its appropriate branch and pins `Context.Config` per-call:

```csharp
[MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]   // 1.x clients → 1.x configs
[MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]   // 2.x clients → 2.x configs
public class ExpeditionConfig { ... }
```

Pattern grammar — `Major.Minor.Patch` with three component forms:

- **Literal** — `2.0.5` matches exactly.
- **Capture** — `x` matches any value AND propagates from `Client` to `Config` (so `Client="1.x.*", Config="1.x.*"` routes 1.5.0 → 1.5.0 and 1.6.2 → 1.6.2 with one rule).
- **Latest range** — `2.2+` matches `2.2` or higher within the same major.
- **Wildcard** — `*` matches any version (terminal).

Resolution picks the most-specific rule (literal > capture > range > wildcard, then by component depth).

`IMetaConfigProvider<T>` gained:
- `GetConfig(MetaConfigVersion)` — fetch a specific historical version (used both at runtime and for download URLs).
- `ResolveLatestMatching(major, minor)` — pick the latest patch for a (major, minor) range.
- `GetDownloadUrl(version)` — optional, used by `DownloadingConfigProvider<T>` on the client.

Generated `MetaProvider` now caches per-call config in a two-level dictionary keyed by `clientVersion → resolvedVersion → TConfig`, invalidated when the provider's `CurrentVersion` advances (runtime patch deploy).

### Added — State schema migration via `[MetaStateVersion]` + per-client cap

Declare migration breakpoints on a state class. The framework runs `[MetaInit]` once per breakpoint, with `Context.Config` pinned to that step's transition version (not the latest), so each migration sees the config it was authored against:

```csharp
[MetaStateVersion(2, "2.0", typeof(ExpeditionConfig))]   // schema 2 needs config >= 2.0
[MetaStateVersion(3, "3.0", typeof(ExpeditionConfig))]   // schema 3 needs config >= 3.0
public partial class ProfileState : ISharedState { ... }
```

Migration is **client-aware** — a 1.x client connecting to a fresh entity does NOT trigger a 2.0 migration just because the server's `_configProvider.CurrentVersion` is 2.0. Activation no longer drives `[MetaInit]`; init/migration is deferred to:

- **`SubscribeAsync`** — runs `[MetaInit]` capped to the subscriber's resolved config branch.
- **`HandleCallAsync`** / **`HandleQueryAsync`** — lazy migration capped to `RpcCall.CallerClientVersion` (and per-method `[MinStateVersion]` when set).

The per-entity `IsClientConfigCompatible` gate still rejects subscribes from clients on a config branch below the entity's persisted schema (e.g. profile already at schema 2, client on 1.x).

### Added — `[MetaInit]` two-arg form + `Context.Version` / `Context.ConfigVersion`

`[MetaInit]` now accepts an optional second parameter — the *target* schema for the current step. Use it to write idempotent migrations without tracking step number by hand:

```csharp
[MetaInit]
public Task<int> Init(int version, int target)
{
    if (version < 1 && target >= 1) { /* base init */ }
    if (version < 2 && target >= 2) { /* 1→2, Context.Config pinned to 2.0 */ }
    return Task.FromResult(Math.Max(version, target));
}
```

The legacy single-arg form (`Init(int version)`) still works — generator detects the parameter count and emits the matching call shape. No changes required to existing services.

`MetaContext` now exposes:
- `Context.Version` — current state schema version (during a migration step, this is the source version).
- `Context.ConfigVersion` — the `MetaConfigVersion` currently pinned (matches `Context.Config`).

### Added — `[NoMigrate]` and `[MinStateVersion(N)]`

Per-method migration control on `[MetaMethod]`s:

- **`[NoMigrate]`** — the call skips lazy migration entirely and pins `Context.Config` to the schema-floor branch (the highest config branch that does not require migration past the entity's persisted schema). Use for cross-entity "administrative" methods like inbox/gift sending — sending a gift to a profile shouldn't force-upgrade that profile if its owner is still on an older client.
- **`[MinStateVersion(N)]`** — caps migration at schema N. If the entity is below N, migrate up to N (no further); if at or above, no migration runs.

```csharp
[MetaMethod(Mode = ExecutionMode.Server)]
[NoMigrate]
void DepositGift(GiftItem item);
```

### Added — `MaxClientVersion` + per-PlayerId downgrade tracking

`MetaTransportOptions` gained `MaxClientVersion`. Combined with the rewritten `ClientVersionPolicy.Validate` (which now uses inclusive Min/Max bounds rigorously instead of `clientMajor == serverMajor`), this lets servers explicitly support a client-version range without depending on the server's own version.

`IPlayerVersionGrain` records the highest client version a player has ever connected with — subsequent connects from a *lower* version are rejected with a clear "downgrade not allowed" error. Stored as two ints in `PlayerVersionGrainState` (no string parsing on the hot path).

### Added — `DownloadingConfigProvider<T>` for client-side config delivery

The Unity client can now resolve and download the right config for its app version on connect:

```csharp
Client.Resolver.RegisterConfigProvider<ExpeditionConfig>(
    new DownloadingConfigProvider<ExpeditionConfig>(
        urlResolver: Client.ConfigDownloadUrlResolver(typeof(ExpeditionState).FullName!),
        downloader:  UnityConfigDownloader.DownloadAsync,
        serializer:  Client.Serializer));
```

`UnityConfigDownloader.DownloadAsync` captures the Unity main-thread `SynchronizationContext` via `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` and posts `UnityWebRequest` construction back to it, so async resolution that resumes on a threadpool thread (after `ConfigureAwait(false)` upstream) doesn't crash with `UnityException: Create can only be called from the main thread`.

### Added — `RpcCall.CallerClientVersion` + `MetaContext.CallerClientVersion`

The connecting client's app version is now propagated through SubscribeAsync and into every RpcCall, enabling per-call config resolution and per-call migration capping. `MetaContext.CallerClientVersion` mirrors it for the duration of the dispatch so cross-entity calls can carry it forward — without this, a 1.x session whose Profile cross-calls a fresh Expedition entity would see `clientVersion=null` on the target → migration cap defaults to the provider's `CurrentVersion` → unwanted schema-2 migration on what should have been a 1.x interaction.

### Changed — Activation no longer drives `[MetaInit]`

Previously `EntityGrain.OnActivateAsync` called `_provider.InitializeStateAsync(state.Version)`, which pre-migrated fresh entities to the latest schema before any client subscribed. This locked older clients out of fresh entities (their resolved config branch couldn't satisfy the new schema's compatibility gate).

Now activation only sets up the provider; first-time init and lazy migration run from `SubscribeAsync` / `HandleCallAsync`, capped to the connecting client's branch. For `[MetaInit]` services without `[MetaStateVersion]`, the generated provider emits a minimal `CheckAndRunLazyMigrationAsync` that triggers base init exactly once on first interaction.

### Changed — `MetaConfigVersion` is now `Major.Minor.Patch`

Added `Patch` field with `[Id(2), Key(2), MemoryPackOrder(2)]`. Adds `Parse(string)`, comparison operators, and `default == (0,0,0)`. Kept `[MemoryPackable]` (not `VersionTolerant`) — `MEMPACK041` rejects VersionTolerant on unmanaged structs.

### Removed — `EntityGrainState.ConfigVersion`

The persisted per-entity config pin is gone. Config is resolved per-call from the connecting client's app version, so a single entity grain serves multiple branches without re-activation. `Id(6)` is reserved as a tombstone in `EntityGrainState` to keep the serialization contract stable.

### Fixed — `OnDisconnectedAsync` crash on transport-level disconnect

`OnDisconnectedAsync` previously called `GrainFactory.GetGrain<>(playerId)` even when `playerId` was empty (transport disconnect before SessionConnect). Now early-returns when `!IsSessionConnected`.

## [0.18.0] - 2026-05-04

### Added — `IMetaResultComparer<T>` for structural result comparison

Pre-0.18.0 the generated Optimistic / CrossOptimistic / Server ApiClient methods compared local and server return values **byte-for-byte** through `Span.SequenceEqual` on the serialized payloads. That works fine for primitives and POCOs with stable serialization, but breaks for return types whose byte representation is not canonical for their value:

- `Dictionary<,>` — enumeration order depends on insertion sequence and hash buckets, so two semantically-equal dictionaries can produce different bytes when the inserts ran in different orders on client vs server.
- Types containing floats — `-0.0` vs `+0.0`, NaN payloads, denormals all fail bytewise comparison while passing structural equality.
- Types whose byte form depends on something the impl method doesn't actually care about (e.g. `HashSet<>`).

The fix is opt-in: implement `IMetaResultComparer<T>` for the affected return type, and the source generator rewrites the comparison path for every method returning `T` to call the comparer instead of comparing bytes.

```csharp
// Marker interface — discovered by the generator at compile time.
public class PendingGrantsComparer : IMetaResultComparer<PendingGrants>
{
    public bool AreEqual(PendingGrants server, PendingGrants local) =>
        DictsEqualByContent(server.Currencies, local.Currencies)
        && DictsEqualByContent(server.Resources, local.Resources)
        && /* ... */;
}
```

No registration required. The generator scans the compilation (own assembly + non-system referenced assemblies) for public, non-abstract, parameterless-ctor classes implementing `IMetaResultComparer<T>`, picks the winner per target type by `[ResultComparer(Priority = N)]` (default 0; ties at top priority produce a `#error` directive in the generated ApiClient naming all candidates so the build fails with an actionable message), and emits one static field per used return type:

```csharp
private static readonly IMetaResultComparer<PendingGrants> _resultComparer_MyGame_PendingGrants
    = new MyGame.PendingGrantsComparer();
```

Each Optimistic / CrossOptimistic / Server method dispatcher then routes through it:

```csharp
// Server method — serverResult is already deserialized for the call body
if (!_resultComparer_MyGame_PendingGrants.AreEqual(serverResult, localResult)) {
    _diagnostics?.OnResultMismatch(...);
    var localResultBytes = MemoryPackSerializer.Serialize(localResult);  // lazy, only on mismatch
    _ = _network.SendDesyncReportAsync(...);
    throw new DesyncException(...);
}

// Optimistic continuation — server bytes deserialized in try/catch up front
PendingGrants serverResult = default!;
bool serverDeserializedOk = false;
try { serverResult = MemoryPackSerializer.Deserialize<PendingGrants>(t.Result.ResultBytes)!; serverDeserializedOk = true; }
catch (Exception) { }
if (!serverDeserializedOk || !_resultComparer_MyGame_PendingGrants.AreEqual(serverResult, localResult)) { /* mismatch */ }
```

Notable properties:

- **Happy path is faster than 0.17.0.** Without a comparer, the byte path always serializes `localResult` to compare. With a comparer, `localResult` is serialized only on the mismatch branch (for the desync follow-up report) — one Pack saved per Optimistic call when the comparer accepts.
- **Mismatch path includes one Pack** (for the report) instead of zero — net loss is amortized by mismatches being rare.
- **Server-result deserialization failure is treated as a mismatch.** If the bytes don't decode, `OnResultMismatch` fires with `default(T)` for the server side rather than swallowing the exception silently.
- **Composes cleanly with deep desync.** The `OnPatchDesync` path is unaffected — patch CRCs still compare state mutations, comparers only affect `OnResultMismatch`.

### Added

- **`SharedMeta.Core.Diagnostics.IMetaResultComparer<in T>`** ([Runtime/Core/Diagnostics/IMetaResultComparer.cs](com.coregame.sharedmeta/Runtime/Core/Diagnostics/IMetaResultComparer.cs)) — single-method interface (`bool AreEqual(T server, T local)`). Implementations must be deterministic and thread-safe (the Optimistic continuation calls them from the threadpool).
- **`SharedMeta.Core.Diagnostics.ResultComparerAttribute`** — optional, on the comparer class. `NoAutoRegister = true` opts a comparer out of generator discovery; `Priority = N` resolves ambiguity when multiple comparers exist for the same `T` (highest wins; ties produce a build-time `#error`).
- **`ResultComparerScanner`** ([src/SharedMeta.Generator/Generators/ResultComparerScanner.cs](src/SharedMeta.Generator/Generators/ResultComparerScanner.cs)) — Roslyn scanner the generator runs against `compilation.Assembly.GlobalNamespace` plus non-system `compilation.References`. Mirrors the discovery convention used by transformers (`IArgumentTransformer<,>`).
- **Integration tests** ([tests/SharedMeta.IntegrationTests/ResultComparerTests.cs](tests/SharedMeta.IntegrationTests/ResultComparerTests.cs)) — symmetric pair: comparer returning `true` swallows desync despite divergent bytes (`System.Random` in impl), comparer returning `false` surfaces desync despite identical bytes.

### Changed

- **`SimplifiedApiClientGenerator`** — at the top of `Generate`, scans the compilation for comparers and resolves a per-method winner. Emits `#error` directives for ambiguous types (one line per affected return type, listing all candidates with their priorities). Adds a static field per used target type. The four mismatch-detection sites (Server / Optimistic / CrossOptimistic / OptimisticSync) branch on comparer presence: comparer path calls `AreEqual` and serializes `localResult` lazily for the desync report; byte path is unchanged.
- **`GenerateOptimisticResultDeserialization`** — accepts an optional `ResultComparerInfo`. When present, deserializes the server result up-front in a try/catch and gates the mismatch on either deserialization failure or `!comparer.AreEqual(...)`. When absent, falls through to the existing byte-comparison emission.

### Migration

Nothing required. The feature is purely additive — projects without an `IMetaResultComparer<T>` implementation see the same byte-comparison behavior as 0.17.0. To opt a return type in: drop a class implementing `IMetaResultComparer<T>` anywhere reachable from the assembly that compiles the corresponding `[MetaService]` interface (typically the Shared assembly), rebuild, done.

## [0.17.0] - 2026-05-02

### ⚠ Breaking — config delivery is now fail-loud

Pre-0.17.0 the generator unconditionally emitted `resolver.TryRegisterConfigProvider<TConfig>(new StaticConfigProvider<TConfig>(new TConfig()))` for every service that declared a config type, and `MetaServiceResolver.ResolveConfigAsync` returned `null` (with a warning) when no provider was registered. Together these two behaviors silently substituted an empty default-constructed config — bugs hid for hours: `Context.Config.X` looked legitimate but contained zeroed fields, NREs detonated deep inside service code, the warning got drowned in regular console output. Two changes close that:

1. **Generator no longer auto-registers a default-constructed `StaticConfigProvider<TConfig>` unconditionally.** Auto-registration is now opt-in via `[MetaService(DefaultConfig = true)]` — the flag is the explicit "I am OK with `new TConfig()` as a fallback" contract. Services that declare `ConfigType = typeof(X)` without `DefaultConfig = true` require an explicit `resolver.RegisterConfigProvider<X>(...)` call from the host.
2. **`MetaServiceResolver.ResolveConfigAsync` now throws `InvalidOperationException` when no provider is registered**, instead of warning-and-returning-null. The exception text names the missing config type, the failing service, the entity, and prints the two recommended registration recipes (`StaticConfigProvider` for preloaded instances, `DownloadingConfigProvider` for server-pushed bytes).

Failure now happens at the **first subscribe** — easy to spot, exact stack trace, actionable message. No more silent zeroed config.

### Migration

If your build broke after the bump, you have two paths depending on intent:

**(A) You always want the explicit registration** (recommended — works for both LocalBackend and real servers):

```csharp
// Before RegisterAllServices() is fine; RegisterConfigProvider is clobbering — order
// relative to RegisterAllServices doesn't matter, the explicit call always wins over
// any auto-emitted default that DefaultConfig = true might add.
client.Resolver.RegisterConfigProvider<MyConfig>(
    new StaticConfigProvider<MyConfig>(loadedConfig));

// or for server-driven configs:
client.Resolver.RegisterConfigProvider<MyConfig>(
    new DownloadingConfigProvider<MyConfig>(
        urlResolver: client.ConfigDownloadUrlResolver(typeof(MyState).FullName!),
        downloader:  UnityConfigDownloader.DownloadAsync,
        serializer:  client.Serializer,
        cache:       new FileConfigCache<MyConfig>(cacheDir, client.Serializer)));
```

**(B) An empty `new MyConfig()` truly is acceptable as a fallback** (the legacy behavior — opt in explicitly):

```csharp
[MetaService(StateType = typeof(MyState), DefaultConfig = true)]
//                                        ^^^^^^^^^^^^^^^^^^^
//        the generator sees this and emits TryRegisterConfigProvider<MyConfig>(...)
//        with new MyConfig() as before — same auto-default, but now requested.
public interface IMyService : IMetaService { ... }
```

If you're seeing the new exception and `MyConfig`'s ctor genuinely produces a usable default — option (B) is one attribute change. If `MyConfig` requires actual data (most cases) — option (A) is the right answer.

### Changed

- **`ServiceRegistrationGenerator`** — `Generate` now tracks `usesDefaultConfig` separately from `configTypeFullName`. Emission of `TryRegisterConfigProvider<TConfig>(new StaticConfigProvider<TConfig>(new TConfig()))` is gated by `usesDefaultConfig`; explicit `ConfigType = typeof(X)` without `DefaultConfig = true` produces no auto-fallback.
- **`MetaServiceResolver.ResolveConfigAsync`** — missing provider raises `InvalidOperationException` with an actionable message and registration recipes; the previous `MetaLog.Warning` + `return null` path is gone.

### Fixed

- **`[MetaMethod(SkipServerOnFalse = true)]` was silently ignored by `SimplifiedApiClientGenerator`** since the generator was rewritten — the attribute parsed and the docs described it, but the codegen always emitted an unconditional `_ = _network.CallBytesAsync(...)` for Optimistic methods. Old `XApiClientGenerator` had it; the rewrite lost it. Now `SimplifiedApiClientGenerator.GenerateMethod` parses `SkipServerOnFalse`, threads it into `GenerateOptimisticMethod` / `GenerateOptimisticMethodSync`, and wraps the fire-and-forget RPC in `if (!EqualityComparer<T>.Default.Equals(localResult, default!)) { ... }` for both async and sync overloads — when local impl returns the default value (typically `false` for validation-style methods), the RPC is short-circuited entirely. Compile-time validation (`#error`) now also rejects: (a) `SkipServerOnFalse = true` on a `void` method (no return value to compare against `default`), and (b) `SkipServerOnFalse = true` combined with an explicit non-Optimistic `Mode`. Coverage: new `SkipServerOnFalseTests` integration class with three scenarios (true → server receives, false → server skipped, mixed → only the trues land).

## [0.16.0] - 2026-04-30

### Added

- **`[GeneratedFromMetaMethod(typeof(IFoo), "Bar")]` on every generated client method** ([Runtime/Core/Attributes.cs](com.coregame.sharedmeta/Runtime/Core/Attributes.cs)). All four generators that produce client-side mirrors of `[MetaMethod]` now stamp this attribute on each emitted method, providing a stable, name-convention-independent link back to the original interface method:
  - `SimplifiedApiClientGenerator` — `*ApiClient.{Name}Async` / `*ApiClient.{Name}Sync` / `*ApiClient.{Name}Signal`
  - `QueryClientGenerator` — `*EntityQueryApi.{Name}Async`
  - `ContextInjectionGenerator` — `{Interface}EntityCaller.{Name}Async`, `{Service}EntityRecorder.{Name}Async`, `{Service}EntityReplayer.{Name}Async`, `{Service}LocalEntityCaller.{Name}Async`
- The attribute exposes `ServiceInterface` (`Type`) and `MethodName` (`string`), giving downstream tooling a `typeof()`-anchored identity that follows refactor-rename of the interface type. The SharedMeta Rider plugin (≥ 0.2.0) consumes this to bridge **Find Usages** between the user-authored `[MetaMethod]` and every generated counterpart, including cross-entity callers — the previous naming-heuristic approach missed `EntityCaller` because those types live in the consuming impl class's namespace, which the plugin couldn't predict.

### Migration

No source changes required — the attribute is purely additive on generated code. Old `.g.cs` regenerates with the attribute on next build. Tooling that depends on it (Rider plugin) must be ≥ 0.3.1.

## [0.15.1] - 2026-04-29

### Fixed

- **`PlayerPrefsTokenStorage` now scopes its keys by deviceId.** Previously every instance keyed PlayerPrefs only by `Application.identifier`, so two clients on one machine — or one client with `UseRandomDeviceId` — shared a single "current token" slot, and the second `EnsureAuthenticatedAsync` call would reuse the JWT cached for the first PlayerId. New ctor: `new PlayerPrefsTokenStorage(deviceId)`. Parameterless ctor still works and produces the same keys as before — backward compatible.
- Wizard-emitted client and the Expedition Unity example updated to use the scoped form.

## [0.15.0] - 2026-04-26

### Changed — client config delivery rewritten around `IClientMetaConfigProvider<TConfig>`

The pre-0.15.0 chain of four cooperating knobs — `MetaServiceConfig.ConfigFactory` + `MetaServiceResolver.ConfigCache` + `MetaServiceResolver.ConfigDownloader` + `MetaServiceResolver.ConfigDownloadUrlFactory` — collapsed into a single registration point per config type:

```csharp
// Before (0.14.x): three properties on the resolver, plus generator-emitted ConfigFactory
resolver.ConfigCache = new FileConfigCache("./cache", serializer);   // untyped
resolver.ConfigDownloader = new HttpConfigDownloader();
resolver.ConfigDownloadUrlFactory = (typeName, ver) => connection.GetConfigDownloadUrlAsync(typeName, ver);
// + generator-emitted MetaServiceConfig.ConfigFactory = () => new ExpeditionConfig();

// After (0.15.0): one provider per TConfig
resolver.RegisterConfigProvider<ExpeditionConfig>(new DownloadingConfigProvider<ExpeditionConfig>(
    urlResolver: client.ConfigDownloadUrlResolver(typeof(ExpeditionState).FullName!),
    downloader: url => http.GetByteArrayAsync(url),
    serializer: client.Serializer,
    cache: new FileConfigCache<ExpeditionConfig>("./cache", client.Serializer)));
```

Generator-emitted `Add{Service}Services()` extensions now also call `resolver.TryRegisterConfigProvider<TConfig>(new StaticConfigProvider<TConfig>(new TConfig()))` — non-clobbering, so an explicit `RegisterConfigProvider` call placed **before** `RegisterAllServices()` wins. Out of the box, services that declare a `[MetaService(ConfigType=...)]` or `[MetaConfig(Default=true)]` get a bundled-config provider for free without any wiring.

### Added

- **`IClientMetaConfigProvider<TConfig>`** — single point of materialization. `Task<TConfig> GetConfigAsync(MetaConfigVersion version)`. Built-ins: `StaticConfigProvider<TConfig>`, `DownloadingConfigProvider<TConfig>`, `CompositeConfigProvider<TConfig>` (primary + fallback chain).
- **`IClientMetaConfigCache<TConfig>`** — typed cache surface; `FileConfigCache<TConfig>` is the bundled disk-backed implementation.
- **`MetaClient.ConfigDownloadUrlResolver(string stateTypeName)`** — convenience helper returning a `Func<MetaConfigVersion, Task<string?>>` over `Connection.GetConfigDownloadUrlAsync` for use as `DownloadingConfigProvider`'s `urlResolver`.
- **`UnityConfigDownloader.DownloadAsync(string url)`** — Unity-friendly `Func<string, Task<byte[]>>` over `UnityWebRequest`.
- **`IMetaServiceResolver.RegisterConfigProvider<TConfig>` / `TryRegisterConfigProvider<TConfig>`** — overwriting and non-clobbering registration. Stored as a typed closure built once at registration time so the hot path stays reflection-free.

### Removed (hard-deleted, no `[Obsolete]` shim)

- `IMetaConfigCache`, `IMetaConfigDownloader` interfaces.
- `MetaServiceResolver.ConfigCache`, `.ConfigDownloader`, `.ConfigDownloadUrlFactory` properties.
- `MetaServiceConfig.ConfigFactory` field.
- `HttpConfigDownloader` class — replaced by passing `url => httpClient.GetByteArrayAsync(url)` to `DownloadingConfigProvider<TConfig>`.
- `UnityConfigDownloader` is no longer a class implementing `IMetaConfigDownloader` — same name now scopes a `static DownloadAsync(string)` helper.

## [0.14.1] - 2026-04-26

### Fixed

- **`*ServiceExtensions.g.cs` no longer fails to compile when the state type and the service interface live in different namespaces.** `ServiceRegistrationGenerator` was emitting the `PatchApplier` lambda with an unqualified `{StateName}PatchApplier` reference. The applier class is generated in the *state's* namespace, while the extensions file only `using`s the *service interface's* namespace — so when the two namespaces differed, the build broke with `CS0103: The name '{State}PatchApplier' does not exist in the current context`. The bundled CardGame example colocates state and services in one namespace, which masked the bug. The reference is now fully qualified as `{stateTypeFullName}PatchApplier` ([ServiceRegistrationGenerator.cs:141](src/SharedMeta.Generator/Generators/ServiceRegistrationGenerator.cs#L141)).

## [0.14.0] - 2026-04-26

### Added — multi-service-on-entity state propagation

- **`EntityStateContainer<TState>` — shared state holder per entity** (`Runtime/Core/Client/EntityStateContainer.cs`). Owns the current state object, the `MutationCount` counter, and the `OnMutated` event. Every API client subscribed to the same entity now points at the same container, so:
  - **Foreign-service broadcasts update local state for ALL execution modes** — `Optimistic`, `Server`, `CrossOptimistic`, `ServerPatch`, `ServerReplace`. The entity-level handler in `MetaServiceResolver` applies state-data when the broadcast carries it; for pure replay broadcasts (Optimistic / Server / CrossOptimistic without state-data) it falls back to a generator-emitted `EntityReplayDispatcher` that spins up the foreign service's impl class on the fly and re-runs the method against the shared state. **No matching ApiClient required on the receiver.** Pre-0.14.0 the per-ApiClient `ServiceName` filter silently dropped foreign broadcasts in every mode; that gate is gone.
  - **No drift after `ServerReplace`.** When one ApiClient's wholesale-replace path swaps the state instance, every other ApiClient on the entity sees the new instance through the shared container — pre-0.14.0 the non-receiving ApiClients kept pointing at the stale instance.
  - **`MutationCount` is now shared per entity**, not per ApiClient. Every API client subscribed to the entity returns the same value; the counter bumps exactly once per mutation regardless of how many ApiClients are subscribed. The 0.13.1 polling semantic is preserved but more useful — `if (api.MutationCount != lastSeen) Invalidate();` now catches mutations from any service on the entity.
  - **`OnStateMutated` fires on every API client on the entity in lock-step** — sourced from `EntityStateContainer.OnMutated`. Fires on the same set of mutations as `MutationCount` bumps (foreign-service broadcasts included).

- **`MetaServiceResolver.GetStateContainer<TState>(entityId)`** — direct access to the shared container. Useful for entity-only consumers (e.g. UI views) that don't need a full ApiClient.

- **`IEntityStateContainer`** — non-generic surface so the resolver can drive the container without knowing `TState` at runtime. Exposes `State`, `MutationCount`, `OnMutated`, `NotifyMutated`, `ReplaceObject`.

- **`MetaServiceConfig.StateContainerFactory` / `PatchApplier` / `EntityReplayDispatcher`** — generator-emitted callbacks. `StateContainerFactory` wraps a deserialized state into a typed container without reflection. `PatchApplier` applies ServerPatch byte payloads to the shared state; the entity-level handler in `MetaServiceResolver` activates `ChangeTracker` around the call so `[Tracked]` field setters touched by the patch fire `Tracked{State}.OnChanged`. `EntityReplayDispatcher` is the foreign-service replay path — it instantiates the service's impl class, sets up `ClientMetaContext` with replay context (random + named randoms + config), activates `ChangeTracker`, dispatches the broadcast's method against the shared state, and `FlushAndNotify`s. Skips Query/Signal methods (they don't broadcast).

### Changed (breaking for generated code only)

- Generated `*ApiClient.g.cs` constructor signature: third positional parameter is now `EntityStateContainer<TState>` instead of `TState`. Source-compatible for callers because nobody constructs API clients directly — `MetaServiceResolver.GetServiceAsync` does it via the generated factory. Rebuild with the new generator DLL and any consumer code keeps working.
- `MetaServiceConfig.ApiClientFactory` third positional parameter now receives an `IEntityStateContainer` (boxed) instead of the raw state object. Generator-emitted lambdas cast inside.
- `MutationCount` is no longer per-ApiClient (`int { get; private set; }`) — it now delegates to the shared container.
- `OnStateMutated` is no longer fired manually from per-method code paths; it sources from `EntityStateContainer.OnMutated`. Firing order is preserved everywhere a setter actually runs (per-method paths, foreign-service replay, ServerPatch application): `Tracked{State}.OnChanged` first via `_tracker.FlushAndNotify()`, `OnStateMutated` after via `container.NotifyMutated()`. The one path that differs is `ServerReplace` wholesale-replace at entity level — the container is replaced with a freshly deserialized instance (no setter calls happen on the old one), so only `OnStateMutated` fires and `Tracked{State}.OnChanged` has nothing to notify (same as pre-0.14.0).

### Notes

- The foreign-service replay path requires that the impl class for the foreign service is reachable from the client assembly (it lives in shared code) and that its constructor is parameterless. Cross-entity calls (`Context.GetI{Service}(otherEntityId)`) inside foreign-service methods are not replayed during entity-level dispatch — that scenario still requires the matching service to be subscribed locally so its ApiClient handles the cascade.

## [0.13.1] - 2026-04-25

### Added

- **`MutationCount` property on generated API clients** — local-only `int` counter incremented on every state mutation: Optimistic / CrossOptimistic local execution, Server / ServerPatch / ServerReplace result application, incoming broadcasts (regular and subscriber-event), and reconnect refresh. Cheap polling alternative to the `OnStateMutated` event for flows that just want to ask "did anything modify my state since I last checked?" — e.g. cache invalidation, idempotent re-renders. Fires in more places than `OnStateMutated` (which intentionally skips Optimistic/CrossOptimistic), so prefer `MutationCount` when you want a unified "state was touched" signal. Not synchronized across clients, not persisted, not coordinated with the network sequence number.

### Internal

- `ClientVersionPolicy` refactor — single public method `Task<ClientVersionValidationResult> ValidateAsync(string?)` encapsulates TTL caching, grain refresh, and version parsing. `IsCacheExpired` and `RefreshFromGrainAsync` are no longer part of the public surface; `ValidateClientVersion` / `TryParseVersion` moved out of `MetaConnectionHandler`. Generator updated to inject `IGrainFactory` into the policy at construction time. No behavior change for consumers — the connect-path version gate still enforces the same rules.

## [0.13.0] - 2026-04-25

### Added

- **Client version checking on `SessionConnect`** — the server can now enforce a minimum client version and reject incompatible clients before they establish a session.
  - `SessionConnectRequest.ClientVersion` — client sends its version string (`"major.minor.patch"`) on every connect.
  - `SessionConnectResponse.ServerVersion` / `.MinClientVersion` — server echoes its version and the minimum it accepts; clients can surface these in upgrade prompts.
  - `ConnectionSessionConnectResult.ServerVersion` / `.MinClientVersion` — surfaced through `IConnection` to the application layer.
  - `MetaTransportOptions.ServerVersion` / `.MinClientVersion` — static startup configuration; `MinClientVersion` can be overridden at runtime without restarting.
  - **Compatibility rules** (applied on every `SessionConnect` when both `ServerVersion` and `MinClientVersion` are set):
    - Client did not send a version → allowed through (backward compatibility with old clients).
    - `clientMajor ≠ serverMajor` → rejected ("incompatible major version").
    - `clientVersion < MinClientVersion` (minor/patch) → rejected ("client too old, please upgrade").
    - Otherwise → accepted.
  - All six client transports updated: Unity `SignalRConnection` (new `clientVersion` constructor param), `UnityHttpConnection`, `BestHttpPollingConnection`, `BestHttpSignalRConnection` (new `ClientVersion` option), .NET `SignalRConnection` (new `clientVersion` param), `HttpPollingConnectionOptions.ClientVersion`.

- **`ClientVersionPolicy` — per-silo cache + validator** (registered automatically by `AddMetaServices()` / `ConfigureMeta()`):
  - Initialized from `MetaTransportOptions` at startup; the cluster-wide override is fetched from `IVersionPolicyGrain` and cached locally with a 60-second TTL (`ClientVersionPolicy.CacheTtl`).
  - Single public method: `Task<ClientVersionValidationResult> ValidateAsync(string? clientVersion)` — encapsulates the cache refresh, grain fetch, and version parsing. Returns `{ ServerVersion, MinClientVersion, Error }`; `Error == null` ⇒ allowed.
  - `MetaConnectionHandler` calls `ValidateAsync` once per connect — no direct grain or `Interlocked` access in handler code.

- **`IVersionPolicyGrain` + `VersionPolicyGrain` — cluster-wide version gate via Orleans grain singleton**:
  - Single activation (key `"global"`) shared across the entire cluster. State persisted to the `"Default"` storage provider.
  - Calls `SetMinClientVersionAsync(version)` from any process in the cluster (e.g. a dedicated admin service) to block old clients on all silos simultaneously.
  - Grain value overrides static config; setting it to `null` clears the override and falls back to `MetaTransportOptions.MinClientVersion`. Admin changes propagate to every silo within one TTL window without hammering the grain.

## [0.12.4] - 2026-04-24

### Removed

- **`MetaContext.GetEntityApi<T>(string id)` — the abstract method and its three overrides in `ClientMetaContext`, `ServerMetaContext` and `CrossOptimisticMetaContext`.** It was a documented-but-dead API: declared `abstract` on `MetaContext`, but every concrete implementation threw `NotImplementedException` ("requires generated recorder/replayer/caller"). The real cross-entity entry point has always been the typed `GetI{Service}(entityId)` accessor that the source generator injects into the service partial based on `[MetaServiceImpl(..., typeof(IService))]` dependency declarations. Leaving the dead method on the public surface actively misled AI-assisted code generation: agents reading `SharedMeta-AI.md` / `GUIDE.md §6` / `ARCHITECTURE.md` copied the `Context.GetEntityApi<IMapService>(mapId).MethodAsync(...)` snippet verbatim, producing code that compiled but threw at runtime. No runtime consumers existed (generator and tests don't reference it), so removal is source-compatible for anyone who was using the actual working pattern

### Fixed

- **Documentation swept for the dead `Context.GetEntityApi` snippet.** Every reference replaced with the declared-dependency + generated-`GetI{Service}` pattern, with an explicit "do not use" note citing this removal:
  - `SharedMeta-AI.md` — "Cross-Entity Calls" section and the `[MetaService]` vs `[ServerMetaService]` comparison table (the file AI agents read first)
  - `docs/GUIDE.md` — §6 "Cross-Entity Calls", §6.5 comparison table, §7 `ResolveSessionResources` example, Signal-method limitation wording
  - `docs/ARCHITECTURE.md` — §8 "Cross-Entity Communication"
  - `examples/Unity/Expedition/ServerSolution/Expedition.Server/SharedMeta-AI.md`
  - Project Wizard generated `SharedMeta-AI.md` snippet (emitted per-project)

## [0.12.3] - 2026-04-23

### Changed

- **Unity editor menus moved under `Tools/SharedMeta/`** — `Tools/SharedMeta/Project Wizard` and `Tools/SharedMeta/Server Runner`. The previous top-level `SharedMeta/` entries are gone. Aligns with Unity convention that third-party tooling lives under `Tools/`
- **Project Wizard default project names aligned with convention** — `Meta.Shared` (was `MyGame.Shared`), `Meta.Client` (was `Meta`), `Meta.Server` (was `MyGame.Server`). Default Unity folders follow suit (`Assets/Scripts/Meta.Shared`, `Assets/Scripts/Meta.Client`). Still user-editable in the wizard
- **Project Wizard generated `MetaGameClient.Start()` now wraps the connect flow in `try/catch`** — logs `Debug.LogError` + `Debug.LogException` on failure instead of letting the async-void exception become an unhandled UnityException, with an inline pointer to the Expedition sample's modal-reconnect UI pattern for production use
- **Project Wizard generated `MetaClientOptions` block now documents optional hooks** — `Diagnostics` (`IDesyncDiagnostics`) and `ConnectionHealth` (`IConnectionHealth`) as commented-out property initializers, plus a hint about `Dispatcher.DiagnosticsLog` for request-lifecycle tracing
- **Project Wizard Expedition template now showcases new 0.11 / 0.12 features** — `[NamedRandom("Map")]` + `[NamedRandom("Loot")]` on `ExpeditionState` (independent per-mechanic scroll), `[MetaMethod(Mode = ExecutionMode.Query)] bool IsActive()` (read-only, no-subscription polling), `[MetaMethod(Mode = ExecutionMode.Signal)] void Ping(string)` (fire-and-forget, state-immutable), and `GenerateMap` / `Move` impls now roll against `MapRandom` / `LootRandom` directly

### Fixed

- **Project Wizard no longer mutates an unrelated parent `Directory.Packages.props` when generating into a subfolder of a repository that already has a root-level CPM.** `FindDirectoryPackagesProps` used to walk up the directory tree without bound; if the wizard was invoked from within a repo that defines centralised package versions at its root (e.g. testing the wizard from inside this SharedMeta repo), it would append `CoreGame.SharedMeta.*` entries to that root file instead of creating a fresh one inside the wizard's target solution. Search is now capped at the declared solution root — no walk above. Existing CPM at solution root is still detected and extended
- **Generated Server `.csproj` no longer fails to compile when SignalR + MessagePack is selected.** `Program.cs` emits `AddMetaMessagePackProtocol()` from the separate `CoreGame.SharedMeta.Transport.SignalR.MessagePack` package; the wizard now includes that `<PackageReference>` (and the matching `<PackageVersion>` in the generated / merged `Directory.Packages.props`) whenever both conditions hold
- **Generated Server `Program.cs` Serilog config now surfaces SharedMeta diagnostic logs by default** — adds `.MinimumLevel.Override("SharedMeta", LogEventLevel.Debug)` to the `UseSerilog` initializer. Without it, `[Desync]`/`[Handler]` entries stayed hidden behind Serilog's default Information threshold
- **Generated Expedition client example pointed at non-existent `ResumeOrStartExpeditionAsync` / `result.EntityId`.** Corrected to `StartExpeditionAsync` / `entityId`, with added inline examples for `PingSignal` and the `ExpeditionServiceQueryApi.IsActiveAsync` flow

## [0.12.2] - 2026-04-23

### Fixed

- **Reconnect regression introduced by the 0.12.1 `OrderedDispatcher` refactor.** `ClientDispatcher.HandleDisconnected` called `_ordering.Reset(1)` on every transient disconnect, which cleared the pending buffer **and** rewound the expected sequence number to 1. On session resume the server continues numbering from `lastAcknowledgedSequence + 1` (e.g. seq 9, 10, 11 …) — those packets landed in the re-initialised buffer waiting for seq 1, which never comes, so nothing was ever delivered. The resent RPC's response flows through the same ordering path, so the retry loop saw no reply and fired again, producing the observable infinite `Re-sending ↔ Disconnected` oscillation. Pre-refactor `MessageBuffer.Clear()` correctly preserved the head. **Symptom was SignalR-specific only because Expedition's `DebugConnectionWrapper` uses `PacketLossMode.ConnectionDrop` for SignalR** (drops the whole connection → reconnect cycle triggers every time) whereas HTTP uses `PacketLossMode.RequestHang` (no reconnect for most cases → the rewind never fires)
- `ConnectSessionAsync` now explicitly `Reset(1)`s ordering and zeroes `_lastAcknowledgedSequence` when the server reports `IsNewSession = true` (resume attempt failed, server minted a fresh session). Without this, stale `_nextExpected` from the prior session would drop the new session's first broadcasts as duplicates

### Changed

- `OrderedDispatcher` now exposes two distinct methods: `Clear()` (drops the pending buffer, keeps `_nextExpected` — correct for transient disconnects that will resume the same session) and `Reset(long)` (drops buffer **and** sets a new `_nextExpected` — for session supersede / restart where the server will renumber from 1). Single-method API from 0.12.1 conflated the two cases

## [0.12.1] - 2026-04-23

### Fixed

- **HTTP transport desync reports silently dropped when invoked from a background continuation.** `UnityWebRequest` construction requires Unity's main thread, but the generated `ApiClient` fires `_network.SendDesyncReportAsync(...)` from a `.ContinueWith(...)` that runs on the thread pool — construction threw `UnityException: Create can only be called from the main thread` and the exception was absorbed by the fire-and-forget `_ = ...` path, so the server never received the report. `UnityHttpConnection` now captures the `SynchronizationContext` of the thread that constructs it (expected to be Unity's main thread) and marshals `UnityWebRequest` construction onto it when `PostRawAsync` is entered from a non-main thread. JSON body serialization stays off the main thread (CPU work). No overhead on the fast path when already on the main thread
- **`SessionManagerGrain.BroadcastToSessionOp` dropped `NamedRandomScrollDeltas` when building an `RpcResponse` from an `EntityBroadcast`**, so clients receiving a broadcast triggered by another client's RPC never saw per-named-stream scroll deltas — named-stream desync detection and `Skip`-catchup on `ServerPatch`/`ServerReplace` were silently broken in multi-client scenarios. Fix copies the field through in both bundling branches

### Changed

- **`ClientDispatcher` rewritten around a new `OrderedDispatcher`** — seq-ordered reassembly, dispatcher ownership, and re-entrant handler bypass (via `AsyncLocal<bool>` propagated through `ExecutionContext`) now live in a single dedicated class. Behavior is preserved (full test suite + 300× broadcast-race loop + re-entrant RPC deadlock regression test all green), but the scattered `lock`/`Volatile`/flag scaffolding is gone; ordering/re-entrancy contract is now readable end-to-end
- `InProcessConnection` simplified — ordering & re-entrancy responsibility lifted out of the transport; it now just delivers batches in the order the server produced them, like the other transports
- **Per-call chatty `[ClientDispatcher]` Debug logs removed** (`SendAndCompleteAsync`, `RPC response`, `HandleBatch`, `Resolved`, `DeliverBroadcast`, `Calling N handlers`). They fired on every RPC and swamped Debug output. Detailed per-request tracing remains available through the existing opt-in `ClientDispatcher.DiagnosticsLog` delegate — assign an `Action<string>` to it (e.g. a file writer) and the internal `LogDiag` channel emits `SEND/RECV/BATCH/CONFIRMED` events for post-mortem analysis

### Added

- **Desync-flow diagnostic logs on the rare desync path** — `[Desync]` entries in `DispatcherNetworkAdapter.SendDesyncReportAsync` (request sent / server response / local failure), `[UnityHttp]` entries in `UnityHttpConnection.SendDesyncReportAsync` (enter / response / failure), `[Handler]` entries in `MetaConnectionHandler.SendDesyncReportAsync` and `SetDebugOptionsAsync`. All at Debug level — zero cost on the happy path, full client → transport → server trace when a desync is being investigated
- `docs/ORDERING.md` and `docs/ORDERING-GUARANTEES.md` — formal documentation of the ordering pipeline: server-side (per-session monotonic sequence, per-entity `EntitySequenceNumber` + `HeldBroadcasts`, RPC response bundling, deferred responses, optional `EnforceRpcOrder`), client-side (seq-ordered reassembly via `OrderedDispatcher`, in-order op dispatch, TCS ↔ RequestId binding), and the transport contract every `IConnection` implementation must honor

### Removed

- `com.coregame.sharedmeta/Runtime/Client/MessageBuffer.cs` (+ `.cs.meta`) — replaced by `OrderedDispatcher`

## [0.12.0] - 2026-04-20

### Added

- **Signal methods — fire-and-forget RPC** — `[MetaMethod(Mode = ExecutionMode.Signal)]` marks a method as a one-way server call. Client generates a synchronous `{Method}Signal(params)` that returns `void` and delegates to `INetwork.SendSignalAsync`; no RequestId tracking, no response on the wire, no auto-retry interaction, no broadcast-suppression side effects. Server routes through a generated `{Service}SignalDispatcher` invoked from the grain's `[OneWay]` `HandleSignalAsync` — read-only execution, no sequence increment, no broadcasts, no persistence. Server-side errors are logged and swallowed (fire-and-forget contract). Primary use cases: heartbeat, telemetry ping, notification via `[ServerMetaService]` bridges
- **`IMetaHub.SignalCall` / `IConnection.SignalCallAsync` / `IMetaProvider.HandleSignalAsync` / `IEntityGrainBase.HandleSignalAsync`** — new transport-to-grain pipeline for signals. All three transports implement it: **InProcess** (direct grain invocation), **SignalR** (`HubConnection.SendAsync` instead of `InvokeAsync` — no wire-level ACK awaited), **HttpPolling** (`POST /meta-http/signal` → `202 Accepted` before execution completes). `ISessionManager.SignalEntityAsync` bridges handler → entity grain
- **`NullPayloadWriter` + `NullServerRecordContext`** — zero-allocation singletons (`Array.Empty<byte>()`, no-op writes) used during signal dispatch. `ServerMetaContext.SignalMode` toggles `Writer` to the null sink so `[ServerMetaService]` bridge Recorders called from inside a signal method body write into /dev/null — real side-effects (HTTP calls, Orleans grain hops) still happen, recording is silently discarded since there is no replay payload to feed back
- **`[OneWay]` on `IEntityGrain.HandleSignalAsync`** — Orleans is told the call is truly one-way; SessionManager grain does not wait for an ACK from the entity grain after handing off the signal
- **Unified `ExecutionMode` enum** — `ExecutionMode.Query` and `ExecutionMode.Signal` joined the existing modes (`Local`/`Optimistic`/`Server`/`CrossOptimistic`/`ServerPatch`/`ServerReplace`). Both Query and Signal change method signature and lifecycle (Query must return a value; Signal must be `void` and never awaits) and cannot coexist with other modes on the same method. Making them first-class enum members eliminates the former bool-flag clash-detection (`Query = true` + `Signal = true` was a `#error`; with the enum it is not expressible). See **Deprecated** below for the migration path from the legacy bool flags

### Changed

- **Runtime override is locked for Query and Signal.** `IExecutionModeProvider.GetMode(...)` short-circuits when the method's declared mode is `Query` or `Signal` — overrides map entries are ignored. `ExecutionModeProvider.SetMode` / `SetServiceMode` additionally throw `ArgumentException` if asked to apply a `Query`/`Signal` override, since these are structural traits that exist only at code-generation time, not routing strategies. Overriding into or out of them would be a silent no-op; the throw makes the misuse explicit
- Signal signature is validated at compile time via `#error` in the generated ApiClient when misused: non-`void` return type, combined with `Mode = ExecutionMode.Query` (same-field impossible; cross-mode collision still checked), explicit non-default `Mode = ...`, or `Sync = ...` each produce a named diagnostic
- `SignalCallRequest` DTO added alongside `RpcCallRequest` / `QueryCallRequest` — wire shape: `EntityId + ServiceName + MethodName + Payload` (no RequestId, no RandomScrollDelta, no response counterpart)

### Deprecated

- **`MetaMethodAttribute.Query` (bool)** and **`MetaMethodAttribute.Signal` (bool)** — marked `[Obsolete]`. Use `Mode = ExecutionMode.Query` or `Mode = ExecutionMode.Signal` respectively. The generator accepts either form for backward compatibility; setting both the legacy bool AND a conflicting explicit `Mode` on the same method produces a `#error` diagnostic. The legacy bool properties will be removed in a future major version

### Migration

- **From `[MetaMethod(Query = true)]`** → `[MetaMethod(Mode = ExecutionMode.Query)]`. Behavior is identical
- **From `[MetaMethod(Signal = true)]`** → `[MetaMethod(Mode = ExecutionMode.Signal)]`. Behavior is identical
- No runtime changes required for consumers; any existing source that uses the bool flags continues to compile with a `CS0618` warning until migrated
- Consumers of `ExecutionModeProvider.SetMode` must not pass `ExecutionMode.Query` or `ExecutionMode.Signal` — the method now throws `ArgumentException`. If you previously had code that tried to apply such an override, it was a silent no-op; remove the call

### Known limitations / TODO

- Compile-time syntactic check for **state mutations inside signal method bodies** is deferred. The runtime contract is "signal methods must not mutate state" — today this is documented but not enforced by the generator (convention-only, same as Query). Planned for a follow-up release as a Roslyn walker over the impl method body
- **Cross-entity calls** from inside a signal body throw `NotSupportedException` (via `NullServerRecordContext.CallEntityAsync`). If you need to chain into another entity, use a regular `Mode = Server` method instead

## [0.11.0] - 2026-04-17

### Added

- **Synchronous client API for Optimistic methods** — new `[MetaMethod(Sync = SyncApi.Generate)]` opt-in generates a sibling `{Method}Sync` overload on the generated `ApiClient` alongside `{Method}Async`. The sync overload returns `T` (or `void`) directly, completes the local mutation in the calling frame, and fires the server round-trip in the background via `ContinueWith`. Target use case: DOTS / main-thread game loops where a profile mutation must be visible in the *very next frame* with no `await` boundary in between
- **`SyncApi` enum** — `None` (default, async only), `Generate` (emit both async and sync overloads), `OnlySync` (emit only the sync overload; per-mode private dispatchers are omitted — the service is effectively locked to Optimistic/Local)
- **`SyncPolicy` enum** — controls runtime behavior when `IExecutionModeProvider` has overridden the effective mode away from Optimistic/Local (e.g. a downloaded config promoted the method to `Server`). `Throw` (default, raises `InvalidOperationException`), `Warn` (logs via `MetaLog.Warning` + `IDesyncDiagnostics.OnSyncPolicyViolation` and executes locally anyway), `Silent` (executes locally, diagnostics callback only)
- **`IDesyncDiagnostics.OnSyncPolicyViolation(serviceName, methodName, effectiveMode)`** — new default-implemented hook fired when `SyncPolicy.Warn` or `Silent` swallows a runtime mode override
- **`[NamedRandom]` — independent deterministic random streams per state** — `[NamedRandom("Combat")]` on a `[SharedState]` class declares a separately-seeded `IMetaRandom` stream; the generator emits a typed `{Name}Random` property on every service `Context` partial for that state. Lets `Combat`, `Loot`, `MapGen` advance independently so adding a wall-placement call does not shift the loot roll. Semantics mirror `Context.Random` (same algorithm and seed on both sides). Optional `Seed = "literal"` pins the stream to a fixed seed across entities
- **`NamedRandomAttribute`** — `AttributeUsage = Class, AllowMultiple = true`. Positional: the index into the generated `Context.NamedRandoms` list follows attribute declaration order on the state. Reordering / adding / removing attributes reseeds the affected slots from the derived seed — acceptable because the value being lost is random state
- **Per-index scroll-delta desync detection** — `RpcResponse.NamedRandomScrollDeltas` and `EntityBroadcast.NamedRandomScrollDeltas` carry a `long[]?` (null when nothing advanced). Client compares per-index and fires `OnRandomDesync` with method name suffixed `[NamedRandom:{i}]`. On `ServerPatch` / `ServerReplace` / broadcast catch-up, client calls `Skip(delta)` per-index to stay in sync
- **`EntityGrainState.NamedRandomsBytes` (Id 7)** — packed positional `MetaRandom[]` persisted alongside `ServerRandomBytes` / `OptimisticRandomBytes`. Transmitted on subscribe via new `NamedRandomsBytes` field on `SubscribeResponse`, `ConnectResponse`, `ResubscribedEntityInfo`, `EntitySnapshot`

### Changed

- `[MetaMethod(Sync = ...)]` with `Mode` other than `Optimistic`/`Local` produces a `#error` in the generated `ApiClient.g.cs` — Roslyn surfaces it as a compile error naming the method
- `[MetaMethod(Sync = ...)]` on a service method whose return type is `Task`/`Task<T>` produces a `#error` — sync generation requires a non-async signature because the local body cannot legitimately `await` and still complete in-frame

### Fixed

- **`IDesyncDiagnostics.OnRandomDesync` could fire false positives when two or more Optimistic methods that advance `Context.Random` (or a `[NamedRandom]` stream) were issued in close succession on the same `ApiClient`**. The generated Optimistic path captured `scrollIdBefore` before local exec but computed `localScrollDelta = _optimisticRandom.ScrollId - scrollIdBefore` **inside** the fire-and-forget `ContinueWith` — by the time that continuation ran, subsequent calls on the same client may have already advanced the shared `_optimisticRandom.ScrollId`, yielding a phantom local delta that didn't match the server's per-call delta. Generator now snapshots `localScrollDelta` (and `localNamedScrollDeltas` for named streams) synchronously right after local exec, so the continuation compares stable captured values against the server response. Affects `{Method}Async_Optimistic`, `{Method}Sync_Optimistic`, and `{Method}Async_CrossOptimistic` paths. No runtime correctness impact — the random state and replay were always consistent; only the diagnostic callback was noisy

### Migration

- No migration required. `Sync` defaults to `SyncApi.None`; existing services continue to generate only the async API, unchanged

### Known limitations / TODO

- On `SyncPolicy.Warn`/`Silent`, the sync body still runs locally even when the effective mode has been overridden to `Server`/`ServerPatch`/`ServerReplace`. A future opt-in may route such calls through a real server round-trip (fire-and-discard local result) for callers that prefer correctness over immediacy when config promotes the method — see `TODO(sync-mode-override)` in `SimplifiedApiClientGenerator.cs`

## [0.10.2] - 2026-04-15

### Fixed

- **`ServerMetaConfiguration.g.cs` referenced `PatchSchema` classes without namespace qualification**, causing `CS0103: The name '{State}PatchSchema' does not exist in the current context` in server projects whose namespace differs from the state type's namespace. Generator now emits fully-qualified `{StateFullName}PatchSchema.Instance` for both `byState` and `byService` registry entries

## [0.10.1] - 2026-04-14

### Added

- **Device reset endpoint** — `POST /meta/auth/reset-device` (`[Authorize]`) force-unlinks a `deviceId` from the current player. After reset, the next login with that device creates a new player profile. Unlike `/unlink`, this works even when the device is the player's only auth key
- **`IAuthGrain.ForceUnlinkAsync()`** — unconditional unlink that clears the auth key→PlayerId mapping and removes the key from the player's `AuthIndexGrain`, without the "last key" safety check
- **`MetaAuth.ResetDeviceAsync()`** — client-side helper. Calls the reset endpoint with the current JWT, optionally clears `ITokenStorage` on success. Works across all platforms (Unity via `UnityMetaAuth`, .NET via `HttpMetaAuthProvider`, local via `LocalMetaAuthProvider`)
- **`IMetaAuthProvider.ResetDeviceAsync()`** — new interface method implemented by all built-in providers

## [0.10.0] - 2026-04-13

### Added

- **Client-side connection health monitoring** — new `IConnectionHealthListener` interface and `ConnectionHealthOptions` notify the game when pending RPC requests exceed configurable timeout thresholds. Two stages: `Slow` (default 1s, show spinner) and `Unresponsive` (default 5s, show modal dialog). Wired via `MetaClientOptions.ConnectionHealth`. Callbacks fire on the game-loop thread from `ProcessPendingBroadcasts()`
- **Client-side auto-retry for lost requests** — `ClientDispatcher` automatically resends all pending requests every `ConnectionHealthOptions.RetryIntervalMs` (default 2s) when the oldest pending request exceeds `SoftTimeoutMs`. This is the primary recovery mechanism for packet loss — fully client-side, no server dependency
- **`MetaClient.ResumeSessionAsync()`** — attempts to restore the current session (same `sessionId`, missed packet recovery) without restarting. Use for "try again" after connection issues. Falls back to `RestartSessionAsync()` if resume fails
- **`ClientDispatcher.LastCompletedRequestId`** — tracks the last request ID that received a real entity response, useful for UI diagnostics (sent vs confirmed)
- **`ClientDispatcher.DiagnosticsLog`** — optional `Action<string>` delegate for request lifecycle tracing. When set, logs all SEND/RECV/CONFIRMED/TRANSPORT_ERROR/AUTO_RETRY/STALL/RESEND events with timestamps. Write to file for post-mortem analysis
- **`DebugConnectionWrapper`** — wraps any `IConnection` to simulate network problems for testing. Configurable added latency (`MinLatencyMs`/`MaxLatencyMs`), packet loss (`PacketLossPercent`), and manual disconnect (`SimulateDisconnect()`/`SimulateTemporaryDisconnectAsync()`). Lives in `Runtime/Core/Transport/`, available to all clients (Unity, Godot, console)
- **`PacketLossMode` enum** — `ConnectionDrop` (full disconnect, realistic for SignalR/TCP) vs `RequestHang` (individual request fails with `HttpRequestException`, realistic for HTTP polling). Set via `DebugConnectionSettings.LossMode`
- **Transport selection in Expedition Unity example** — startup panel lets user choose SignalR or HTTP Polling transport. HTTP polling auto-enables `EnforceRpcOrder` and sets `PacketLossMode.RequestHang`
- **Connection health UI in Expedition example** — red overlay with "Reconnect" button, modal blocker when `Unresponsive`. Request tracking strip shows `Sent: #N  Confirmed: #M  Pending: K`
- **Debug network panel in Expedition example** — Sim ON/OFF, Drop, Metro 3s (temporary disconnect with real server-side session recovery), Latency +/-, Loss +/- controls. Editor/Development builds only

### Changed

- **`ProcessPendingBroadcasts()` runs `CheckConnectionHealth()` first** — health checks and auto-retry now execute every frame regardless of broadcast suppression (`_broadcastSuppressCount`). Previously, pending optimistic RPCs blocked health checks entirely
- **Server-side stall notifications are now lazy** — `SessionManagerGrain` no longer creates periodic grain timers for stall detection. Instead, stall diagnostics are pushed on the next incoming request (if a gap exists) and logged on grain deactivation. Eliminates timer overhead for HTTP polling games that regularly trigger brief out-of-order arrival
- **HTTP polling clients deliver `StallNotification` broadcasts** — poll loop filter in `UnityHttpConnection` and `HttpPollingConnection` now passes through responses with `StallNotification != null` even when `Operations.Count == 0`. Previously, stall notifications were silently dropped as "empty broadcasts"
- **Expedition server enables `EnforceRpcOrder`** — required for HTTP polling transport where wire-level FIFO is not guaranteed. Safe for SignalR (one extra int comparison per in-order call)
- **Expedition server registers HTTP polling endpoint** — `app.MapMetaHttpPolling("/meta-http")` with `HttpPollingConnectionManager` in DI

### Fixed

- **HTTP poll loop dropped `StallNotification` broadcasts** — `UnityHttpConnection.RunPollLoop` and `HttpPollingConnection` filtered broadcasts with `Operations.Count > 0`, which excluded `StallNotification` (out-of-band, zero ops). Server-side stall detection worked but client never received the notifications. Fixed by adding `|| broadcast.StallNotification != null` to the filter
- **`CheckConnectionHealth` never ran during broadcast suppression** — when an optimistic RPC was awaiting replay (`_broadcastSuppressCount > 0`), `ProcessPendingBroadcasts()` returned early before reaching `CheckConnectionHealth()`. Auto-retry and health status updates stopped entirely. Moved health check to run first, before the suppression guard
- **Server stall timer never re-notified after partial drain** — when `EnforceRpcOrder` drained some stashed requests but a gap remained (e.g., #5,#6,#7 drained but #8 still missing), `_lastStallStage` stayed at `TimeoutPending` and the tick callback skipped notifications for the new gap. Replaced timer with lazy diagnostics on next request arrival

## [0.9.4] - 2026-04-11

### Changed

- **`MetaLoginResult` moved from `SharedMeta.Client` to `SharedMeta.Core.Auth` namespace.** The type itself and all its members are unchanged — only its namespace moved. In Unity (single `SharedMeta.Runtime` asmdef) the move is transparent. In the .NET NuGet build (`SharedMeta.Core` + `SharedMeta.Client` as separate assemblies) this fixes a circular reference that was introduced in 0.9.3: `IMetaAuthProvider` / `HttpMetaAuthProvider` in `SharedMeta.Core` could not legally reference `SharedMeta.Client.MetaLoginResult`, so 0.9.3 failed to build on the NuGet side

### Migration

- Code that uses `MetaLoginResult` **unqualified** (via `using SharedMeta.Client;` alone) must **add `using SharedMeta.Core.Auth;`**. Fully qualified references (`SharedMeta.Client.MetaLoginResult`) must be rewritten to `SharedMeta.Core.Auth.MetaLoginResult`. All first-party code in this repo has been updated — the grep for migration is `rg "MetaLoginResult" --type cs`

## [0.9.3] - 2026-04-11

### Added

- **`ICrossEntityResolver.UpdateCachedState(entityId, newState)`** — new interface method that lets generated `ApiClient` code push a fresh state reference into the resolver after `ServerReplace` mode replaces the local `_state`. Implementations should update whatever backing cache maps `entityId → state`. See the fix under **Fixed** below for context
- **`IMetaAuthProvider` interface** — unified extension point for replacing the default HTTP auth flow. Implement `LoginAsync` / `LoginWithPlatformAsync` / `LinkAsync` / `UnlinkAsync` and assign the instance to `MetaAuth.Provider` to route all auth calls through a custom transport (Unity, local backend, Firebase, PlayFab, etc.)
- **`HttpMetaAuthProvider`** — default `IMetaAuthProvider` implementation using `HttpClient`. Used automatically on non-Unity .NET targets when no custom provider is set
- **`MetaAuth.Provider` property** — takes precedence over the legacy Func-based hooks and the built-in HttpClient fallback

### Changed

- **`MetaAuth` priority order**: `Provider` → legacy `LoginFunc`/`PlatformLoginFunc`/`AuthActionFunc`/`UnlinkFunc` → default `HttpMetaAuthProvider` (non-Unity only). Existing `UnityMetaAuth.Register()` callers continue to work unchanged via the legacy-Func fallback
- Legacy `LoginFunc` / `PlatformLoginFunc` / `AuthActionFunc` / `UnlinkFunc` properties documented as legacy — prefer `MetaAuth.Provider` for new code

### Fixed

- **`Client.GetState<T>()` returned stale state after `ServerReplace` mode** — when a `[MetaMethod(Mode = ServerReplace)]` call completed, the generated `ApiClient` replaced its private `_state` field with the server-provided snapshot, but `MetaServiceResolver.EntityConnection.State` still pointed at the original subscribe-time instance. Any code reading via `Client.GetState<T>()` (including UI render paths and cross-entity `GetEntityState` calls from other services) saw the stale state, while `ApiClient.State` saw the fresh one. Fixed by adding `ICrossEntityResolver.UpdateCachedState(entityId, newState)` — the generated ApiClient now pushes the fresh reference into the resolver whenever `_state` is replaced (both in direct RPC responses and in broadcast `StateBytes` / trigger `StateBytes` paths). The resolver's cached `EntityConnection.State` stays in sync, and all read paths see the same live reference

## [0.9.2] - 2026-04-10

### Added

- **`Context.SaveStateAsync()`** — force-persist entity state mid-method for pseudo-transactional patterns. On server: persists state + random bytes to Orleans storage immediately. On client: no-op. Enables safe cross-entity resource transfer where state must be checkpointed before sending acknowledgements

## [0.9.1] - 2026-04-09

### Added

- **Natural property assignment for sub-wrappable fields** — `{State}PatchWrapper` now emits setters for nested-object properties. `state.RequiredProfile = new Profile { ... }` works via implicit operator. Assign-then-mutate in the same call is supported (applier unpacks Value first, then applies Children mutations)
- `PatchNode.MarkChildFullReplace(fieldId, packed)` — clears collection state + records `FullReplace` op in one call
- 411-test `SharedMeta.PatchFuzzTests` project — standalone patch roundtrip suite covering all field types, collection ops, nested mutations, and 70 randomized fuzz seeds

### Fixed

- **List field reassignment drops subsequent mutations** — list-field setters now record `FullReplace` structural ops instead of terminal Value, so `state.Heroes = new List(); state.Heroes.Add(...)` works correctly
- **`CollectionPatchApplier` early return on Value** — no longer drops structural ops when Value is present on the same node
- **`PatchTextRenderer` diff hides divergent collection fields** — skip condition now checks StructuralOps/Children, not just Value equality
- **SubWrappable applier `if/else` on terminal vs children** — changed to sequential Value-then-Children so `SetX()` + mutate-via-wrapper works

## [0.9.0] - 2026-04-08

### Added — granular list patches

- **Element-sub-wrappable lists** — services with `[MetaServiceImpl(DeepDesync = true)]` can define state with `List<T>` where `T` has `[MemoryPackOrder]`/`[Id]`/`[Key]` properties. The generator emits a specialized `{Element}PatchableList` nested class whose indexer hands out per-element `{Element}PatchWrapper` bound to per-element subtree nodes. Mutations like `state.Heroes[5].Exp += 100` flow into `Heroes/[5]/Exp`, not a full collection snapshot. Two-level nesting works (`state.Heroes[i].Equipment.Add(item)`)
- **Compile-time tracking guard via type system** — every `{State}PatchWrapper` has a one-way `static implicit operator {Wrapper}({State}?)`. With no reverse operator, the type system enforces that helper methods exposing collection elements **must** return `{Element}PatchWrapper`. Returning raw `{Element}` from a helper compiles in the regular service class but fails the generated `_PatchTracked` copy with `CS0029`. Silent loss of patch tracking is now a compile error
- **Granular structural ops for `List<T>`** — `Add`/`Insert`/`RemoveAt`/`Remove`/`Clear`/`Set` are recorded as individual `PatchListOp` entries on the collection node's `StructuralOps` list. Same for scalar lists (`List<byte>`, `List<int>`). Sort/Reverse/AddRange fall back to `FullReplace` ops
- **Index shifting** — when `Insert`/`RemoveAt` reshape a list with pending element-subtree mutations, the sender shifts the affected child indices via `PatchNode.ShiftElementChildren`. Mixed structural+element mutations in one call work correctly
- `PatchListOp` / `PatchListOpKind` types in `SharedMeta.Core.Patch`
- `CollectionPatchApplier.Apply<T>(...)` runtime helper for generated appliers
- `PatchTextRenderer` updated for collection nodes — renders `ops`/`elements` sections separately
- 10 `ElementSubWrappableTests` + new `PartyState`/`Hero`/`Item` test surface in `SharedMeta.Test.Meta1`

### Changed — wire format break

- **`PatchNode` wire format extended**: new `PatchChildKind Kind` field, `FieldId` widened to `long`, new `StructuralOps: List<PatchListOp>?` field. Patches between 0.8.0 and 0.9.0 are not interoperable. Both client and server must upgrade together
- `IPatchSchema` interface uses `long fieldId` (was `int`)
- `PatchNodeDiffer` keys children by `(Kind, FieldId)` to avoid collisions between field children and element children

### Fixed

- **Nullable type dedupe in `PatchSchemaGenerator`** — `Card?` and `Card` referenced from different parents previously emitted two competing `CardPatchSchema` classes in the same scope. Now nullable annotations are stripped and the dedupe set is shared across all recursion levels

## [0.8.0] - 2026-04-08

### Added

- **Server-side RPC reordering** — `SessionManagerGrain` optionally guarantees that RPC calls reach entity grains in monotonically increasing `RequestId` order, regardless of threadpool / transport delivery scheduling. New `SessionManagerOptions.EnforceRpcOrder` (default `false`) opts in. The session manager parks out-of-order calls in a fixed-capacity ring buffer and drains them inline when the missing predecessor arrives, bundling all results into one `SessionResponse`. Required for transports that don't preserve submission order at the wire level (HTTP polling, custom UDP, anything with intermediate `Task.Run`); SignalR over a single hub connection works fine without it. Client side is unchanged — it already matches operations to pending TCS by `RequestId`
- `RpcOrderingBuffer<T>` — generic, allocation-free ring buffer with `Classify` / `TryStash` / `TryDequeueNext` / `MarkDispatchedInOrder` / `Reset` API in `SharedMeta.Server.Core.Session`. Used by `SessionManagerGrain` as the only ordering primitive. Covered by 13 focused unit tests (overflow, head wraparound, duplicate / stale, reset, mixed in-order/out-of-order)
- **Session health stall notifications** — when an RPC ordering gap stays open beyond `SoftStallNotifyTimeout` (default 500 ms), the server pushes a `StallNotification` through the observer channel as a new `SessionResponse.StallNotification` field. At `HardStallNotifyTimeout` (default 10 s) a second `StallStage.TimeoutPending` notification is pushed. Recovery emits a final `StallStage.Recovered` notification when the gap fills. After `MaxStallDuration` (default 5 minutes) the session is terminated and the client must reconnect
- `ISessionHealthListener` (in `SharedMeta.Core.Diagnostics`) — client-side interface with `OnSessionStalled` / `OnSessionRecovered`, wired through `MetaClientOptions.SessionHealth`. `ClientDispatcher.ProcessServerResponse` routes stall-only batches directly to the listener without touching the broadcast buffer or pending requests
- `SessionManagerOptions` — new DI-configurable options class. Fields: `EnforceRpcOrder`, `StashCapacity`, `SoftStallNotifyTimeout`, `HardStallNotifyTimeout`, `MaxStallDuration`, `StallTickInterval`, `DuplicateStashLogLevel`
- 4 new `SessionOrderingTests` (direct `ISessionManager` integration tests): in-order stash drain, stalled → recovered notification cycle, stash overflow → terminate session, full stall stage progression
- 1 new `DeepDesyncTests.ConcurrentDeterministicCalls_NoPatchDesync_StressGate` — fires 50 sequential Optimistic RPCs through a real client to verify the reordering gate prevents the threadpool-induced reordering that previously caused phantom patch desyncs

### Fixed

- **Concurrent Optimistic RPCs no longer trigger phantom patch desyncs** — two consecutive Optimistic calls (`await api.AddAsync(10); await api.AddAsync(20);`) could race on the threadpool and reach the entity grain in the wrong order, causing the deep-desync CRC comparison to fail even though local execution was correct. Fixed at the architectural level by the optional server-side RPC reordering gate. Production SignalR transports are unaffected; HTTP polling and similar transports should set `SessionManagerOptions.EnforceRpcOrder = true`

## [0.7.0] - 2026-04-04

### Added

- **Deep desync detection** — field-level state mutation tracking via patch CRC comparison. `[MetaServiceImpl(DeepDesync = true)]` generates a `_PatchTracked` copy of the service class where `State` routes through `PatchWrapper`. Server computes FNV-1a CRC of serialized PatchNode after each call; client compares its local CRC. Detects state divergence even when return values match. `IDesyncDiagnostics.OnPatchDesync` fires on mismatch
- `PatchTrackedClassGenerator` — Roslyn generator that copies full service class with `State` → `PatchWrapper` substitution. Skips `[MetaInit]` and `GenerateClientApi = false` methods
- `PatchCrc` — FNV-1a hash utility for patch byte comparison
- `PatchNodeDiffer` — field-by-field diff of two PatchNode trees for detailed desync reports
- `IPatchWrapper` — interface for accessing PatchNode from generated PatchWrapper classes
- `DeepDesyncReport` + `IDesyncReportSink` — desync report model and storage interface
- `RpcResponse.DeepDesyncCrc` — nullable uint CRC in server response (null = disabled)
- `EntityGrainOptions.DeepDesyncEnabled` — global runtime override (null/true/false) applied at grain activation
- Server `ServerMetaConfigurationGenerator`: generated provider dispatches to `_PatchTracked` service when PatchWrapper active; generated factory sets `DeepDesyncEnabled = true` for `[DeepDesync]` services
- Client `SimplifiedApiClientGenerator`: generated ApiClient creates `_patchTrackedService`, activates PatchNode before execution, computes local CRC, compares in Server and Optimistic modes
- `PatchableList<T>`, `PatchableDictionary<K,V>`, `PatchableHashSet<T>` — full API parity with base collection types + implicit conversion from base types
- Integration tests: 3 deep desync tests (deterministic OK, non-deterministic detected, mixed)
- Test infrastructure: `Test.Server` now uses project reference + fully generated `ServerMetaConfiguration` (no manual providers)
- **Debug API** — `IConnection.SetDebugOptionsAsync` / `MetaClient.SetDeepDesyncAsync` — client toggles deep desync per-session at runtime. Server gates via `MetaTransportOptions.AllowDebugApi` (default false, safe for production)
- `DebugOptionsRequest` / `DebugOptionsResponse` transport packets; `RpcCall.DeepDesyncRequested` per-call flag
- Expedition example: `GenerateNewMapBroken()` using `System.Random` (intentional desync), 3-button UI (ServerReplace / Optimistic / Broken), server `--desync` flag, client toggle button

### Fixed

- `ServerMetaConfigurationGenerator` assembly scan: no longer skips user assemblies with `SharedMeta` prefix — only skips framework packages

## [0.6.0] - 2026-04-03

### Added

- **Platform authentication** — pluggable `IExternalAuthValidator` for Google Play Games, Sign in with Apple, and Steam. Separate NuGet packages: `Auth.Google`, `Auth.Apple`, `Auth.Steam`
- **Account linking** — `POST /link` [Authorize] binds platform to current player; `POST /unlink` [Authorize] removes any auth key (cannot unlink last key)
- **Platform login** — `POST /login-platform` authenticates via platform token, keyed as `{platform}:{platformUserId}`
- `GET /keys` [Authorize] — list linked auth keys for current player
- `IAuthIndexGrain` — per-player auth key index
- `AuthGrain.LinkAsync`, `UnlinkAsync`, `GetPlayerIdAsync`
- Client: `MetaAuth.LoginWithPlatformAsync`, `LinkAccountAsync`, `UnlinkAsync`
- DI: `AddMetaAuthGoogle()`, `AddMetaAuthApple()`, `AddMetaAuthSteam()`
- 12 auth integration tests

## [0.5.2] - 2026-03-28

### Fixed

- BestHTTP transport asmdef: `HAS_BESTHTTP` auto-defined via `versionDefines` when `com.tivadar.besthttp` is installed — no manual scripting define needed

## [0.5.1] - 2026-03-26

### Fixed

- Generator: Query methods no longer emit unused `On{Method}_Replayed` events in API client
- Unity transport: `QueryCallAsync` implemented in `BestHttpSignalRConnection`, `BestHttpPollingConnection`, `SignalRConnection`, `UnityHttpConnection`

### Added

- Query methods in `ApiClient` as synchronous local calls — executes on client state without network roundtrip when entity is already subscribed
- Expedition example: client-side Query flow with generation mode choice (ServerReplace vs Optimistic)

## [0.5.0] - 2026-03-22

### Added

- **Query Calls** — lightweight read-only RPC to any entity without subscribing. `[MetaMethod(Query = true)]` marks methods as queryable; `[MetaMethod(Query = true, OpenAccess = true)]` bypasses EntityAccessPolicy. Client uses generated `{Service}QueryApi` with `entityId` per-proxy. Server routes through `SessionManager.QueryEntityAsync` → `EntityGrain.HandleQueryAsync`. No broadcasts, no replay, no persistence, no sequence numbers.
- `QueryCallRequest` / `QueryCallResponse` transport packets — supported across SignalR, HttpPolling, and InProcess transports
- `MetaProviderBase.HandleQueryAsync` — dispatches query call without replay/random/broadcast machinery
- `QueryClientGenerator` — generates `{Service}QueryApi` classes with typed query methods
- **`ExecutionMode.ServerReplace`** — server executes method and sends full serialized state; client replaces state wholesale. Use when full state < patch diff (map generation, full reset). `OnStateRefreshed` event fires on client.
- **`Context.GetState<TEntityState>(entityId)`** — read-only cross-entity state access from shared code via `MetaContext`. `[AlwaysInterleave]` grain method prevents deadlocks. Recorded to replay payload for deterministic client replay. Returns `null` if unknown entity type.
- `IEntityGrainBase.GetEntityStateAsync()` — read-only state serialization at grain level
- `RpcResponse.StateBytes` / `EntityBroadcast.StateBytes` — full state transport pipeline
- Generator: `ServerReplace` mode dispatch and `GenerateServerReplaceMethod` in API client
- StateBytes handling in broadcast, subscriber, and trigger replay handlers
- Integration tests: `ServerReplaceTests` (3) and `GetStateTests` (3)
- GUIDE.md: ServerReplace mode section (Section 4), GetState section (Section 6)

## [0.4.5] - 2026-03-15

### Fixed

- `EnsureSessionConnected` guard: now checks `IsSessionConnected` instead of null, preventing `ArgumentException` on empty grain key
- SignalR connection stability: server-side timeouts (`ClientTimeoutInterval=30min`, `KeepAliveInterval=15min`) now apply in all environments, not just Development mode

### Added

- BestHTTP SignalR: `PingInterval`, `ReconnectDelays`, `MaxReconnectAttempts` options; automatic reconnect via `DefaultRetryPolicy`
- Server Runner: explicit text color for dark theme readability, Copy button for log
- GUIDE.md: Quick Start section, Expedition Unity example, Common Pitfalls

## [0.4.3] - 2026-03-15

### Added

- **Service error state** — generated API clients catch exceptions during shared method execution at the framework level: log via `MetaLog.Error`, set error state (`HasError`/`ErrorException`), fire `OnServiceError` event, block further calls until `ClearError()` or reconnect
- `ServiceErrorStateException` — thrown when calling methods on an error-state service
- Service Error Handling section in GUIDE.md and SharedMeta-AI.md

## [0.4.2] - 2026-03-14

### Fixed

- CrossOptimistic cross-entity calls: `LocalEntityCaller` now propagates entity `Config` to `CrossOptimisticMetaContext`, fixing `NullReferenceException` when cross-entity service methods access `Config` properties (e.g. energy regen, game limits)
- Added `ICrossEntityResolver.GetEntityConfig(entityId)` for config resolution during client-side cross-entity execution

## [0.4.1] - 2026-03-14

### Changed

- `PlayerPrefsTokenStorage` — keys now isolated by `Application.identifier` (bundle ID), preventing token conflicts between multiple SharedMeta projects on the same device

## [0.4.0] - 2026-03-14

### Push-Based Change Tracking

- `[Tracked]` attribute on private backing fields — generates public properties with tracking setters
- `ChangeTracker` — `AsyncLocal` change buffer, activated per method call, pooled for zero allocation
- `ChangeNode` struct — flat-list tree of field changes with parent/child indices
- `ChangeValue` — discriminated union avoiding boxing for int/long/float/double/bool/string
- `ListPool<T>` and `ObjectPool<T>` — simple pools for change node lists and wrapper views
- Client-only: `ChangeTracker.Current` is null on server (zero overhead)
- `ReactiveStateGenerator` — generates `TrackingProperty` enum, tracking property setters, and `Tracked{State}` static subscription classes
- Generated API clients wrap method execution in `ChangeTracker.Activate()`/`FlushAndNotify()` — broadcasts, optimistic replay, and ServerPatch replay all fire tracked field subscriptions

### Added

- `OnStateMutated` event on generated API clients — fires after any state mutation (broadcast replay, subscriber event, reconnect)
- `GetEntityConfig<TConfig>(entityId)` on `MetaClient` and `IMetaServiceResolver` — access resolved server config from client code

### Changed

- Generated broadcast deserialization always uses `CreateReader` for correct length-prefixed format (fixes edge case with single-param broadcasts)

## [0.3.5] - 2026-03-12

### Added

- `MetaAuth` — cross-platform authentication helper with `LoginAsync` and `EnsureAuthenticatedAsync` (works on both Unity and .NET)
- `ITokenStorage` interface for persisting auth tokens across app sessions
- `PlayerPrefsTokenStorage` (Unity) — `ITokenStorage` implementation using PlayerPrefs
- `CachedToken` — token wrapper with automatic expiry validation (5-minute safety margin)
- `UnityMetaAuth` — Unity login via `UnityWebRequest`, auto-registered at startup via `[RuntimeInitializeOnLoadMethod]`
- `SharedMeta.Auth.Client` asmdef — separate assembly for Unity-dependent auth code (`noEngineReferences: false`)
- Project Wizard: generated client now uses `MetaAuth.EnsureAuthenticatedAsync` with `PlayerPrefsTokenStorage` when auth is enabled, passes `accessToken` to all transport types

## [0.3.4] - 2026-03-12

### Security

- SessionConnect claim resolution: now checks both `sub` and `ClaimTypes.NameIdentifier` (covers ASP.NET Identity and OIDC providers that remap standard JWT claims)
- SessionConnect no longer falls back to client-supplied PlayerId when user is authenticated but no identity claim found — returns an error instead
- `MetaTransportOptions.RequireAuthentication` — rejects anonymous connections at SessionConnect for both SignalR and HTTP Polling transports
- Project Wizard now registers `MetaTransportOptions { RequireAuthentication = true }` when auth is enabled

## [0.3.3] - 2026-03-11

### Fixed

- MetaInit version migration: all services now receive the original entity version instead of cascading updates; the entity version is set to the maximum across all services

## [0.3.2] - 2026-03-08

### Extensibility

- `MetaHub.SessionConnect` — now `virtual` for custom authentication/session logic in subclasses
- `MetaHub.GetOrCreateHandler()` and `GetHandler()` — now `protected` (was `private`)
- `BestHttpSignalRConnection.Hub` — protected getter for the underlying BestHTTP `HubConnection` (ext-service adapters)
- `BestHttpPollingConnection.Options` — protected getter for connection options (ext-service adapters)
- `BestHttpPollingConnection.PostAsync<T>()` and `PostRawAsync()` — now `protected` for subclass HTTP calls

## [0.3.1] - 2026-03-06

### Static Game Configuration (`[MetaConfig]`)

- `[MetaConfig]` attribute for static game configuration classes
- `IMetaConfigProvider<TConfig>` — server-side versioned config provider
- `MetaConfigVersion` struct (Major.Minor) with full serialization support (MemoryPack, MessagePack, Orleans)
- `IConfigVersionResolver` — optional DI service for A/B tests and gradual config rollouts
- `IConfigDownloadUrlResolver` — generated resolver for config download URLs
- Config version pinning per entity in `EntityGrainState.ConfigVersion`
- `IMetaConfigCache` and `IMetaConfigDownloader` client-side interfaces for config caching/downloading
- `GetConfigDownloadUrlAsync` RPC for on-demand config URL resolution
- `EntityGrainState` and `PersistedSubscriberInfo` now use `[MemoryPackable(GenerateType.VersionTolerant)]` for backward-compatible persistence

## [0.3.0] - 2026-03-06

### State Initialization (`[MetaInit]`)

- `[MetaInit]` attribute for automatic state initialization and migration during grain activation
- Signature: `Task<int> Init(int version)` — takes current version, returns new version
- `EntityGrainState.Version` persisted alongside entity state
- Server-only: not broadcast to clients (clients receive initialized state via snapshot)
- Grain not persisted after init alone — only when players interact (`_isDirty` guard)

### Persistence

- `_isDirty` flag in `EntityGrain` — skip persistence for grains activated but never interacted with
- Unified persistence pattern: `PersistIfNeeded` moved to `finally` blocks in all `Handle*` methods
- Removed force-persist from error catch blocks — errors set dirty flag naturally via sequence number increment

## [0.2.0] - 2026-03-05

### BestHTTP Transports (Unity)

New Unity transports using BestHTTP asset, included in the UPM package:

- **`BestHttpSignalRConnection`** — SignalR transport via BestHTTP. Works on all Unity platforms including WebGL. Supports JSON (LitJson) and MessagePack protocols. Wraps IFuture-based API with TaskCompletionSource for async/await.
- **`BestHttpPollingConnection`** — HTTP long-polling transport via BestHTTP. Universal compatibility.
- LitJson custom type registrations: `Guid` (string) and `byte[]` (base64) for compatibility with server-side `System.Text.Json`.
- MessagePack SignalR protocol support via `MessagePackCSharpProtocol` (requires `BESTHTTP_SIGNALR_CORE_ENABLE_MESSAGEPACK_CSHARP` scripting define).

### Client-Only Transport Packages (.NET)

New NuGet packages for .NET clients (Godot, console apps, etc.) without server dependencies:

- **`CoreGame.SharedMeta.Transport.SignalR.Client`** — `SignalRConnection` + `MetaHubProxy` implementing `IConnection`. Uses JSON protocol by default (zero extra dependencies). Supports optional `configureBuilder` callback for MessagePack or other protocols. Dependencies: `SharedMeta.Core` + `Microsoft.AspNetCore.SignalR.Client`.
- **`CoreGame.SharedMeta.Transport.HttpPolling.Client`** — `HttpPollingConnection` implementing `IConnection` using `System.Net.Http.HttpClient` and `System.Text.Json`. Drop-in replacement for Unity's `UnityHttpConnection`. Dependencies: `SharedMeta.Core` only.
- **`CoreGame.SharedMeta.Transport.SignalR.MessagePack`** — Bridge package providing `AddMetaMessagePackProtocol()` extension for `IHubConnectionBuilder`. Dependencies: `SharedMeta.Serialization.MessagePack` + `Microsoft.AspNetCore.SignalR.Protocols.MessagePack`.

All client NuGet packages target `net8.0` and `net10.0` with zero server dependencies (no Orleans, no ASP.NET FrameworkReference).

### Transport Refactoring

- **`SharedMeta.Transport.SignalR`** (server) now references `SharedMeta.Transport.SignalR.MessagePack` for the MessagePack protocol extension
- Client-side `AddMetaMessagePackProtocol(IHubConnectionBuilder)` moved to `SharedMeta.Transport.SignalR.MessagePack`
- JSON and MessagePack protocols work simultaneously — JSON clients connect to MessagePack servers via SignalR protocol negotiation

### Unity Project Wizard

- Add transport selection: SignalR (WebSocket), HTTP Polling, BestHTTP SignalR, BestHTTP HTTP
- Add serializer selection: MemoryPack or MessagePack
- Add dependency management UI with auto-detection and install buttons
- Generate complete Shared, Server, and Client projects from wizard
- BestHTTP SignalR + MessagePack: generates `MessagePackCSharpProtocol()` connection with proper scripting defines
- Fix `Directory.Packages.props` generation: update existing SharedMeta package versions, add raw serializer NuGet packages
- Fix `ResolveSharedMetaPackageVersion` returning stale version from nupkg files instead of wizard UI field
- Conditional transport assemblies via `defineConstraints` — no hard dependencies

### ServerPatch

- Fix optimistic random not advancing on client after patch application
- Fix `PatchBytes` not forwarded to broadcast subscribers via `SessionManagerGrain`
- Add `MetaRandom.Skip(count)` for advancing PRNG state without producing values

### Generator

- Ship pre-built generator DLL in UPM package (`Runtime/Analyzers/`)
- Generated client code now supports transport selection in `MetaGameClient.cs`
- MessagePack serializer support in generated code (`[MetaSerializer(SerializerType.MessagePack)]`)
- Fix subscriber broadcast dispatchers not calling service methods

### MessagePack

- Cross-assembly serialization via `CompositeResolver` — resolvers from all referenced assemblies are composed at startup
- `MetaMessagePackOptions.Configure(params Assembly[])` discovers per-assembly source-generated resolvers via reflection
- Auto-generated `GeneratedMetaMessagePackConfiguration.Configure()`

### Other

- Fix `pack-nugets.sh` MSYS path conversion (`/p:` → `-p:`)
- Remove hard `com.unity.nuget.newtonsoft-json` dependency from `package.json`
- Fix `ConnectResponse` properties: `init` → `set` to resolve MsgPack017 warning
- Add `IsAsync` property to `SubscriberMethodInfo` for correct async/sync dispatch

## [0.1.0] - 2026-02-26

- Initial public release
