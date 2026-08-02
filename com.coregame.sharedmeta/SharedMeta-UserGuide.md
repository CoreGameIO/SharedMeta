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
- For server-side persistence through real Orleans storage providers (Azure Tables, Redis, ADO.NET, or `FileGrainStorage` in its default Orleans mode), add `[GenerateSerializer]` on the class plus `[Id(n)]` on each property. Unity compiles these via the bundled `Orleans.Stubs` (no-op attributes), so the same source builds on both sides:

```csharp
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
[SharedState]
public partial class GameState : ISharedState
{
    [Key(0), MemoryPackOrder(0), Id(0)] public int Score { get; set; }
    [Key(1), MemoryPackOrder(1), Id(1)] public List<string> Items { get; set; } = new();
}
```

You can skip the Orleans attributes only if you stay on `FileGrainStorage` with `UseOrleansSerializer = false` — that mode routes persistence through `IMetaSerializer` (MemoryPack/MessagePack) instead of the Orleans serializer.

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

    [MetaMethod(Mode = ExecutionMode.LocalQuery)]
    int ItemCount();   // client-side read over State, no RPC — client calls api.ItemCountSync()
}
```

> **LocalQuery is sync by default.** A `LocalQuery` method defaults to `Sync = SyncApi.OnlySync`,
> so the generator emits only the synchronous `{Method}Sync(...)` on the client API. It reads the
> local `State` snapshot in the calling frame with no server round-trip. Set `Sync` explicitly to
> override: `SyncApi.Generate` emits both sync and async, `SyncApi.None` emits only the async wrapper
> (which still runs locally — handy if the method may later move to a server-backed mode).

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

Add a `[MetaInit]` method to your service implementation to initialize or migrate state. Two signatures are supported — the generator picks the matching call shape automatically:

```csharp
// Single-arg form (legacy)
[MetaInit] public Task<int> Init(int version) { ... }

// Two-arg form (0.19.0+) — also receives the target schema for this step
[MetaInit] public Task<int> Init(int version, int target) { ... }
```

Use the two-arg form when you have `[MetaStateVersion]` migration breakpoints (see [Per-Client Config Branches & State Migration](#per-client-config-branches--state-migration)). It lets each migration step branch by both source and target version:

```csharp
[MetaServiceImpl(typeof(IGameService), typeof(GameState))]
public partial class GameServiceImpl : IGameService
{
    [MetaInit]
    public Task<int> InitState(int version, int target)
    {
        if (version < 1 && target >= 1)
        {
            // Base init — Config pinned to 1.x branch
            State.Score = 0;
            State.Items = new List<string> { "starter-sword" };
        }
        if (version < 2 && target >= 2)
        {
            // 1→2 migration — Config pinned to 2.0 transition version
            State.NewField = Config.NewFieldDefault;
        }
        return Task.FromResult(Math.Max(version, target));
    }
}
```

**When `[MetaInit]` runs (changed in 0.19.0):**
- Activation no longer pre-migrates fresh entities. Init/migration is deferred to the first client `SubscribeAsync` or RPC call, capped to that client's resolved config branch — so a 1.x client never triggers a 2.0 migration on a fresh entity.
- For services with `[MetaInit]` but no `[MetaStateVersion]`, base init runs exactly once on first interaction.

**Available during `[MetaInit]`:**
- `Context.Random` and `Context.ServerRandom` — deterministic randomness for init
- `Config` — pinned to the appropriate branch for this step
- `Context.Version` / `Context.ConfigVersion` — current schema and config version (0.19.0+)
- `State` — entity state to mutate

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
[MetaService(StateType = typeof(GameState))]
[ServiceConfig(typeof(GameConfig), "Config")]
public interface IGameService : IMetaService { ... }
```

`[ServiceConfig]` is repeatable — declare it more than once to give a service several independently-versioned configs, each with its own accessor name:

