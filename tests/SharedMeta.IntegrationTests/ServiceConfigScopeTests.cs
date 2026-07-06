using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using SharedMeta.Test.Server;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.33.0 Phase A — proves <c>[ServiceConfig]</c> entries get the same pin (Shared scope) and
/// <see cref="IConfigVersionResolver.CurrentClientVersion"/> substitution (Global scope) the
/// legacy <c>[MetaService(ConfigType=...)]</c> path already had. Mirrors
/// <see cref="EntityScopeTests"/>'s Shared/Global cases exactly, but against the
/// <see cref="ServiceConfigScopeFixtures"/>-equivalent fixtures declared purely via
/// <c>[ServiceConfig]</c> (no legacy ConfigType at all) — see
/// tests/SharedMeta.Test.Meta1/ServiceConfigScopeFixtures.cs.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ServiceConfigScopeTests
{
    private readonly TestClusterFixture _fixture;

    public ServiceConfigScopeTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private InProcessServer CreateServer() => new InProcessServer(_fixture.CreateHandlerFactory());

    private static string UniqueId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static void RegisterSharedScopeClientProvider(TestClientSetup client)
    {
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<ServiceConfigSharedScopeConfig>(v => new ServiceConfigSharedScopeConfig
            {
                Major = v.Major,
                Minor = v.Minor,
                Patch = v.Patch,
            }));
    }

    private static void RegisterGlobalScopeClientProvider(TestClientSetup client)
    {
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<ServiceConfigGlobalScopeConfig>(v => new ServiceConfigGlobalScopeConfig
            {
                Major = v.Major,
                Minor = v.Minor,
            }));
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  Shared: first subscriber pins a patch; a joiner resolving a DIFFERENT patch on
    //  the same Major.Minor runs under the PINNED patch, not its own resolved one.
    //  Same shape as EntityScopeTests.Shared_NewSubscriberAtDifferentPatch_RunsUnderInitialPin,
    //  but the config is declared via [ServiceConfig], not the legacy ConfigType.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(Timeout = 60_000)]
    public async Task Shared_ServiceConfig_NewSubscriberAtDifferentPatch_RunsUnderInitialPin()
    {
        var entityId = UniqueId("sc-shared-patch-");
        var aId = UniqueId("a-");
        var bId = UniqueId("b-");
        var server = CreateServer();

        await using var clientA = new TestClientSetup(server, aId, clientAppVersion: "1.0.0");
        RegisterSharedScopeClientProvider(clientA);
        await clientA.ConnectAsync();
        await clientA.CreateResolver().GetServiceAsync<ServiceConfigSharedScopeServiceApiClient>(entityId);

        await using var clientB = new TestClientSetup(server, bId, clientAppVersion: "1.0.5");
        RegisterSharedScopeClientProvider(clientB);
        await clientB.ConnectAsync();
        var bApi = await clientB.CreateResolver().GetServiceAsync<ServiceConfigSharedScopeServiceApiClient>(entityId);

        await bApi.RecordConfigAsync();

        var qapi = new ServiceConfigSharedScopeServiceQueryApi(clientB.Connection, clientB.Serializer).EntityApi(entityId);
        var last = await qapi.GetLastConfigAsync();
        Assert.Equal(1, last.Major);
        Assert.Equal(0, last.Minor);
        Assert.Equal(0, last.Patch);   // pin's patch, NOT B's 1.0.5
        Assert.Empty(clientA.DetectedIssues);
        Assert.Empty(clientB.DetectedIssues);
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  Global: two clients on DIFFERENT app versions both dispatch under
    //  IConfigVersionResolver.CurrentClientVersion — proving the [ServiceConfig] entry
    //  substitutes the resolver's version, not each caller's own, exactly like the
    //  legacy primary already does for Global-scope entities.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(Timeout = 60_000)]
    public async Task Global_ServiceConfig_DifferentClientVersions_BothSeeCurrentClientVersionBranch()
    {
        var entityId = UniqueId("sc-global-");
        var aId = UniqueId("a-");
        var bId = UniqueId("b-");
        var server = CreateServer();

        var prevResolverVersion = TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion;
        TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = "2.0.0";
        try
        {
            await using var clientA = new TestClientSetup(server, aId, clientAppVersion: "1.0.0");
            RegisterGlobalScopeClientProvider(clientA);
            await clientA.ConnectAsync();
            var aApi = await clientA.CreateResolver().GetServiceAsync<ServiceConfigGlobalScopeServiceApiClient>(entityId);

            await using var clientB = new TestClientSetup(server, bId, clientAppVersion: "2.0.0");
            RegisterGlobalScopeClientProvider(clientB);
            await clientB.ConnectAsync();
            var bApi = await clientB.CreateResolver().GetServiceAsync<ServiceConfigGlobalScopeServiceApiClient>(entityId);

            // A's own client version is "1.0.0" (would resolve Major=1 on its own), but Global
            // scope must substitute CurrentClientVersion ("2.0.0" → Major=2) for every caller.
            var majorSeenByA = await aApi.RecordConfigAsync();
            Assert.Equal(2, majorSeenByA);

            var majorSeenByB = await bApi.RecordConfigAsync();
            Assert.Equal(2, majorSeenByB);

            Assert.Empty(clientA.DetectedIssues);
            Assert.Empty(clientB.DetectedIssues);
        }
        finally { TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = prevResolverVersion; }
    }

    private sealed class VersionEchoConfigProvider<T> : IClientMetaConfigProvider<T> where T : class
    {
        private readonly Func<MetaConfigVersion, T> _factory;
        public VersionEchoConfigProvider(Func<MetaConfigVersion, T> factory) => _factory = factory;
        public Task<T> GetConfigAsync(MetaConfigVersion version) => Task.FromResult(_factory(version));
    }
}
