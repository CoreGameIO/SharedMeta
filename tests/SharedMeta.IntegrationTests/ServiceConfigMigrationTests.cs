using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.33.0 Phase B — proves <c>[MetaStateVersion]</c> schema-floor migration works when the
/// gating config type is declared via <c>[ServiceConfig]</c> instead of the legacy
/// <c>[MetaService(ConfigType=...)]</c> primary. Mirrors <see cref="MigrationTests"/>'s pattern
/// (sequential [MetaInit] steps, config pinned per step) plus new coverage for
/// <c>[NoMigrate]</c> schema-floor pinning and the Breaking-schema compat gate — neither of
/// which had any existing test coverage even for the legacy primary before this work.
/// Fixture: tests/SharedMeta.Test.Meta1/ServiceConfigMigrationFixture.cs.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ServiceConfigMigrationTests
{
    private readonly TestClusterFixture _fixture;

    public ServiceConfigMigrationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private InProcessServer CreateServer() => new InProcessServer(_fixture.CreateHandlerFactory());

    private static string UniqueId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static void RegisterClientProvider(TestClientSetup client)
    {
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<ServiceConfigMigrationConfig>(v => new ServiceConfigMigrationConfig
            {
                Major = v.Major,
                Minor = v.Minor,
            }));
    }

    private static ServiceConfigMigrationServiceEntityQueryApi GetQueryApi(TestClientSetup client, string entityId)
        => new ServiceConfigMigrationServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigBelowAllThresholds_NoMigrationRuns()
    {
        var server = CreateServer();
        var playerId = UniqueId("scm-none-");

        await using var client = new TestClientSetup(server, playerId, clientAppVersion: "0.5.0");
        RegisterClientProvider(client);
        await client.ConnectAsync();

        Assert.Equal(0, await GetQueryApi(client, playerId).GetValueAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigAt2_0_AppliesSchema1Then2_WithCorrectPinnedConfigs()
    {
        var server = CreateServer();
        var playerId = UniqueId("scm-v2-");

        await using var client = new TestClientSetup(server, playerId, clientAppVersion: "2.0.0");
        RegisterClientProvider(client);
        await client.ConnectAsync();

        // Config 2.0 meets thresholds for schema 1 and 2 — sequential migration:
        //   step 1 with Config@1.0 → Value=1; step 2 with Config@2.0 → Value=2.
        Assert.Equal(2, await GetQueryApi(client, playerId).GetValueAsync());
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// [NoMigrate]: the call must never trigger migration (state stays at whatever schema
    /// it was already at) and must see Config pinned to the schema-FLOOR branch for
    /// CurrentStateSchemaVersion — not whatever the connecting client's own live branch is.
    /// New coverage: [NoMigrate] schema-floor pinning had no existing test even for the
    /// legacy primary before this Phase B work.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task NoMigrate_ServiceConfigLinked_PinsToSchemaFloorBranch()
    {
        var server = CreateServer();
        var playerId = UniqueId("scm-nomigrate-");

        // Subscribe at 1.0.0 first — migrates to schema 1 (floor = Config@1.0).
        await using var client = new TestClientSetup(server, playerId, clientAppVersion: "1.0.0");
        RegisterClientProvider(client);
        await client.ConnectAsync();
        Assert.Equal(1, await GetQueryApi(client, playerId).GetValueAsync());

        var svc = await client.CreateResolver().GetServiceAsync<ServiceConfigMigrationServiceApiClient>(playerId);
        var floorMajor = await svc.GetFloorConfigMajorAsync();

        // Schema 1's floor is Config@1.0 — [NoMigrate] must see Major=1, and state.Value
        // must still read 1 (no migration triggered by the NoMigrate call itself).
        Assert.Equal(1, floorMajor);
        Assert.Equal(1, await GetQueryApi(client, playerId).GetValueAsync());
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Schema 2 is declared Breaking=true. Once an entity has migrated to schema 2 (via a
    /// 2.x subscriber), a legacy 1.x client's subscribe must be rejected with
    /// IncompatibleFeatureException — same behavior GlobalScopeState's Breaking gate already
    /// has, now proven for a [ServiceConfig]-linked (non-Global, non-legacy-primary) type.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Breaking_ServiceConfigLinked_RejectsOldClientOnceMigratedPastThreshold()
    {
        var entityId = UniqueId("scm-breaking-");
        var server = CreateServer();

        // Prime the entity: a 2.x subscriber migrates it to schema 2.
        await using var primer = new TestClientSetup(server, UniqueId("primer-"), clientAppVersion: "2.0.0");
        RegisterClientProvider(primer);
        await primer.ConnectAsync();
        await primer.CreateResolver().GetServiceAsync<ServiceConfigMigrationServiceApiClient>(entityId);

        // Legacy 1.x client attempts to subscribe to the SAME entity — rejected.
        await using var legacy = new TestClientSetup(server, UniqueId("legacy-"), clientAppVersion: "1.0.0");
        RegisterClientProvider(legacy);
        await legacy.ConnectAsync();
        var ex = await Assert.ThrowsAsync<IncompatibleFeatureException>(async () =>
            await legacy.CreateResolver().GetServiceAsync<ServiceConfigMigrationServiceApiClient>(entityId));
        Assert.Equal("State", ex.Requirement.FeatureKind);
        Assert.Contains(nameof(ServiceConfigMigrationState), ex.Requirement.Identifier);
    }

    private sealed class VersionEchoConfigProvider<T> : IClientMetaConfigProvider<T> where T : class
    {
        private readonly Func<MetaConfigVersion, T> _factory;
        public VersionEchoConfigProvider(Func<MetaConfigVersion, T> factory) => _factory = factory;
        public Task<T> GetConfigAsync(MetaConfigVersion version) => Task.FromResult(_factory(version));
    }
}
