using SharedMeta.Client;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.20.3: read-only subscription snapshot API on <see cref="MetaServiceResolver"/> and
/// <see cref="MetaClient"/> for debug inspection (which entities is the client tracking,
/// which config branch got pinned, which services are wired locally).
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SubscriptionIntrospectionTests
{
    private readonly TestClusterFixture _fixture;

    public SubscriptionIntrospectionTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task GetSubscribedEntities_ReturnsSnapshotForSubscribedEntities()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var entityA = $"counter_A_{Guid.NewGuid():N}";
        var entityB = $"counter_B_{Guid.NewGuid():N}";

        // Before any subscribe — empty snapshot, debug string sentinel.
        Assert.Empty(resolver.GetSubscribedEntities());
        Assert.Equal("(no subscribed entities)", resolver.DescribeSubscriptions());

        await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        var snapshot = resolver.GetSubscribedEntities();
        Assert.Equal(2, snapshot.Count);

        var infoA = snapshot.Single(s => s.EntityId == entityA);
        Assert.Equal(typeof(CounterState), infoA.StateType);
        Assert.Equal(typeof(CounterConfig), infoA.ConfigType);
        Assert.NotNull(infoA.Config);
        Assert.IsType<CounterConfig>(infoA.Config);
        Assert.Contains("ICounterService", infoA.ServiceNames);

        // State reference is live — same instance the typed query returns.
        var liveState = resolver.GetState<CounterState>(entityA);
        Assert.Same(liveState, infoA.State);
    }

    [Fact(Timeout = 30_000)]
    public async Task DescribeSubscriptions_RendersHumanReadableSummary()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var entityId = $"counter_describe_{Guid.NewGuid():N}";
        await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var summary = resolver.DescribeSubscriptions();

        Assert.Contains(entityId, summary);
        Assert.Contains(nameof(CounterState), summary);
        Assert.Contains(nameof(CounterConfig), summary);
        Assert.Contains("ICounterService", summary);
    }
}
