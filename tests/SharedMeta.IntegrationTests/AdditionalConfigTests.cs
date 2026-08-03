using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.33.0 [ServiceConfig] coverage — a service with a legacy primary ConfigType plus two
/// independently-versioned/published [ServiceConfig] entries (Balance, Season), resolved
/// synchronously in Optimistic mode (client predictive execution AND server confirmation),
/// unlike multi-config siblings / StatelessMetaService's server-only resolution path. Also
/// covers the fully symmetric case (no legacy ConfigType, only [ServiceConfig] entries).
/// Fixture: <see cref="IAdditionalConfigService"/> / <see cref="AdditionalConfigService"/> and
/// <see cref="ISymmetricConfigService"/> / <see cref="SymmetricConfigService"/> in
/// SharedMeta.Test.Meta1/AdditionalConfigFixture.cs. Server-side providers in
/// SharedMeta.Test.Server/TestServerConfiguration.cs stamp Major/Minor from the resolved
/// version so tests can assert which branch resolved.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class AdditionalConfigTests
{
    private readonly TestClusterFixture _fixture;

    public AdditionalConfigTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Registers matching client-side providers for the legacy primary + both [ServiceConfig]
    /// entries, mirroring the server's test providers (Major/Minor echo the resolved version) so
    /// Optimistic client-side prediction and server confirmation agree (no desync noise).
    /// </summary>
    private static void RegisterClientConfigProviders(TestClientSetup client)
    {
        client.MetaClient.Resolver.RegisterConfigProvider(
            new StaticConfigProvider<AdditionalFixturePrimaryConfig>(new AdditionalFixturePrimaryConfig { Value = 42 }));
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<AdditionalFixtureBalanceConfig>(v => new AdditionalFixtureBalanceConfig { Major = v.Major, Minor = v.Minor }));
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<AdditionalFixtureSeasonConfig>(v => new AdditionalFixtureSeasonConfig { Major = v.Major, Minor = v.Minor }));
    }

    private static void RegisterSymmetricClientConfigProviders(TestClientSetup client)
    {
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<SymmetricFixtureShopConfig>(v => new SymmetricFixtureShopConfig { Major = v.Major }));
        client.MetaClient.Resolver.RegisterConfigProvider(
            new VersionEchoConfigProvider<SymmetricFixtureVaultConfig>(v => new SymmetricFixtureVaultConfig { Major = v.Major }));
    }

    /// <summary>
    /// Optimistic call resolves the legacy primary + both [ServiceConfig] entries synchronously
    /// on the client (predictive execution) and the server (confirmation) — no
    /// NotSupportedException, no desync. Balance is pinned to 1.0 regardless of client version
    /// (its own [MetaConfigVersion] rule); Season tracks the connecting client's version —
    /// proving the two [ServiceConfig] entries resolve independently of each other and of the
    /// legacy primary.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Optimistic_ReadAll_ResolvesIndependentAdditionalConfigs()
    {
        var entityId = $"additional_config_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        // Own player id, like every other version-specific client in this file: IPlayerVersionGrain
        // is cluster-wide and remembers the highest version an id ever connected with, so pinning a
        // shared literal ("alice") to 2.x makes every later 1.0.0 connect under that id fail the
        // downgrade gate — for the rest of the run, in whatever class happens to run next.
        await using var client = new TestClientSetup(server, "alice_v23", clientAppVersion: "2.3.0");
        RegisterClientConfigProviders(client);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var svc = await resolver.GetServiceAsync<AdditionalConfigServiceApiClient>(entityId);

        var seasonMajor = await svc.ReadAllAsync();

        // Season mirrors the client's own major version (2.x → Major=2); Balance is
        // independently pinned to 1.0 by its own [MetaConfigVersion] rule regardless of
        // client version — proving the two additional configs don't share a branch.
        Assert.Equal(2, seasonMajor);
        Assert.Equal(42, svc.State.LastPrimary);
        Assert.Equal(1, svc.State.LastBalanceMajor);
        Assert.Equal(2, svc.State.LastSeasonMajor);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Two clients on different ClientAppVersion branches, each first-subscribing to their OWN
    /// entity, see independently-resolved Season versions (1.x vs 2.x) while Balance stays
    /// pinned at 1.0 for both — confirming per-entity-first-subscriber resolution, not a global
    /// shared/frozen value. 0.33.0+: [ServiceConfig] entries are now pinned on first subscribe
    /// for Private/Shared scope (parity with the legacy primary) — a SECOND subscriber joining
    /// the SAME already-pinned entity now correctly sees the pin's branch, not their own
    /// version (see EntityScopeTests.Shared_NewSubscriberAtDifferentPatch_RunsUnderInitialPin
    /// for the equivalent legacy-primary behavior). Two separate entities isolate each
    /// client's own first-subscribe pin instead.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DifferentClientVersions_ResolveDifferentSeasonBranchesIndependently()
    {
        var entityIdV1 = $"additional_config_multi_v1_{Guid.NewGuid():N}";
        var entityIdV2 = $"additional_config_multi_v2_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var clientV1 = new TestClientSetup(server, "alice_v1", clientAppVersion: "1.5.0");
        RegisterClientConfigProviders(clientV1);
        await clientV1.ConnectAsync();

        await using var clientV2 = new TestClientSetup(server, "alice_v2", clientAppVersion: "2.7.0");
        RegisterClientConfigProviders(clientV2);
        await clientV2.ConnectAsync();

        var svcV1 = await clientV1.CreateResolver().GetServiceAsync<AdditionalConfigServiceApiClient>(entityIdV1);
        var svcV2 = await clientV2.CreateResolver().GetServiceAsync<AdditionalConfigServiceApiClient>(entityIdV2);

        var seasonV1 = await svcV1.ReadAllAsync();
        var seasonV2 = await svcV2.ReadAllAsync();

        Assert.Equal(1, seasonV1);
        Assert.Equal(2, seasonV2);
        // Balance is independent of Season and of the client's own version — always 1.0.
        Assert.Equal(1, svcV1.State.LastBalanceMajor);
        Assert.Equal(1, svcV2.State.LastBalanceMajor);
        Assert.Empty(clientV1.DetectedIssues);
        Assert.Empty(clientV2.DetectedIssues);
    }

    /// <summary>
    /// 0.33.0+ pin parity: a SECOND subscriber joining an entity a first client already
    /// subscribed to sees the [ServiceConfig] entry's PINNED branch, not their own client
    /// version's — same behavior the legacy primary Config already has for Private/Shared
    /// scope (<see cref="EntityScopeTests.Shared_NewSubscriberAtDifferentPatch_RunsUnderInitialPin"/>).
    /// This fixture mixes a legacy primary ConfigType with [ServiceConfig] entries, so this
    /// proves the pin mechanism applies to BOTH independently on the same entity.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SecondSubscriberOnSameEntity_SeesSeasonPinnedToFirstSubscribersBranch()
    {
        var entityId = $"additional_config_pin_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var clientV1 = new TestClientSetup(server, "bob_v1", clientAppVersion: "1.5.0");
        RegisterClientConfigProviders(clientV1);
        await clientV1.ConnectAsync();
        await clientV1.CreateResolver().GetServiceAsync<AdditionalConfigServiceApiClient>(entityId);

        await using var clientV2 = new TestClientSetup(server, "bob_v2", clientAppVersion: "2.7.0");
        RegisterClientConfigProviders(clientV2);
        await clientV2.ConnectAsync();
        var svcV2 = await clientV2.CreateResolver().GetServiceAsync<AdditionalConfigServiceApiClient>(entityId);

        // Both read the SAME entity's Season — the pin established by clientV1's first
        // subscribe (1.x branch) wins for clientV2 too, even though clientV2's own app
        // version would naturally resolve Season to the 2.x branch.
        var seasonSeenByV2 = await svcV2.ReadAllAsync();
        Assert.Equal(1, seasonSeenByV2);
        Assert.Empty(clientV1.DetectedIssues);
        Assert.Empty(clientV2.DetectedIssues);
    }

    /// <summary>
    /// A service with NO legacy ConfigType — only two [ServiceConfig] entries, symmetric with
    /// each other (no privileged "primary"). Proves the new declaration path works standalone,
    /// not merely as an add-on to the legacy mechanism.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Optimistic_SymmetricService_ResolvesBothServiceConfigsWithNoLegacyPrimary()
    {
        var entityId = $"symmetric_config_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "bob_v20", clientAppVersion: "2.0.0");
        RegisterSymmetricClientConfigProviders(client);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var svc = await resolver.GetServiceAsync<SymmetricConfigServiceApiClient>(entityId);

        var shopMajor = await svc.ReadBothAsync();

        Assert.Equal(2, shopMajor);
        Assert.Equal(2, svc.State.LastShopMajor);
        Assert.Equal(1, svc.State.LastVaultMajor);
        Assert.Empty(client.DetectedIssues);
    }

    private sealed class VersionEchoConfigProvider<T> : IClientMetaConfigProvider<T> where T : class
    {
        private readonly Func<MetaConfigVersion, T> _factory;
        public VersionEchoConfigProvider(Func<MetaConfigVersion, T> factory) => _factory = factory;
        public Task<T> GetConfigAsync(MetaConfigVersion version) => Task.FromResult(_factory(version));
    }
}