```csharp
[MetaService(StateType = typeof(GameState))]
[ServiceConfig(typeof(GameConfig), "Config")]
[ServiceConfig(typeof(SeasonConfig), "Season")]
public interface IGameService : IMetaService { ... }
```

The older `[MetaService(ConfigType = typeof(GameConfig))]` / `DefaultConfig = true` still work (marked `[Obsolete]` — a compiler warning nudging you toward `[ServiceConfig]`, not a break).

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
    public MetaConfigVersion CurrentVersion => new(2, 0, 0);
    public GameConfig GetConfig(MetaConfigVersion version) => /* fetch by version */;
    public MetaConfigVersion ResolveLatestMatching(int major, int minor) => new(major, minor, 0);
    public string? GetDownloadUrl(MetaConfigVersion version)
        => $"https://example.com/config/{version.Major}/{version.Minor}";
}

// Register in DI:
services.AddSingleton<IMetaConfigProvider<GameConfig>>(new GameConfigProvider());
```

`MetaConfigVersion` is `Major.Minor.Patch` as of 0.19.0. `ResolveLatestMatching` is called by the `[MetaConfigVersion]` resolver to materialize `Patch` captures (e.g. `1.6.x` → `1.6.17`).

### 5. Config Admin & Bootstrap (0.27.0+ / 0.28.0)

The framework ships the full publish-side stack so a host wires it with one DI call instead of per-config registrations + a hand-rolled bootstrapper. The bootstrapper is **typed two-phase** (0.28.0): `GetVersionAsync<TConfig>` / `GetBytesAsync<TConfig>` are dispatched per `[MetaConfig]` type by the generator-emitted `IConfigCatalog` — `TConfig` is closed at the compile-time call site, no reflection, no `Type` arguments.

```csharp
siloBuilder.ConfigureServices(services =>
{
    services.ConfigureMeta(svc => { /* impls */ });

    services.ConfigureConfigs(o =>
    {
        // Project-side typed IConfigBootstrapper. Wizard emits ConfigBootstrapper.cs with one
        // typed branch per [MetaConfig] in the template (default-instance pattern). Swap each
        // branch for a real source (manifest, CDN, GDrive) when a content pipeline arrives.
        o.UseBootstrapper<ConfigBootstrapper>();

        // LoadIfEmpty — only seed when registry has nothing for that type.
        // LoadIfNew (default) — seed when the specific version isn't published yet.
        // LoadAlways — always run the bootstrapper (idempotent re-publish on identical bytes).
        o.Strategy = ConfigSeedStrategy.LoadIfNew;
    });
});
```

What this does, all in:

- The generator emits per-`[MetaConfig]` `BroadcastingConfigProvider<T>` registrations + a `GeneratedConfigCatalog` typed dispatcher — no hand-listing, no reflection.
- `ConfigBootstrapHostedService` runs on startup: catalog visits each entry typed, then `bootstrapper.GetVersionAsync<TConfig>` → strategy gate → `bootstrapper.GetBytesAsync<TConfig>(version)` → `IConfigRegistry.PublishIfChangedAsync<TConfig>` → audit row through `IConfigMetadataGrain` → broadcast provider warm-up.
- `DefaultClientVersionService` is auto-registered as `IConfigVersionResolver` + `IHostedService` — see *Client App Version* below.
- `IConfigAdminGrain` is auto-discovered by Orleans — admin tools join the cluster as a client and call it directly (no HTTP controller). Typed extensions (`admin.DownloadAsync<TConfig>(version)`, `admin.UploadAsync<TConfig>(...)`) live in `SharedMeta.Server.Core.Config.Admin.ConfigAdminGrainExtensions`.

**Bootstrapper choice.** Pick one per project:

```csharp
o.UseBootstrapper<ConfigBootstrapper>(); // project-side typed dispatcher (Wizard default)
o.UseDirectorySeed("data/drafts");       // built-in, read-only scan {root}/{Type.Name}/{M.m.p}.bin
o.UseBootstrapper<MyBootstrapper>();     // project-typed: implements IConfigBootstrapper
o.UseBootstrapper(new MyInstance());     // project instance
```

`IConfigBootstrapper` is two methods (`GetVersionAsync` returns the project's offered version or `null` to skip, `GetBytesAsync` materializes when a publish is decided) — implement directly for inline custom sources (embedded resources, internal CDN, etc.).

**Pre-bootstrap project work** (e.g. dev YAML → bin compile that feeds `UseDirectorySeed`): register your own `IHostedService` **before** `ConfigureConfigs`. `IHostedService.StartAsync` runs in registration order, so the prep step completes before `ConfigBootstrapHostedService` scans.

**Admin operations from any project tool joined to the cluster as a client:**

```csharp
var admin = cluster.GetGrain<IConfigAdminGrain>(0);
ConfigOverview[]  list      = await admin.ListConfigsAsync();
byte[]            bytes     = await admin.DownloadAsync(name, version);
ConfigOverview    afterPub  = await admin.UploadAsync(name, version, bytes, origin: "edit", publishedBy: user, notes: null);
bool              dropped   = await admin.UnpublishAsync(name, version, deletedBy: user);
```

### 6. Client App Version (0.27.0+)

Bind a `"ClientVersion"` section in `appsettings.json` and the framework's `DefaultClientVersionService` handles the three roles formerly stitched together project-side (current default, rejection gates, server build label):

```json
{
  "ClientVersion": {
    "Current": "0.1.0",
    "Min":     "",
    "Max":     "",
    "Server":  ""
  }
}
```

| Field | Role |
|---|---|
| `Current` | Bootstrap value for `ICurrentClientVersionGrain` (the cluster-wide `IConfigVersionResolver.CurrentClientVersion`). Admin overrides survive restarts. |
| `Min` / `Max` | Bootstrap mirror into `MetaTransportOptions.MinClientVersion` / `MaxClientVersion`. Runtime overrides flow through the existing `IVersionPolicyGrain` → `ClientVersionPolicy`. |
| `Server` | One-shot mirror into `MetaTransportOptions.ServerVersion`. Not runtime-managed (a "new server version" is a redeploy). |

Runtime control from admin tooling — one grain, four operations:

```csharp
var admin = cluster.GetGrain<IConfigAdminGrain>(0);
ClientVersionSnapshot snap = await admin.GetClientVersionsAsync();
await admin.SetCurrentClientVersionAsync("0.2.0", "release-bot");
await admin.SetMinClientVersionAsync("0.1.5", "release-bot");
await admin.SetMaxClientVersionAsync(null,    "release-bot");  // clear override
```

Cross-silo propagation: the admin grain pushes through to `ICurrentClientVersionGrain` / `IVersionPolicyGrain` and locally pokes `DefaultClientVersionService.SetCurrentLocally()` so the silo serving the admin request reflects the change immediately. Other silos pick it up via the 30-second background poll.

### Per-Client Config Branches & State Migration

> **Added in 0.19.0**: route connecting clients to their own config branch and migrate entity state schema gradually as live config advances. See the [framework guide](https://github.com/CoreGameIO/SharedMeta/blob/main/docs/GUIDE.md#per-client-config-branches--state-migration) for the full treatment.

#### Route clients to a config branch

Declare on the config class which client app version maps to which config:

```csharp
[MetaConfig(Default = true)]
[MemoryPackable, MessagePackObject]
[MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]   // 1.x clients → 1.x configs
[MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]   // 2.x clients → 2.x configs
public partial class GameConfig { … }
```

Pattern grammar — `Major.Minor.Patch` with `x` (capture, propagates to Config), `N+` (range), `*` (wildcard), or literal values. The framework resolves each connecting client per-RPC and pins `Context.Config` to the matching branch.

#### Schema migration breakpoints

Declare on a state class that schema N requires config ≥ X:

```csharp
[SharedState]
[MemoryPackable(GenerateType.VersionTolerant)]
[MetaStateVersion(2, "2.0", typeof(GameConfig))]   // schema 2 needs GameConfig >= 2.0
public partial class ProfileState : ISharedState { … }
```

Migration is **client-aware** — a 1.x client connecting to a fresh entity gets schema 1 (base init only). A 2.x client triggers lazy migration to schema 2 with `Context.Config` pinned to the 2.0 transition version. The per-entity `IsClientConfigCompatible` gate rejects subscribes from clients whose config branch can't satisfy the entity's persisted schema.

#### Per-method controls

- **`[NoMigrate]`** — skip lazy migration; pin `Context.Config` to the schema-floor branch. Use for cross-entity "administrative" calls (gift sending) so the recipient isn't force-upgraded.
- **`[MinStateVersion(N)]`** — cap migration target at schema N for this method.

```csharp
[MetaMethod(Mode = ExecutionMode.Server)]
[NoMigrate]
void DepositGift(GiftItem item);
```

#### MaxClientVersion + downgrade gate

```csharp
builder.Services.AddSingleton(new MetaTransportOptions
{
    ServerVersion    = "2.0.0",
    MinClientVersion = "1.1.0",
    MaxClientVersion = "2.0.*",   // accept any 2.0.x; reject 2.1+
    RequireAuthentication = true,
});
```

`IPlayerVersionGrain` records the highest version a player has connected with — subsequent connects from a *lower* version are rejected (downgrade prevention).

#### Entity Scope (`[EntityScope]`) — 0.21.0+

Declare the sharing model of an entity on its state class. Default (no attribute) = `Private`.

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

| Scope | Subscribers | Config-version pin | Optimistic? |
|---|---|---|---|
| `Private` | Owner only (others may cross-entity-call without subscribing) | Established on owner's first connect; lives for grain's active lifetime | Safe |
| `Shared` | First subscriber establishes pin; joiners validated (`Major.Minor` must match; patch downgrade allowed) | First subscriber's resolved versions | Safe |
| `Global` | Open subscribe gated on schema compatibility | **Never pinned** — always `IConfigVersionResolver.CurrentClientVersion`-resolved | Not safe under config rollout — use Server / ServerPatch |

Set up `MetaClientOptions.ClientAppVersion` on the client so server-side resolution can route per-client config branches:

```csharp
var client = new MetaClient(connection, serializer, new MetaClientOptions
{
    PlayerId         = playerId,
    ClientAppVersion = "2.0.0",   // 0.21.0+ — stamped on SessionConnect + every RPC/subscribe
});
```

**0.27.0+**: `services.ConfigureConfigs(...)` auto-registers `DefaultClientVersionService` as `IConfigVersionResolver`, backed by `ICurrentClientVersionGrain` and bootstrapped from `appsettings.json "ClientVersion:Current"`. Only register your own when you need a non-passthrough `ResolveVersion` (A/B routing, staged rollouts):

```csharp
services.AddSingleton<IConfigVersionResolver>(new MyResolver());

