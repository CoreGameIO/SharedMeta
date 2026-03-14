using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Core.Reactive;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// End-to-end integration tests for reactive change tracking through the full
/// InProcessServer → Orleans → broadcast → replay pipeline.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ReactiveIntegrationTests
{
    private readonly TestClusterFixture _fixture;

    public ReactiveIntegrationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_UpdatesReactiveCounter()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        await api.AddReactiveAsync(42);

        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(42, state.ReactiveCounter);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_MultipleAdds_Accumulates()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        await api.AddReactiveAsync(10);
        await api.AddReactiveAsync(20);
        await api.AddReactiveAsync(30);

        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(60, state.ReactiveCounter);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_BroadcastToSecondClient()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var client1 = new TestClientSetup(server, "player1");
        await using var client2 = new TestClientSetup(server, "player2");
        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        // Client 1 calls AddReactive — client 2 should receive broadcast
        await api1.AddReactiveAsync(42);

        // Wait for broadcast to propagate
        await Task.Delay(200);

        var state1 = client1.CreateResolver().GetState<CounterState>(entityId);
        var state2 = client2.CreateResolver().GetState<CounterState>(entityId);

        Assert.Equal(42, state1.ReactiveCounter);
        Assert.Equal(42, state2.ReactiveCounter);
        Assert.Empty(client1.DetectedIssues);
        Assert.Empty(client2.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_BroadcastReplay_FiresOnReplayedEvent()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var client1 = new TestClientSetup(server, "player1");
        await using var client2 = new TestClientSetup(server, "player2");
        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        // Track broadcasts on client 2
        var broadcastValues = new List<int>();
        api2.OnAddReactive_Replayed += value => broadcastValues.Add(value);

        // Client 1 sends values
        await api1.AddReactiveAsync(10);
        await Task.Delay(200);
        await api1.AddReactiveAsync(20);
        await Task.Delay(200);

        Assert.Equal(2, broadcastValues.Count);
        Assert.Equal(10, broadcastValues[0]);
        Assert.Equal(20, broadcastValues[1]);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_AccumulatesWithoutReset()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        await api.AddReactiveAsync(100);
        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(100, state.ReactiveCounter);

        // Reset clears Operations and Sum, but not ReactiveCounter
        await api.ResetAsync();
        state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(100, state.ReactiveCounter); // unchanged by Reset
        Assert.Equal(0, state.Sum);
        Assert.Empty(state.Operations);

        // Add more to ReactiveCounter
        await api.AddReactiveAsync(50);
        state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(150, state.ReactiveCounter);

        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_MixedWithAddValue_BothWork()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Mix reactive and non-reactive methods
        await api.AddValueAsync(10, 1);
        await api.AddReactiveAsync(5);
        await api.AddValueAsync(20, 2);
        await api.AddReactiveAsync(15);

        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(30, state.Sum); // 10 + 20
        Assert.Equal(20, state.ReactiveCounter); // 5 + 15
        Assert.Equal(2, state.Operations.Count);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_LateJoiner_GetsCurrentState()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var client1 = new TestClientSetup(server, "player1");
        await client1.ConnectAsync();

        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        // Player1 makes changes before player2 joins
        await api1.AddReactiveAsync(100);
        await api1.AddReactiveAsync(50);

        // Player2 joins late
        await using var client2 = new TestClientSetup(server, "player2");
        await client2.ConnectAsync();
        var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        // Player2 should see the accumulated state
        var state2 = client2.CreateResolver().GetState<CounterState>(entityId);
        Assert.Equal(150, state2.ReactiveCounter);

        Assert.Empty(client1.DetectedIssues);
        Assert.Empty(client2.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_ConcurrentClients_StateConverges()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var client1 = new TestClientSetup(server, "player1");
        await using var client2 = new TestClientSetup(server, "player2");
        await using var client3 = new TestClientSetup(server, "player3");
        await client1.ConnectAsync();
        await client2.ConnectAsync();
        await client3.ConnectAsync();

        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var api3 = await client3.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        // Each client adds different values
        await api1.AddReactiveAsync(10);
        await api2.AddReactiveAsync(20);
        await api3.AddReactiveAsync(30);
        await api1.AddReactiveAsync(5);

        // Wait for all broadcasts to propagate
        await Task.Delay(500);

        // All clients should converge to the same value
        var state1 = client1.CreateResolver().GetState<CounterState>(entityId);
        var state2 = client2.CreateResolver().GetState<CounterState>(entityId);
        var state3 = client3.CreateResolver().GetState<CounterState>(entityId);

        Assert.Equal(65, state1.ReactiveCounter);
        Assert.Equal(65, state2.ReactiveCounter);
        Assert.Equal(65, state3.ReactiveCounter);

        Assert.Empty(client1.DetectedIssues);
        Assert.Empty(client2.DetectedIssues);
        Assert.Empty(client3.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ChangeTracker_ActiveDuringBroadcastReplay()
    {
        // Verify that OnStateMutated fires on client2 when client1's broadcast is replayed
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var client1 = new TestClientSetup(server, "player1");
        await using var client2 = new TestClientSetup(server, "player2");
        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        // Track OnStateMutated on client2 — fires after FlushAndNotify in DispatchServiceBroadcast
        bool stateMutated = false;
        api2.OnStateMutated += () => stateMutated = true;

        await api1.AddReactiveAsync(42);

        // Wait for broadcast
        await Task.Delay(200);

        Assert.True(stateMutated);
        var state2 = client2.CreateResolver().GetState<CounterState>(entityId);
        Assert.Equal(42, state2.ReactiveCounter);
    }

    [Fact(Timeout = 60_000)]
    public async Task AddReactive_ReactiveCounter_SerializesInSnapshot()
    {
        // Verify that reactive field survives serialization through the transport
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var client1 = new TestClientSetup(server, "player1");
        await client1.ConnectAsync();

        var entityId = $"reactive_{Guid.NewGuid():N}";
        var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await api1.AddReactiveAsync(77);

        // New client subscribes — receives full state snapshot via MemoryPack serialization
        await using var client2 = new TestClientSetup(server, "player2");
        await client2.ConnectAsync();
        var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        var state2 = client2.CreateResolver().GetState<CounterState>(entityId);
        Assert.Equal(77, state2.ReactiveCounter);
    }

    // ─── ServerPatch mode + reactive field tests ───

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_SingleClient_UpdatesViaPatch()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            await api.AddReactiveAsync(42);

            var state = resolver.GetState<CounterState>(entityId);
            Assert.Equal(42, state.ReactiveCounter);
            Assert.Empty(client.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_MultipleAdds_Accumulates()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            await api.AddReactiveAsync(10);
            await api.AddReactiveAsync(20);
            await api.AddReactiveAsync(30);

            var state = resolver.GetState<CounterState>(entityId);
            Assert.Equal(60, state.ReactiveCounter);
            Assert.Empty(client.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_BroadcastAppliesPatch()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var client1 = new TestClientSetup(server, "player1");
            await using var client2 = new TestClientSetup(server, "player2");
            await client1.ConnectAsync();
            await client2.ConnectAsync();

            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
            var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            await api1.AddReactiveAsync(42);
            await Task.Delay(200);

            var state1 = client1.CreateResolver().GetState<CounterState>(entityId);
            var state2 = client2.CreateResolver().GetState<CounterState>(entityId);

            Assert.Equal(42, state1.ReactiveCounter);
            Assert.Equal(42, state2.ReactiveCounter);
            Assert.Empty(client1.DetectedIssues);
            Assert.Empty(client2.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_OnStateMutated_FiresOnBroadcastReceiver()
    {
        // OnStateMutated fires on the BROADCAST RECEIVER, not the caller.
        // Verify it fires on client2 for each patch broadcast from client1.
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var client1 = new TestClientSetup(server, "player1");
            await using var client2 = new TestClientSetup(server, "player2");
            await client1.ConnectAsync();
            await client2.ConnectAsync();

            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
            var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            int mutatedCount = 0;
            api2.OnStateMutated += () => mutatedCount++;

            await api1.AddReactiveAsync(10);
            await Task.Delay(200);
            await api1.AddReactiveAsync(20);
            await Task.Delay(200);

            Assert.Equal(2, mutatedCount);

            var state2 = client2.CreateResolver().GetState<CounterState>(entityId);
            Assert.Equal(30, state2.ReactiveCounter);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_OnReplayed_Fires()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var client1 = new TestClientSetup(server, "player1");
            await using var client2 = new TestClientSetup(server, "player2");
            await client1.ConnectAsync();
            await client2.ConnectAsync();

            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
            var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            var broadcastValues = new List<int>();
            api2.OnAddReactive_Replayed += value => broadcastValues.Add(value);

            await api1.AddReactiveAsync(10);
            await Task.Delay(200);
            await api1.AddReactiveAsync(20);
            await Task.Delay(200);

            // OnAddReactive_Replayed fires even in patch mode
            Assert.Equal(2, broadcastValues.Count);
            Assert.Equal(10, broadcastValues[0]);
            Assert.Equal(20, broadcastValues[1]);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_MixedModes_ReactiveAndRegular()
    {
        // AddReactive in ServerPatch, AddValue in Server mode
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            await api.AddValueAsync(10, 1);         // Server mode
            await api.AddReactiveAsync(5);           // ServerPatch mode
            await api.AddValueAsync(20, 2);         // Server mode
            await api.AddReactiveAsync(15);          // ServerPatch mode

            var state = resolver.GetState<CounterState>(entityId);
            Assert.Equal(30, state.Sum);
            Assert.Equal(20, state.ReactiveCounter);
            Assert.Empty(client.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_BroadcastPatch_OnStateMutated_Fires()
    {
        // Verify OnStateMutated fires on client2 when patch broadcast is applied
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var client1 = new TestClientSetup(server, "player1");
            await using var client2 = new TestClientSetup(server, "player2");
            await client1.ConnectAsync();
            await client2.ConnectAsync();

            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
            var api2 = await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            bool stateMutated = false;
            api2.OnStateMutated += () => stateMutated = true;

            await api1.AddReactiveAsync(42);
            await Task.Delay(200);

            Assert.True(stateMutated);
            var state2 = client2.CreateResolver().GetState<CounterState>(entityId);
            Assert.Equal(42, state2.ReactiveCounter);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_PatchTriggersChangeTreeNotification()
    {
        // Verify that applying a patch with reactive field fires ChangeTreeArgs notification
        // on the calling client (synchronous notification from PatchApplier → generated setter → tracker)
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var client1 = new TestClientSetup(server, "player1");
            await client1.ConnectAsync();

            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            // Subscribe to ChangeTreeArgs for CounterState's TrackedTypeId
            // Must capture values inside handler — Nodes list is pooled after FlushAndNotify
            var fieldId = (int)TrackingProperty.CounterState_ReactiveCounter;
            var captured = new List<(object Root, bool HasField, int OldValue, int NewValue)>();
            Action<ChangeTreeArgs> handler = args =>
            {
                var leaf = args.FindLeaf(fieldId);
                captured.Add((args.Root, args.HasChange(fieldId),
                    leaf?.OldValue.IntValue ?? -1,
                    leaf?.NewValue.IntValue ?? -1));
            };
            ChangeTracker.Subscribe(CounterState.TrackedTypeId, handler);
            try
            {
                await api1.AddReactiveAsync(42);

                // Client's own ServerPatch replay fires synchronously
                Assert.Single(captured);
                Assert.IsType<CounterState>(captured[0].Root);
                Assert.True(captured[0].HasField);
                Assert.Equal(0, captured[0].OldValue);
                Assert.Equal(42, captured[0].NewValue);

                // Second change
                await api1.AddReactiveAsync(8);

                Assert.Equal(2, captured.Count);
                Assert.Equal(42, captured[1].OldValue);   // was 42
                Assert.Equal(50, captured[1].NewValue);    // now 50
            }
            finally
            {
                ChangeTracker.Unsubscribe(CounterState.TrackedTypeId, handler);
            }
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatch_AddReactive_LateJoiner_GetsCurrentState()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterService", "AddReactive", ExecutionMode.ServerPatch);
        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var client1 = new TestClientSetup(server, "player1");
            await client1.ConnectAsync();

            var entityId = $"sp_reactive_{Guid.NewGuid():N}";
            var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            await api1.AddReactiveAsync(100);
            await api1.AddReactiveAsync(50);

            // Late joiner should see accumulated state
            await using var client2 = new TestClientSetup(server, "player2");
            await client2.ConnectAsync();
            await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            var state2 = client2.CreateResolver().GetState<CounterState>(entityId);
            Assert.Equal(150, state2.ReactiveCounter);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    // ─── Reactive notification mechanism tests ───

    [Fact(Timeout = 60_000)]
    public async Task Notification_LocalCall_FiresChangeTreeArgs()
    {
        // When a client calls AddReactive locally (Server mode: replay on client after server confirms),
        // the generated property setter records changes → FlushAndNotify → subscriber fires
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_notif_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var notifications = new List<(int OldValue, int NewValue)>();
        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += args =>
        {
            var leaf = args.FindLeaf((int)TrackingProperty.CounterState_ReactiveCounter);
            if (leaf != null)
                notifications.Add((leaf.Value.OldValue.IntValue, leaf.Value.NewValue.IntValue));
        };

        try
        {
            await api.AddReactiveAsync(10);
            await api.AddReactiveAsync(25);

            // Each call should produce a notification with correct old/new values
            Assert.Equal(2, notifications.Count);
            Assert.Equal(0, notifications[0].OldValue);
            Assert.Equal(10, notifications[0].NewValue);
            Assert.Equal(10, notifications[1].OldValue);
            Assert.Equal(35, notifications[1].NewValue);
        }
        finally
        {
            TrackedCounterState.Unregister();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Notification_BroadcastReplay_FiresOnReceiver()
    {
        // When client2 receives a broadcast from client1 and replays it,
        // the generated property setter fires change notifications on client2
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var client1 = new TestClientSetup(server, "player1");
        await using var client2 = new TestClientSetup(server, "player2");
        await client1.ConnectAsync();
        await client2.ConnectAsync();

        var entityId = $"reactive_notif_{Guid.NewGuid():N}";
        var api1 = await client1.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await client2.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        var client2Notifications = new List<(int OldValue, int NewValue)>();
        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += args =>
        {
            var leaf = args.FindLeaf((int)TrackingProperty.CounterState_ReactiveCounter);
            if (leaf != null)
                client2Notifications.Add((leaf.Value.OldValue.IntValue, leaf.Value.NewValue.IntValue));
        };

        try
        {
            await api1.AddReactiveAsync(42);
            await Task.Delay(300);

            // client2 should receive broadcast → replay → notification fires
            Assert.True(client2Notifications.Count >= 1, $"Expected at least 1 notification on client2, got {client2Notifications.Count}");
            // One of the notifications should be 0→42
            Assert.Contains(client2Notifications, n => n.OldValue == 0 && n.NewValue == 42);
        }
        finally
        {
            TrackedCounterState.Unregister();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Notification_NoChange_DoesNotFire()
    {
        // Setting the same value should NOT trigger a notification
        // (EqualityComparer check in generated setter)
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_notif_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        int notificationCount = 0;
        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += _ => notificationCount++;

        try
        {
            // AddReactive(0) adds 0 to ReactiveCounter (0 + 0 = 0), so value doesn't actually change
            await api.AddReactiveAsync(0);

            // The += 0 should result in same value, so no notification
            Assert.Equal(0, notificationCount);
        }
        finally
        {
            TrackedCounterState.Unregister();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Notification_HasChange_CorrectFieldId()
    {
        // Verify HasChange returns true for the correct field and false for others
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_notif_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        bool hasReactiveCounter = false;
        bool hasNonExistentField = true;

        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += args =>
        {
            hasReactiveCounter = args.HasChange((int)TrackingProperty.CounterState_ReactiveCounter);
            hasNonExistentField = args.HasChange(99999); // non-existent field
        };

        try
        {
            await api.AddReactiveAsync(5);

            Assert.True(hasReactiveCounter);
            Assert.False(hasNonExistentField);
        }
        finally
        {
            TrackedCounterState.Unregister();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Notification_RootObjectReference_IsCorrectStateInstance()
    {
        // The Root in ChangeTreeArgs should be the actual CounterState instance
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_notif_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        object? capturedRoot = null;
        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += args => capturedRoot = args.Root;

        try
        {
            await api.AddReactiveAsync(1);

            Assert.NotNull(capturedRoot);
            Assert.IsType<CounterState>(capturedRoot);

            // Should be the same instance as what GetState returns
            var state = resolver.GetState<CounterState>(entityId);
            Assert.Same(state, capturedRoot);
        }
        finally
        {
            TrackedCounterState.Unregister();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Notification_MultipleCallsInSequence_EachFiresSeparately()
    {
        // Each method call should produce its own notification batch
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"reactive_notif_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var values = new List<int>();
        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += args =>
        {
            var leaf = args.FindLeaf((int)TrackingProperty.CounterState_ReactiveCounter);
            if (leaf != null)
                values.Add(leaf.Value.NewValue.IntValue);
        };

        try
        {
            await api.AddReactiveAsync(1);
            await api.AddReactiveAsync(2);
            await api.AddReactiveAsync(3);
            await api.AddReactiveAsync(4);
            await api.AddReactiveAsync(5);

            // 5 calls → 5 notifications with accumulating values: 1, 3, 6, 10, 15
            Assert.Equal(5, values.Count);
            Assert.Equal(1, values[0]);
            Assert.Equal(3, values[1]);
            Assert.Equal(6, values[2]);
            Assert.Equal(10, values[3]);
            Assert.Equal(15, values[4]);
        }
        finally
        {
            TrackedCounterState.Unregister();
        }
    }
}
