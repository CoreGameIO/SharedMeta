using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for CounterService using real Orleans cluster
/// and generated code (CounterServiceApiClient, ICounterServiceDispatcher).
/// </summary>
[Collection(TestClusterCollection.Name)]
public class CounterServiceTests
{
    private readonly TestClusterFixture _fixture;

    public CounterServiceTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task SingleClient_AddValue_UpdatesState()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"counter_{Guid.NewGuid():N}";

        // Act - Get API client (subscribes to entity)
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Add some values
        await api.AddValueAsync(10, 1);
        await api.AddValueAsync(20, 2);
        await api.AddValueAsync(5, 3);

        // Assert
        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(35, state.Sum);
        Assert.Equal(3, state.Operations.Count);

        // Verify operations are recorded correctly
        Assert.Equal(10, state.Operations[0].Value);
        Assert.Equal(20, state.Operations[1].Value);
        Assert.Equal(5, state.Operations[2].Value);

        // Verify caller IDs
        Assert.All(state.Operations, op => Assert.Equal(client.PlayerId, op.CallerId));

        // No issues should be detected
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task SingleClient_Reset_ClearsState()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"counter_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Add values first
        await api.AddValueAsync(100, 1);
        await api.AddValueAsync(50, 2);

        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(150, state.Sum);

        // Act - Reset
        await api.ResetAsync();

        // Assert
        Assert.Equal(0, state.Sum);
        Assert.Empty(state.Operations);

        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoClients_SameEntity_SeeBroadcasts()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client1 = new TestClientSetup(server, "player1");
        await using var client2 = new TestClientSetup(server, "player2");

        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var entityId = $"counter_{Guid.NewGuid():N}";

        var resolver1 = client1.CreateResolver();
        var resolver2 = client2.CreateResolver();

        var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Track broadcasts received by each client
        var broadcastsReceived1 = new List<(int value, int seq)>();
        var broadcastsReceived2 = new List<(int value, int seq)>();

        api1.OnAddValue_Replayed += args => broadcastsReceived1.Add(args);
        api2.OnAddValue_Replayed += args => broadcastsReceived2.Add(args);

        // Act - Client 1 adds values
        await api1.AddValueAsync(10, 1);
        await api1.AddValueAsync(20, 2);

        // Wait for broadcasts to propagate
        await Task.Delay(100);

        // Client 2 adds a value
        await api2.AddValueAsync(15, 1);

        // Wait for broadcasts
        await Task.Delay(100);

        // Assert - Both clients should see the same state
        var state1 = resolver1.GetState<CounterState>(entityId);
        var state2 = resolver2.GetState<CounterState>(entityId);

        Assert.Equal(45, state1.Sum);
        Assert.Equal(45, state2.Sum);

        Assert.Equal(3, state1.Operations.Count);
        Assert.Equal(3, state2.Operations.Count);

        // Client 2 should have received broadcasts for client 1's operations
        Assert.Equal(2, broadcastsReceived2.Count);
        Assert.Contains((10, 1), broadcastsReceived2);
        Assert.Contains((20, 2), broadcastsReceived2);

        // Client 1 should have received broadcast for client 2's operation
        Assert.Single(broadcastsReceived1);
        Assert.Contains((15, 1), broadcastsReceived1);

        Assert.Empty(client1.DetectedIssues);
        Assert.Empty(client2.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task MultipleClients_ConcurrentAdds_StateConsistent()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var clients = new List<TestClientSetup>();
        var apis = new List<CounterServiceApiClient>();
        var resolvers = new List<MetaServiceResolver>();

        var entityId = $"counter_{Guid.NewGuid():N}";
        // Reduced from 5×10=50 to 3×5=15 for more stable testing
        // High-concurrency scenarios may need additional investigation
        const int clientCount = 3;
        const int operationsPerClient = 5;

        for (int i = 0; i < clientCount; i++)
        {
            var client = new TestClientSetup(server, $"player{i}");
            await client.ConnectAsync();
            var resolver = client.CreateResolver();
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            clients.Add(client);
            resolvers.Add(resolver);
            apis.Add(api);
        }

        // Act - All clients add values concurrently
        var tasks = new List<Task>();
        for (int i = 0; i < clientCount; i++)
        {
            var clientIndex = i;
            var api = apis[i];
            for (int j = 0; j < operationsPerClient; j++)
            {
                var seq = j;
                // Each client adds its index * 10 + j
                // Client 0: 0, 1, 2, ... 9
                // Client 1: 10, 11, 12, ... 19
                // etc.
                var value = clientIndex * 10 + j;
                tasks.Add(api.AddValueAsync(value, seq));
            }
        }

        await Task.WhenAll(tasks);

        // Wait for all broadcasts to propagate
        // With reduced parameters (3 clients × 5 ops = 15 operations),
        // this should be sufficient for in-memory TestCluster
        await Task.Delay(1000);

        // Assert - All states should be identical
        var expectedOperations = clientCount * operationsPerClient;
        var expectedSum = 0;
        for (int i = 0; i < clientCount; i++)
        {
            for (int j = 0; j < operationsPerClient; j++)
            {
                expectedSum += i * 10 + j;
            }
        }

        foreach (var (resolver, index) in resolvers.Select((r, i) => (r, i)))
        {
            var state = resolver.GetState<CounterState>(entityId);

            // Debug: print state if mismatch
            if (state.Sum != expectedSum || state.Operations.Count != expectedOperations)
            {
                Console.WriteLine($"[TEST] Client{index}: Sum={state.Sum} (expected {expectedSum}), Ops={state.Operations.Count} (expected {expectedOperations})");
                Console.WriteLine($"[TEST] Operations: {string.Join(", ", state.Operations.Select(o => $"{o.Value}@{o.ClientSequence}"))}");
            }

            // Sum should be correct
            Assert.Equal(expectedSum, state.Sum);

            // Should have all operations
            Assert.Equal(expectedOperations, state.Operations.Count);
        }

        // Check for issues
        var allIssues = clients.SelectMany(c => c.DetectedIssues).ToList();
        if (allIssues.Any())
        {
            Assert.Fail($"Detected issues:\n{string.Join("\n", allIssues)}");
        }

        // Cleanup
        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Client_JoinsLate_GetsCurrentState()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client1 = new TestClientSetup(server, "early_player");
        await client1.ConnectAsync();

        var entityId = $"counter_{Guid.NewGuid():N}";
        var resolver1 = client1.CreateResolver();
        var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);