public class MyResolver : IConfigVersionResolver
{
    public string CurrentClientVersion => "2.0.0";
    public MetaConfigVersion ResolveVersion(string stateTypeName, string entityId, MetaConfigVersion defaultVersion)
        => defaultVersion;
}
```

**Admin force-migrate:** drop support for an old config branch by iterating entity IDs and calling `entityGrain.ForceMigrateToFloorAsync("3.0.0")` on each. Runs the full `[MetaStateVersion]` migration ladder up to the floor's required schema and persists. No subscriber required.

### Client-Side Config (0.15.0+)

Each `[MetaConfig]` type is materialized by an `IClientMetaConfigProvider<TConfig>` registered on the resolver. The generator-emitted `Add{Service}Services()` extension installs a `StaticConfigProvider<TConfig>(new TConfig())` by default — out of the box, the client receives the bundled config compiled into shared code.

> **Common mistake:** assuming `StaticConfigProvider` auto-updates on subscribe. It does not — `GetConfigAsync(version)` ignores the version argument and always returns the instance passed at construction. The server reports the pinned version, but the static provider returns the bundled object regardless. Server-side config changes only reach the client through `DownloadingConfigProvider` (or `CompositeConfigProvider` with a downloading primary).

To override (download from server with disk caching), register a custom provider **before** `RegisterAllServices()`:

```csharp
resolver.RegisterConfigProvider<GameConfig>(new DownloadingConfigProvider<GameConfig>(
    urlResolver: client.ConfigDownloadUrlResolver,      // keyed by config type, not state type
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
    options.AccessTokenLifetime  = TimeSpan.FromMinutes(30); // short — renewed via refresh (0.30.0+)
    options.RefreshTokenLifetime = TimeSpan.FromDays(30);
});
builder.Services.AddSingleton(new MetaTransportOptions { RequireAuthentication = true });
app.MapMetaAuthEndpoints(); // POST /meta/auth/login
```

### Identity Validation (0.37.1+)

`AddMetaAuth` also registers an `IPlayerIdentityValidator`, and `SessionConnect` uses it to reject a token whose player no longer exists. This matters after any auth-store wipe (fresh environment, dropped volume, deleted account): a JWT carries only a signature, an expiry and a `sub` claim, so it keeps authenticating until it expires. Without the check the server trusts the claim and creates empty entity state for a PlayerId nobody can log in as again.

The rejection reads "Authentication rejected" and carries `SessionConnectFailureReason.IdentityUnknown`, so the client's auth-failure path (`MetaClientOptions.OnConnectAuthFailedAsync`) drops the cached token and re-logins into a real PlayerId.

The gate needs `RequireAuthentication = true` — without it the PlayerId is client-supplied, not claim-derived. Turn it off with `ValidatePlayerIdentity = false` if authenticated connections may legitimately carry identities your auth store doesn't hold (service accounts, externally minted tokens). Custom identity sources implement `IPlayerIdentityValidator` and register before `AddMetaAuth`.

### Token Expiry and Reconnect (0.37.2+)

The case that bites on mobile: the app sits in the background, the access token expires (default lifetime 30 min), the transport reconnects with the dead token and the handshake is rejected. For this to heal automatically you need a **provider-based** connection — a fixed token string is captured once and re-sent verbatim forever:

```csharp
var tokens = new MetaTokenManager(authUrl, deviceId, tokenStorage);
var connection = new SignalRConnection(url, tokens.GetTokenAsync);   // provider, not a string
var client = new MetaClient(connection, serializer, new MetaClientOptions
{
    AccessTokenSource = tokens,
});
tokens.StartAutoRefresh();
```

With `AccessTokenSource` set, both the cold connect and the background reconnect re-acquire the token and retry the handshake once (the transport is re-dialled too — SignalR reads its token during the handshake). Refresh keeps the same PlayerId, so subscriptions survive.

On resume from background call `await tokens.GetTokenAsync()` yourself — the process is frozen while backgrounded, so the auto-refresh loop doesn't tick.

`AuthenticationRequired` (credential expired, account fine) recovers automatically. `IdentityUnknown` (account gone) does not — a full login yields a different PlayerId, which a live session can't adopt, so restart your boot/login flow.

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
// Unity: PlayerPrefsTokenStorage stores token in PlayerPrefs.
// Pass deviceId so dev builds running multiple instances on one device — or with random/
// rotating deviceIds (UseRandomDeviceId) — each get their own token slot. Without scoping,
// a fresh deviceId picks up a JWT cached for a previous PlayerId and reuses the wrong account.
ITokenStorage storage = new PlayerPrefsTokenStorage(deviceId);

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

### Token Refresh (0.30.0+)

Login returns a long-lived **refresh token** with the short access JWT. `EnsureAuthenticatedAsync` uses it automatically — when the access token has expired but the refresh token is still valid, it silently refreshes (rotating the token) instead of a full re-login. Nothing to change in the code above; just keep using `EnsureAuthenticatedAsync` with an `ITokenStorage`.

Server-side, refresh sessions are stored per-player (SHA-256-hashed) with **rotation + reuse detection** — replaying a used token revokes that whole session. Tune lifetimes via `AccessTokenLifetime` / `RefreshTokenLifetime`.

For **long sessions** that outlive the access token, drive the connection from a `MetaTokenManager` so a reconnect picks up a fresh token automatically:

```csharp
var tokens = new MetaTokenManager($"{serverUrl}/meta/auth", deviceId, storage);
tokens.StartAutoRefresh();                                            // optional: renew before expiry
var connection = new SignalRConnection($"{serverUrl}/meta", tokens.GetTokenAsync); // provider, not a fixed string
// HTTP transports: set AccessTokenProvider = tokens.GetTokenAsync on the connection options.
```

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
| **CrossOptimistic** | Like Optimistic, but the method also touches other `ISharedState` entities owned by the same player. | Split-profile patterns where one player's data spans multiple states (Profile + Inventory + Quest, etc.). Do not use against entities mutated by other clients. |
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

### Notification Methods (Entity → Entity Fire-and-Forget) — 0.22.0+

Peer of Signal on the **cross-entity axis**: Signal = client → entity fire-and-forget, Notification = entity → entity fire-and-forget. Use when one service wants to inform another entity about a state change without paying for a grain-to-grain round-trip.

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
            GetIClanService(S.ClanId).AddPower(amount);  // void, no await
        return Task.CompletedTask;
    }
}
```

