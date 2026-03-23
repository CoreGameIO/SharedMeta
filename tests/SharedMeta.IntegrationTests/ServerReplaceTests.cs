using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for ServerReplace execution mode.
/// Verifies that server sends full state and client replaces it wholesale.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ServerReplaceTests
{
    private readonly TestClusterFixture _fixture;

    public ServerReplaceTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerReplace_ReplacesClientState()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"replace_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Add values first via Server mode
        await api.AddValueAsync(10, 1);
        await api.AddValueAsync(20, 2);

        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(30, state.Sum);
        Assert.Equal(2, state.Operations.Count);

        // Act — call ServerReplace method, which resets and sets new value
        var result = await api.ReplaceResetAsync(42);

        // Assert — state is fully replaced
        state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(42, result);
        Assert.Equal(42, state.Sum);
        Assert.Empty(state.Operations); // operations cleared by ReplaceReset
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerReplace_BroadcastReplacesOtherClientState()
    {
        // Arrange — two clients on same entity
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client1 = new TestClientSetup(server, "player1_replace");
        await using var client2 = new TestClientSetup(server, "player2_replace");
        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var resolver1 = client1.CreateResolver();
        var resolver2 = client2.CreateResolver();
        var entityId = $"replace_broadcast_{Guid.NewGuid():N}";

        var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Both see initial state
        await api1.AddValueAsync(100, 1);
        await Task.Delay(200); // wait for broadcast

        var state2 = resolver2.GetState<CounterState>(entityId);
        Assert.Equal(100, state2.Sum);

        // Act — client1 calls ServerReplace
        await api1.ReplaceResetAsync(7);
        await Task.Delay(200); // wait for broadcast

        // Assert — client2 received full state via broadcast
        state2 = resolver2.GetState<CounterState>(entityId);
        Assert.Equal(7, state2.Sum);
        Assert.Empty(state2.Operations);
        Assert.Empty(client2.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerReplace_FiresOnStateRefreshed()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"replace_event_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        CounterState? refreshedState = null;
        api.OnStateRefreshed += s => refreshedState = s;

        // Act
        await api.ReplaceResetAsync(99);

        // Assert — OnStateRefreshed was fired with the new state
        Assert.NotNull(refreshedState);
        Assert.Equal(99, refreshedState!.Sum);
        Assert.Empty(client.DetectedIssues);
    }
}
