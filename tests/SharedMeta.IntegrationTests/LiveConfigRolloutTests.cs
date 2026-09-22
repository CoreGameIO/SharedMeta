using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// "Live publish without restart" (GUIDE §3) has to reach an entity that is already active.
///
/// It did not. The provider's observer correctly dropped its own cached instance on publish, but
/// the generated provider caches the materialized config per client version in
/// <c>_configCacheByClient</c> / <c>_serviceConfigCacheByClient_X</c>, and those were cleared only
/// from <c>OnDeactivating</c>. The next call hit the dictionary and returned the pre-publish
/// instance — <c>GetConfig</c> was never reached at all. The emitter's own comment claimed the
/// opposite ("the next call here re-fetches via GetConfig").
///
/// <see cref="RolloutState"/> is <c>EntityScope.Global</c> deliberately: Private/Shared entities
/// freeze their version behind a config pin while they have subscribers, and that freeze is the
/// documented contract for a live session. Global never pins, so it is where a rollout is genuinely
/// expected to land.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class LiveConfigRolloutTests
{
    private readonly TestClusterFixture _fixture;

    public LiveConfigRolloutTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task PublishedConfig_ReachesAlreadyActiveEntity()
    {
        SharedMeta.Test.Server.TestServerConfiguration.RolloutConfigProvider.Reset(10);

        var entityId = $"rollout_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var api = new RolloutServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

        // First read activates the entity and warms its per-client config cache.
        Assert.Equal(10, await api.ReadPayoutAsync());

        // Admin publishes new content while the entity stays active — no deactivation anywhere.
        SharedMeta.Test.Server.TestServerConfiguration.RolloutConfigProvider.Publish(42);

        Assert.Equal(42, await api.ReadPayoutAsync());
    }

    /// <summary>
    /// Without a publish, the cache must still do its job. The fix compares a generation counter
    /// rather than dropping the cache per call; this pins that repeated reads keep hitting the
    /// cached instance instead of re-materializing every time.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task WithoutPublish_ConfigStaysStable()
    {
        SharedMeta.Test.Server.TestServerConfiguration.RolloutConfigProvider.Reset(7);

        var entityId = $"rollout_stable_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var api = new RolloutServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

        Assert.Equal(7, await api.ReadPayoutAsync());
        Assert.Equal(7, await api.ReadPayoutAsync());
        Assert.Equal(7, await api.ReadPayoutAsync());
    }

    /// <summary>
    /// Several rollouts in a row on one activation — the generation comparison has to keep working
    /// after the first invalidation, not just once.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RepeatedPublishes_EachReachTheEntity()
    {
        SharedMeta.Test.Server.TestServerConfiguration.RolloutConfigProvider.Reset(1);

        var entityId = $"rollout_repeat_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var api = new RolloutServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

        Assert.Equal(1, await api.ReadPayoutAsync());

        foreach (var payout in new[] { 2, 3, 99 })
        {
            SharedMeta.Test.Server.TestServerConfiguration.RolloutConfigProvider.Publish(payout);
            Assert.Equal(payout, await api.ReadPayoutAsync());
        }
    }
}