- Method must return `Task` or `void` (no `Task<T>`)
- Implicit `GenerateClientApi = false` — clients never originate notifications
- Caller never observes the target's result; errors in target are logged server-side only
- Generator emits the cross-entity caller as `void {Method}(args)`, not `Task {Method}Async(args)` — pre-0.22 `await GetIFoo(id).BarAsync(...)` call sites compile-error and migrate
- Server-side runs through Orleans `[OneWay]` grain entry — source grain does not wait

**Do not use** when caller reads target state after the call, or needs transactional consistency, or needs to react to target's failure.

See [docs/GUIDE.md § Notification Methods](../docs/GUIDE.md#notification-methods-entity--entity-fire-and-forget--0220) for the full contract + perf numbers.

---

## Calling a Service from Server Code (0.35.0+)

Admin tools, framework grains and background jobs run outside any meta call, so they have no `Context` and no cross-entity accessor. The generator emits a typed server-side API for every service that declares a `StateType`:

```csharp
await grainFactory.GetServerApi<IProfileService>(playerId).AddResourcesAsync("gold", 500);
```

The call dispatches through the entity grain like any other: state changes are recorded, broadcast to every subscriber and persisted, so a connected player sees the effect right away. The entity doesn't need to be active — a cold one activates, migrates and persists.

**Admin-only methods** are ordinary meta methods that clients cannot call:

```csharp
[MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]
void AddResources(string resource, int amount);
```

`GenerateClientApi = false` means no client API is generated **and** the server rejects a forged client packet — while the server API still exposes the method. Authorization is up to you: reaching this API already means running inside the silo. For a web admin panel, add a thin ASP.NET endpoint in the silo host and call the server API from it.

This code is server-only — it compiles in your server project and is fenced out of Unity/client builds, which have no Orleans.

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

### Sibling Services on the Same Entity (0.20.0)

A "sibling" is another `[MetaServiceImpl]` hosted on the same `TState` — both impls live in the same entity grain. 0.20.0 dispatches sibling calls in-process (typed C#, no serialization, no grain RPC) while keeping the existing cross-entity API intact.

**Implicit (gift-to-anyone with possible self-id):** declare the sibling service as a dep, the existing cross-entity getter handles self-detect:

```csharp
[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState),
    typeof(IInventoryService))]                 // declare InventoryService dep
public partial class ProfileService : IProfileService
{
    public async Task SendGift(string targetEntityId, int itemId)
    {
        // Self-id → typed in-process call to the cached sibling impl. Other id →
        // real cross-entity grain RPC. Same line of code handles both.
        await GetIInventoryService(targetEntityId).GrantItemAsync(itemId);
    }
}
```

This fixes the "gift-to-self deadlock" — pre-0.20.0 the call hung because Orleans grain RPC into a non-reentrant grain stalls awaiting itself.

**Explicit (this entity's own sibling):** the generator emits an async accessor that returns the original interface (sync methods stay sync) and resolves the callee's typed `Config` per-service:

```csharp
public async Task ApplyDailyBonus()
{
    var inv = await GetIInventoryServiceSiblingAsync();
    inv.GrantItem("daily_bonus", 1);
    State.LastBonusUtc = Context.ServerTimeTicks;
}
```

The await resolves the callee's typed `Config` through its own `IMetaConfigProvider<TConfig>`. Multi-config siblings (different `[MetaConfig]` types on the same state) each see their own typed config branch.

**What's preserved** across the sibling boundary: state, randoms (`Context.Random`/`ServerRandom`/named), `PatchWrapper`, `ChangeTracker`, and by-reference args. **What's not** (by design): `[Transformer]` Box/Unbox (transformers are a serialization-boundary concern; sibling-bypass skips it), implicit rollback on exception (sibling shares the outer's mutation pipeline — partial mutations stay if a sibling throws).

**Required:** every dep declared in `[MetaServiceImpl(..., typeof(IDep))]` MUST carry `[MetaService(StateType = typeof(...))]` on the dep interface — otherwise the generator emits `#error`.

**Hiding sibling-only / cross-entity-only methods from clients:** mark them `[MetaMethod(GenerateClientApi = false)]`. As of 0.20.0, this both suppresses client API generation **and** rejects forged client RPCs server-side (a modified client crafting an `RpcCallRequest` with the matching service+method gets `"... is not callable from clients"`). Cross-entity (`HandleCallFromEntityAsync`) and sibling-bypass paths are server-internal and continue to work — the trust boundary is the public method on the calling entity, which authorized the originating client through its own access policy.

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

### Framework contracts (e.g. matchmaking)
Inherit the contract on the service interface, and mark each implementing method
`[MetaMethod(Mode = ExecutionMode.Server, GenerateClientApi = false)]` on the impl class — the
declarations live in the framework assembly, so the attribute has to sit on your implementation.
```csharp
[MetaService(StateType = typeof(ProfileState))]
public interface IProfileService : IMetaService, ILobbyListener { }

[MetaServiceImpl(typeof(IProfileService), typeof(ProfileState))]
public partial class ProfileService : IProfileService
{
    [MetaMethod(Alias = "OnMatchFound", Mode = ExecutionMode.Server, GenerateClientApi = false)]
    public void OnMatchFound(MatchFoundEvent e) => State.CurrentGameId = e.MatchId;
}
```
Server code reaches the service through the generated mirror of the contract — inject it once,
pass the entity id per call:
```csharp
await _players.OnMatchFoundAsync(entityId, evt);
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

## Rider Plugin (optional)

[`SharedLibs/RiderPlugin`](https://github.com/CoreGameIO/SharedLibs/tree/main/RiderPlugin) is a Rider plugin that links each `[MetaMethod]` on a `[MetaService]` interface to every method generated for it (`*ApiClient.{Name}Async / Sync / Signal`, `*EntityQueryApi.{Name}Async`, the `{I}EntityCaller` cross-entity proxy and its three runtime impls). With it installed:

- **Find Usages** on the interface method also returns every call site that goes through any generated layer — and vice versa.
- **Ctrl+Click / Go to Declaration** offers the original `[MetaMethod]` as an additional jump target on generated client methods.

Discovery is attribute-driven via `[GeneratedFromMetaMethod]`, which the SharedMeta source generator emits on every mirror starting with version 0.16.0 — so older generated code is invisible to the plugin until you rebuild.

**Install:** grab the pre-built zip from [`SharedLibs/RiderPlugin/dist/`](https://github.com/CoreGameIO/SharedLibs/tree/main/RiderPlugin/dist) (or build it locally with `./gradlew buildPlugin` per the project README), then **Settings → Plugins → ⚙ → Install Plugin from Disk…** and restart Rider.

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

## Read-only validator (0.29.1+)

For every `[MetaServiceImpl]` method whose interface declaration has `Mode = ExecutionMode.LocalQuery` or `Mode = ExecutionMode.Query`, the generator walks the body syntactically and emits `#error` diagnostics for contract violations. Default: on.

### Coverage

| Pattern | Caught |
|---|---|
| Direct assignment to State (`State.X = …`, `State.X.Y[i] = …`, `State.X[k] = v`) | ✅ |
| Collection mutators on State — `State.X.{Add\|AddRange\|Remove\|RemoveAt\|RemoveAll\|RemoveRange\|Insert\|InsertRange\|Clear\|Sort\|Reverse\|Enqueue\|Dequeue\|Push\|Pop\|TrimExcess\|EnsureCapacity\|TryAdd}(...)` | ✅ |
| `Context.Random`, `Context.ServerRandom` consumption | ✅ |
| Generator-emitted `{Name}Random` accessors (`[NamedRandom]`) consumption | ✅ |
| Cross-entity calls — `GetI{Service}(entityId).{Method}(...)` pattern | ✅ |
| Class-level State aliases — `private GameState profile;`, `protected GameState S => State;`, etc. (any field/property typed as the impl's state) | ✅ |
| Local-variable State aliases — `var p = State;`, `var c = State.Cards;`, `foreach (var x in State.Cells)`, `out var v` from `State.Map.TryGetValue(k, out var v)` | ✅ |
| Same-class helper recursion — walks helpers in the same `[MetaServiceImpl]` class (depth limit 5, visited set) so violations behind `=> CountStuff();` surface with both FQNs | ✅ |
| Helpers in **other** classes (cross-file) | ❌ |
| Local **reassignment** (`var p = State; p = otherThing; p.X = 1;` — false positive, walker still treats `p` as State) | ❌ (Level-2 control-flow, deferred) |
| Virtual override targets, delegate-invoked callbacks, reflection (`GetField/SetValue`) | ❌ |
| User-defined collection mutator names (`State.Inventory.AddItem(x)`) | ❌ — extend `CollectionMutators` set in `ReadOnlyMethodValidator.cs` or rename for clarity |

### Diagnostic format

```
SharedMeta: [MetaMethod(Mode = ExecutionMode.LocalQuery)] 'ClanService.CardsInHand' at line 42:13 must not mutate State (direct assignment detected). Switch to ExecutionMode.Optimistic / Server for writes, or move the write out of this method.
```

Includes method FQN + line:col + concrete violation. For aliases, the alias name appears in the message: `must not mutate State alias 'profile'`. For helpers reached via recursion, the FQN shows the chain: `'ClanService.CardsInHand → CountCards'`.

### Opt-out

Add to the host project's `<PropertyGroup>`:

```xml
<SharedMetaDisableReadOnlyValidator>true</SharedMetaDisableReadOnlyValidator>
```

The validator becomes a no-op (other generators continue to run). Useful when generator-side compile cost matters more than catching contract violations at build time — though the cost is usually negligible.
