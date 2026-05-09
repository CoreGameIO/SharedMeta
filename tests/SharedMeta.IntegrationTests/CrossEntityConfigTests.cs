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
    /// 0.20.0 regression test: cross-entity call where targetEntityId equals the calling
    /// entity's id (gift-to-self). Pre-0.20.0 this deadlocked because the EntityCallHandler
    /// routed through Orleans grain RPC, and EntityGrain is non-reentrant — the outer call
    /// holds the grain's task scheduler while awaiting the self-call, which can never start.
    ///
    /// 0.20.0 short-circuits this in EntityGrain.EntityCallHandler: when targetEntityId
    /// matches self and the target service is hosted on the same TState, the call is
    /// dispatched locally via MetaProviderBase.HandleNestedCallAsync — same MetaContext,
    /// same state, fresh inner replay buffer, no grain RPC.
    ///
    /// The 60s xunit timeout is the failure detector — pre-fix this test would hang.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GiftToSelf_CrossEntityCallToSameEntity_DoesNotDeadlock()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"counter_self_{Guid.NewGuid():N}";

        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Cross-entity call with targetEntityId == self. Pre-0.20.0 this hangs (grain
        // self-RPC deadlock). With the self-detect + HandleNestedCallAsync path, the call
        // runs locally on this provider as a nested operation.
        var result = await api.AddCrossEntityAsync(entityId, 42);

        // Result is clamped to MaxValue=1000; 42 < 1000, so unchanged.
        Assert.Equal(42, result);

        // The nested call mutated this entity's own state (since target == self).
        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(42, state.Sum);

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
