using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Patch;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;
using Xunit.Abstractions;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for ServerPatch execution mode.
/// Verifies that when a method is overridden to ServerPatch mode,
/// the server generates a state diff patch and the client applies it correctly.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ServerPatchIntegrationTests
{
    private readonly TestClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ServerPatchIntegrationTests(TestClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task ServerPatch_SingleAdd_StateUpdatedViaPatch()
    {
        // Override AddValue to ServerPatch mode
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "Add", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"sp_single_{Guid.NewGuid():N}";

            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            // Act — call AddValue in ServerPatch mode
            await api.AddValueAsync(42, 1);

            // Assert — state should be updated via patch
            var state = resolver.GetState<CounterState>(entityId);
            Assert.Equal(42, state.Sum);
            Assert.Single(state.Operations);
            Assert.Equal(42, state.Operations[0].Value);
            Assert.Equal(client.PlayerId, state.Operations[0].CallerId);

            Assert.Empty(client.DetectedIssues);
            _output.WriteLine($"ServerPatch single add: Sum={state.Sum}, Ops={state.Operations.Count}");
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact]
    public async Task ServerPatch_MultipleAdds_StateAccumulates()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "Add", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"sp_multi_{Guid.NewGuid():N}";
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            // Act — multiple calls
            await api.AddValueAsync(10, 1);
            await api.AddValueAsync(20, 2);
            await api.AddValueAsync(30, 3);

            // Assert
            var state = resolver.GetState<CounterState>(entityId);
            Assert.Equal(60, state.Sum);
            Assert.Equal(3, state.Operations.Count);
            Assert.Equal(10, state.Operations[0].Value);
            Assert.Equal(20, state.Operations[1].Value);
            Assert.Equal(30, state.Operations[2].Value);

            Assert.Empty(client.DetectedIssues);
            _output.WriteLine($"ServerPatch multiple adds: Sum={state.Sum}, Ops={state.Operations.Count}");
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact]
    public async Task ServerPatch_Reset_ClearsState()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "Add", ExecutionMode.ServerPatch);
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "Reset", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"sp_reset_{Guid.NewGuid():N}";
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            // Add values first
            await api.AddValueAsync(100, 1);
            await api.AddValueAsync(200, 2);

            var state = resolver.GetState<CounterState>(entityId);
            Assert.Equal(300, state.Sum);

            // Act — reset via ServerPatch
            await api.ResetAsync();

            // Assert
            Assert.Equal(0, state.Sum);
            Assert.Empty(state.Operations);

            Assert.Empty(client.DetectedIssues);
            _output.WriteLine("ServerPatch reset: state cleared");
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact]
    public async Task ServerPatch_TwoClients_BroadcastAppliesPatch()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "Add", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client1 = new TestClientSetup(server, "sp_player1");
            await using var client2 = new TestClientSetup(server, "sp_player2");

            await client1.ConnectAsync();
            await client2.ConnectAsync();

            var entityId = $"sp_broadcast_{Guid.NewGuid():N}";
            var resolver1 = client1.CreateResolver();
            var resolver2 = client2.CreateResolver();

            var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);
            var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);

            // Track broadcasts
            var broadcasts2 = new List<(int value, int seq)>();
            api2.OnAddValue_Replayed += args => broadcasts2.Add(args);

            // Act — client 1 adds value
            await api1.AddValueAsync(77, 1);
            await Task.Delay(200);

            // Assert — both clients should see the updated state
            var state1 = resolver1.GetState<CounterState>(entityId);
            var state2 = resolver2.GetState<CounterState>(entityId);

            Assert.Equal(77, state1.Sum);
            Assert.Equal(77, state2.Sum);
            Assert.Single(state1.Operations);
            Assert.Single(state2.Operations);
            Assert.Equal("sp_player1", state2.Operations[0].CallerId);

            // Client 2 should have received the broadcast
            Assert.Single(broadcasts2);
            Assert.Contains((77, 1), broadcasts2);

            Assert.Empty(client1.DetectedIssues);
            Assert.Empty(client2.DetectedIssues);
            _output.WriteLine("ServerPatch broadcast: both clients converge");
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact]
    public async Task ServerPatch_MixedModes_SomeServerPatchSomeServer()
    {
        // Only Add is ServerPatch; Reset stays as Server mode
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "Add", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"sp_mixed_{Guid.NewGuid():N}";
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            // ServerPatch add
            await api.AddValueAsync(50, 1);
            await api.AddValueAsync(25, 2);

            var state = resolver.GetState<CounterState>(entityId);
            Assert.Equal(75, state.Sum);
            Assert.Equal(2, state.Operations.Count);

            // Server-mode reset (normal replay)
            await api.ResetAsync();

            Assert.Equal(0, state.Sum);
            Assert.Empty(state.Operations);

            // ServerPatch add again after reset
            await api.AddValueAsync(99, 1);
            Assert.Equal(99, state.Sum);
            Assert.Single(state.Operations);

            Assert.Empty(client.DetectedIssues);
            _output.WriteLine("ServerPatch mixed modes: works correctly");
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact]
    public async Task ServerPatch_LateJoiner_GetsCurrentState()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "Add", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client1 = new TestClientSetup(server, "sp_early");
            await client1.ConnectAsync();

            var entityId = $"sp_late_{Guid.NewGuid():N}";
            var resolver1 = client1.CreateResolver();
            var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);

            // Early client adds values via ServerPatch
            await api1.AddValueAsync(100, 1);
            await api1.AddValueAsync(200, 2);
            await api1.AddValueAsync(300, 3);

            // Late joiner connects
            await using var client2 = new TestClientSetup(server, "sp_late");
            await client2.ConnectAsync();

            var resolver2 = client2.CreateResolver();
            var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);

            // Assert — late joiner should see full state (via snapshot, not patches)
            var state2 = resolver2.GetState<CounterState>(entityId);
            Assert.Equal(600, state2.Sum);
            Assert.Equal(3, state2.Operations.Count);

            // Late joiner also does ServerPatch add
            await api2.AddValueAsync(400, 1);
            await Task.Delay(200);

            // Both should converge
            var state1 = resolver1.GetState<CounterState>(entityId);
            state2 = resolver2.GetState<CounterState>(entityId);
            Assert.Equal(1000, state1.Sum);
            Assert.Equal(1000, state2.Sum);
            Assert.Equal(4, state1.Operations.Count);
            Assert.Equal(4, state2.Operations.Count);

            Assert.Empty(client1.DetectedIssues);
            Assert.Empty(client2.DetectedIssues);
            _output.WriteLine("ServerPatch late joiner: state converges");
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }
}
