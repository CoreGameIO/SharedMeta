using SharedMeta.Client;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Tests that Config is propagated to CrossOptimisticMetaContext during cross-entity calls.
/// Regression test for the bug where LocalEntityCaller did not set ctx.Config,
/// causing NullReferenceException when cross-entity methods accessed Config properties.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class CrossEntityConfigTests
{
    private readonly TestClusterFixture _fixture;

    public CrossEntityConfigTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Core regression test: CrossOptimistic method on entity A calls entity B cross-entity.
    /// Entity B's method accesses Config.MaxValue — without the fix, Config is null → NRE.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CrossOptimistic_CrossEntityCall_ConfigIsPropagated()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityA = $"counter_A_{Guid.NewGuid():N}";
        var entityB = $"counter_B_{Guid.NewGuid():N}";

        // Subscribe to both entities (both need to be subscribed for cross-entity to work)
        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        // Act — CrossOptimistic call on entity A that calls entity B's AddClamped (which accesses Config)
        var result = await apiA.AddCrossEntityAsync(entityB, 42);

        // Assert — if Config was null, we'd get NRE instead of reaching here
        Assert.Equal(42, result); // 42 < MaxValue(1000), so clamped = 42

        // Entity B's state should have the clamped value added
        var stateB = resolver.GetState<CounterState>(entityB);
        Assert.Equal(42, stateB.Sum);

        // No desyncs
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Verify clamping works — value exceeds Config.MaxValue.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CrossOptimistic_CrossEntityCall_ValueClampedByConfig()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityA = $"counter_A_{Guid.NewGuid():N}";
        var entityB = $"counter_B_{Guid.NewGuid():N}";

        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        // Act — value exceeds MaxValue (1000)
        var result = await apiA.AddCrossEntityAsync(entityB, 5000);

        // Assert — should be clamped to 1000
        Assert.Equal(1000, result);

        var stateB = resolver.GetState<CounterState>(entityB);
        Assert.Equal(1000, stateB.Sum);

        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Verify that Config is available via GetEntityConfig API on the resolver.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GetEntityConfig_ReturnsConfig_WhenEntitySubscribed()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"counter_{Guid.NewGuid():N}";

        // Subscribe
        await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Act
        var config = resolver.GetEntityConfig<CounterConfig>(entityId);

        // Assert
        Assert.NotNull(config);
        Assert.Equal(1000, config!.MaxValue);
    }
}
