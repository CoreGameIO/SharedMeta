using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
// The server API is compiled into the server project (the shared assembly fences it behind
// SHAREDMETA_SERVER, which shared projects do not define).
using SharedMeta.Test.Meta1.Server;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Config-aware argument transformers: only the id crosses the wire, and each side rebuilds the
/// definition from its own resolved <c>[ServiceConfig]</c>. Every call passes a definition with a
/// wrong ("stale") name, so a name that survives means the transformer never ran, "v0-…" means it
/// ran against an unresolved default, and "v1-…"/"v2-…" names the config branch that produced it.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ConfigArgumentTransformerTests
{
    private readonly TestClusterFixture _fixture;

    public ConfigArgumentTransformerTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private static TestClientSetup NewClient(InProcessServer server, string playerId, string? clientAppVersion = null)
    {
        var client = clientAppVersion == null
            ? new TestClientSetup(server, playerId)
            : new TestClientSetup(server, playerId, clientAppVersion: clientAppVersion);
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<ItemCatalogConfig>(v => new ItemCatalogConfig { Major = v.Major }));
        return client;
    }

    private static ItemDef Stale(int id) => new() { Id = id, Name = "stale" };

    /// <summary>
    /// Optimistic: the caller normalizes the argument through Box/Unbox before its local body
    /// runs, so the local prediction and the server's confirmation see the same definition.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Optimistic_RebuildsDefinitionFromConfig_OnBothSides()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = NewClient(server, "alice");
        await client.ConnectAsync();
        var entityId = $"cfg_transform_{Guid.NewGuid():N}";
        var api = await client.CreateResolver().GetServiceAsync<ConfigTransformServiceApiClient>(entityId);

        var predicted = await api.PickAsync(Stale(7), 3);
        // Server-mode call queued behind the optimistic one: its reply arrives after the
        // confirmation, so a desync would already be recorded.
        await api.PickServerAsync(Stale(7), 3);

        Assert.Equal("7:v1-item7:0:3", predicted);
        Assert.Equal("v1-item7", api.State.LastName);
        Assert.Equal(2, api.State.Calls);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerMode_UnboxesUnderServerConfig()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = NewClient(server, "alice");
        await client.ConnectAsync();
        var entityId = $"cfg_transform_srv_{Guid.NewGuid():N}";
        var api = await client.CreateResolver().GetServiceAsync<ConfigTransformServiceApiClient>(entityId);

        var result = await api.PickServerAsync(Stale(9), 4);

        Assert.Equal("9:v1-item9:0:4", result);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// The config comes from version resolution, not a fixed instance: a 2.x client's entity
    /// resolves the 2.0 branch on both sides.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ResolvedBranch_FollowsClientVersion()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        // Own player id: the cluster remembers the highest version an id connected with, and a
        // shared id pinned to 2.x would fail every later 1.x connect under it.
        await using var client = NewClient(server, "cfg_transform_v2", "2.1.0");
        await client.ConnectAsync();
        var entityId = $"cfg_transform_v2_{Guid.NewGuid():N}";
        var api = await client.CreateResolver().GetServiceAsync<ConfigTransformServiceApiClient>(entityId);

        var predicted = await api.PickAsync(Stale(7), 1);
        var confirmed = await api.PickServerAsync(Stale(7), 1);

        Assert.Equal("7:v2-item7:0:1", predicted);
        Assert.Equal("7:v2-item7:0:1", confirmed);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Another subscriber replays the caller's boxed argument, so its broadcast handler must
    /// unbox with its own config exactly as the server did.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Broadcast_ObserverUnboxesUnderItsConfig()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var caller = NewClient(server, "alice");
        await using var observer = NewClient(server, "bob");
        await caller.ConnectAsync();
        await observer.ConnectAsync();

        var entityId = $"cfg_transform_bcast_{Guid.NewGuid():N}";
        var callerApi = await caller.CreateResolver().GetServiceAsync<ConfigTransformServiceApiClient>(entityId);
        var observerResolver = observer.CreateResolver();
        await observerResolver.GetServiceAsync<ConfigTransformServiceApiClient>(entityId);

        await callerApi.PickAsync(Stale(8), 21);

        await WaitForAsync(() => observerResolver.GetState<ConfigTransformState>(entityId).Calls == 1);
        var observed = observerResolver.GetState<ConfigTransformState>(entityId);
        Assert.Equal("v1-item8", observed.LastName);
        Assert.Equal(21, observed.LastTag);
        Assert.Empty(observer.DetectedIssues);
    }

    /// <summary>
    /// State + config: the count comes from the receiver's state, the name from its config; the
    /// caller's count (99) and name must both be discarded.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StateAndConfig_RebuildsFromBoth()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = NewClient(server, "alice");
        await client.ConnectAsync();
        var entityId = $"cfg_transform_owned_{Guid.NewGuid():N}";
        var api = await client.CreateResolver().GetServiceAsync<ConfigTransformServiceApiClient>(entityId);

        await api.AddStockAsync(5, 3);
        var predicted = await api.UseOwnedAsync(new OwnedItem { Id = 5, Count = 99, Name = "stale" }, 1);
        await api.PickServerAsync(Stale(5), 2);

        Assert.Equal("5:v1-item5:3:1", predicted);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Cross-entity hop from a server-side meta call: boxed with the calling entity's config,
    /// unboxed by the target's dispatcher with its own; the target's subscriber replays it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CrossEntityHop_BoxesWithCallerConfig_TargetUnboxes()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = NewClient(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();
        var targetId = $"cfg_transform_target_{Guid.NewGuid():N}";
        var relayId = $"cfg_transform_relay_{Guid.NewGuid():N}";
        await resolver.GetServiceAsync<ConfigTransformServiceApiClient>(targetId);
        var relay = await resolver.GetServiceAsync<ConfigTransformRelayServiceApiClient>(relayId);

        var result = await relay.RelayAsync(targetId, 6);

        Assert.Equal("6:v1-item6:0:5", result);
        await WaitForAsync(() => resolver.GetState<ConfigTransformState>(targetId).Calls == 1);
        Assert.Equal("v1-item6", resolver.GetState<ConfigTransformState>(targetId).LastName);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Server code outside any meta call has no config to box with. Documented limitation: the
    /// call fails up front with a message naming the transformer and the config, instead of
    /// sending a value the entity would unbox differently.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerApi_OutsideMetaCall_FailsNamingTheConfig()
    {
        var entityId = $"cfg_transform_api_{Guid.NewGuid():N}";
        var api = _fixture.GrainFactory.GetServerApi<IConfigTransformService>(entityId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => api.PickServerAsync(Stale(1), 1));

        Assert.Contains("ItemDefTransformer", ex.Message);
        Assert.Contains("ItemCatalogConfig", ex.Message);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "Condition was not met before the timeout.");
    }
}
