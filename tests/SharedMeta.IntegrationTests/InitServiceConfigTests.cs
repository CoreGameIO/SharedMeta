using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using SharedMeta.Test.Meta1.Server;
using SharedMeta.Test.Server;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// [MetaInit] on a fresh entity reads its <c>[ServiceConfig]</c> — state without
/// <c>[MetaStateVersion]</c>, so nothing in the generated init path sets the config context and
/// the framework must bind it before init runs. Regression: init used to see
/// <c>Context.Configs == null</c> and NRE on the first config read, failing every subscribe to
/// a not-yet-persisted entity.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class InitServiceConfigTests
{
    private readonly TestClusterFixture _fixture;

    public InitServiceConfigTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private static string UniqueId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private async Task<TestClientSetup> ConnectAsync(InProcessServer server, string clientAppVersion)
    {
        var client = new TestClientSetup(server, UniqueId("p-"), clientAppVersion: clientAppVersion);
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<InitStartKitConfig>(v => new InitStartKitConfig { Major = v.Major, StartGold = 100 * v.Major }));
        await client.ConnectAsync();
        return client;
    }

    [Theory(Timeout = 60_000)]
    [InlineData("1.0.0", 1)]
    [InlineData("2.0.0", 2)]
    public async Task Subscribe_FreshEntity_InitSeesServiceConfigOfSubscriberBranch(string clientAppVersion, int expectedMajor)
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = await ConnectAsync(server, clientAppVersion);

        var api = await client.CreateResolver().GetServiceAsync<InitStartKitServiceApiClient>(UniqueId("kit-sub-"));

        Assert.Equal(expectedMajor, api.State.InitConfigMajor);
        Assert.Equal(100 * expectedMajor, api.State.Gold);

        await api.AddGoldAsync(5);
        Assert.Equal(100 * expectedMajor + 5, api.State.Gold);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerApi_NeverSubscribedEntity_InitSeesServiceConfig()
    {
        var entityId = UniqueId("kit-srv-");
        var prev = TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion;
        TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = "2.0.0";
        try
        {
            // First touch is a server-originated call: no subscriber, no caller version — init
            // resolves under the server's current client version.
            var api = _fixture.GrainFactory.GetServerApi<IInitStartKitService>(entityId);
            Assert.Equal(200, await api.ReadGoldAsync());
        }
        finally { TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = prev; }
    }

    private static FreshInitServiceEntityQueryApi FreshQuery(TestClientSetup client, string entityId)
        => new FreshInitServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

    /// <summary>
    /// A fresh entity is created directly at the schema its client allows: one [MetaInit] call
    /// targeting schema 3 under the client's own config (3.2), no 1→2→3 walk and no historical
    /// 1.0 / 2.0 / 3.0 branches fetched.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task FreshEntity_CreatedDirectlyAtClientSchema_UnderClientConfig()
    {
        var entityId = UniqueId("fresh-");
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, entityId, clientAppVersion: "3.2.0");
        await client.ConnectAsync();

        var query = FreshQuery(client, entityId);
        Assert.Equal(3, await query.GetValueAsync());
        Assert.Equal("0>3@3.2", await query.GetCallsAsync());
    }

    /// <summary>
    /// A persisted entity climbs the remaining schemas one step at a time, every step under the
    /// config of the client that triggered the migration (3.2) — not the 2.0 / 3.0 transition
    /// branches, which an async-backed provider has never materialized.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PersistedEntity_MigratesStepByStep_UnderCurrentClientConfig()
    {
        var entityId = UniqueId("stepped-");

        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server, entityId, clientAppVersion: "1.4.0");
            await client.ConnectAsync();
            Assert.Equal(1, await FreshQuery(client, entityId).GetValueAsync());
        }

        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server, entityId, clientAppVersion: "3.2.0");
            await client.ConnectAsync();
            var query = FreshQuery(client, entityId);
            Assert.Equal(3, await query.GetValueAsync());
            Assert.Equal("0>1@1.4;1>2@3.2;2>3@3.2", await query.GetCallsAsync());
        }
    }
}