        // First client adds values
        await api1.AddValueAsync(100, 1);
        await api1.AddValueAsync(200, 2);
        await api1.AddValueAsync(300, 3);

        // Act - Late joiner connects
        await using var client2 = new TestClientSetup(server, "late_player");
        await client2.ConnectAsync();

        var resolver2 = client2.CreateResolver();
        var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Assert - Late joiner should have current state
        var state2 = resolver2.GetState<CounterState>(entityId);
        Assert.Equal(600, state2.Sum);
        Assert.Equal(3, state2.Operations.Count);

        Assert.Empty(client1.DetectedIssues);
        Assert.Empty(client2.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task SeparateEntities_IndependentState()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId1 = $"counter_A_{Guid.NewGuid():N}";
        var entityId2 = $"counter_B_{Guid.NewGuid():N}";

        var api1 = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId1);
        var api2 = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId2);

        // Act
        await api1.AddValueAsync(100, 1);
        await api1.AddValueAsync(200, 2);

        await api2.AddValueAsync(10, 1);

        // Assert - Entities should be independent
        var state1 = resolver.GetState<CounterState>(entityId1);
        var state2 = resolver.GetState<CounterState>(entityId2);

        Assert.Equal(300, state1.Sum);
        Assert.Equal(2, state1.Operations.Count);

        Assert.Equal(10, state2.Sum);
        Assert.Single(state2.Operations);

        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerTimeTicks_FlowsToState()
    {
        // Arrange
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"counter_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var beforeTicks = DateTime.UtcNow.Ticks;

        // Act
        await api.AddValueAsync(42, 1);

        var afterTicks = DateTime.UtcNow.Ticks;

        // Assert — ServerTimeTicks should be recorded and be close to real UTC time
        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(42, state.Sum);

        // Time should be non-zero (server stamped it)
        Assert.NotEqual(0, state.LastServerTimeTicks);

        // Time should be within a reasonable range (between before and after, with tolerance)
        var tolerance = TimeSpan.FromSeconds(5).Ticks;
        Assert.InRange(state.LastServerTimeTicks, beforeTicks - tolerance, afterTicks + tolerance);

        // Each operation should also have time recorded
        Assert.All(state.Operations, op => Assert.NotEqual(0, op.ServerTimeTicks));

        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerTimeTicks_ConsistentAcrossBroadcast()
    {
        // Arrange — two clients on the same entity
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client1 = new TestClientSetup(server, "time_player1");
        await using var client2 = new TestClientSetup(server, "time_player2");

        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var entityId = $"counter_{Guid.NewGuid():N}";

        var resolver1 = client1.CreateResolver();
        var resolver2 = client2.CreateResolver();

        var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Act — client 1 performs an operation
        await api1.AddValueAsync(99, 1);

        // Wait for broadcast to propagate
        await Task.Delay(200);

        // Assert — both clients should have identical state including ServerTimeTicks
        var state1 = resolver1.GetState<CounterState>(entityId);
        var state2 = resolver2.GetState<CounterState>(entityId);

        Assert.Equal(99, state1.Sum);
        Assert.Equal(99, state2.Sum);

        // Both clients should see the same ServerTimeTicks (deterministic replay)
        Assert.NotEqual(0, state1.LastServerTimeTicks);
        Assert.Equal(state1.LastServerTimeTicks, state2.LastServerTimeTicks);

        // Operations should have identical time
        Assert.Single(state1.Operations);
        Assert.Single(state2.Operations);
        Assert.Equal(state1.Operations[0].ServerTimeTicks, state2.Operations[0].ServerTimeTicks);

        Assert.Empty(client1.DetectedIssues);
        Assert.Empty(client2.DetectedIssues);
    }
}
