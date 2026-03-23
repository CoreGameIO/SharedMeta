using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for cross-entity state reading via Context.GetState&lt;T&gt;.
/// Verifies that shared code can read other entities' states with replay support.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class GetStateTests
{
    private readonly TestClusterFixture _fixture;

    public GetStateTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task GetState_ReadsOtherEntityState()
    {
        // Arrange — create two entities with known state
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityA = $"getstate_a_{Guid.NewGuid():N}";
        var entityB = $"getstate_b_{Guid.NewGuid():N}";

        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        // Set up entity B with known state
        await apiB.AddValueAsync(100, 1);
        await apiB.AddValueAsync(50, 2);
        var stateB = resolver.GetState<CounterState>(entityB);
        Assert.Equal(150, stateB.Sum);

        // Act — entity A reads entity B's state
        var result = await apiA.ReadOtherEntityStateAsync(entityB);

        // Assert — entity A received entity B's Sum
        Assert.Equal(150, result);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task GetState_ReturnsDefaultForNewEntity()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityA = $"getstate_caller_{Guid.NewGuid():N}";
        var nonExistentEntity = $"getstate_nonexistent_{Guid.NewGuid():N}";

        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);

        // Act — read state of an entity that has never been used
        // Orleans will activate it with default state (Sum = 0)
        var result = await apiA.ReadOtherEntityStateAsync(nonExistentEntity);

        // Assert — returns default Sum (0), not -1 (null)
        Assert.Equal(0, result);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task GetState_DeterministicReplay_NoDesync()
    {
        // Arrange — set up two entities
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityA = $"getstate_replay_a_{Guid.NewGuid():N}";
        var entityB = $"getstate_replay_b_{Guid.NewGuid():N}";

        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        // Populate entity B
        await apiB.AddValueAsync(42, 1);
        await apiB.AddValueAsync(8, 2);

        // Act — multiple cross-entity reads
        var result1 = await apiA.ReadOtherEntityStateAsync(entityB);
        var result2 = await apiA.ReadOtherEntityStateAsync(entityB);

        // Assert — consistent results, no desync
        Assert.Equal(50, result1);
        Assert.Equal(50, result2);
        Assert.Empty(client.DetectedIssues);
    }
}
