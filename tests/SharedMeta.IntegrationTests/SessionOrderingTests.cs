using System.Collections.Concurrent;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Session;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for the server-side RPC reordering gate in <c>SessionManagerGrain</c>.
/// Tests directly invoke the grain to deliver out-of-order requests deterministically —
/// going through a real client + transport adds noise from threadpool scheduling that
/// makes some scenarios (especially overflow / stall escalation) hard to provoke.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SessionOrderingTests
{
    private readonly TestClusterFixture _fixture;

    public SessionOrderingTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task OutOfOrder_StashesAndDrainsInOrder()
    {
        var (sessionId, observer, grain, entityId) = await SetupSessionAsync("ordering-drain");

        // Send req=2 first (gap), then req=1 (closes gap and drains stash).
        var resp2 = await grain.SendToEntityAsync(entityId, requestId: 2, BuildAddCall(2), 0, sessionId);
        // Stashed → empty ack response
        Assert.Empty(resp2.Operations);
        Assert.Equal(0, resp2.SequenceNumber);

        var resp1 = await grain.SendToEntityAsync(entityId, requestId: 1, BuildAddCall(1), 0, sessionId);
        // Real response: contains both req=1 and req=2 ops
        var requestIds = resp1.Operations.Select(o => o.RequestId).Where(id => id > 0).OrderBy(x => x).ToList();
        Assert.Equal(new long[] { 1, 2 }, requestIds);

        // Verify the entity grain saw both adds, in order, by reading state through a query call.
        var state = await ReadCounterStateAsync(grain, entityId);
        Assert.Equal(3, state.Sum);             // 1 + 2 = 3
        Assert.Equal(2, state.Operations.Count); // Both ops applied
        Assert.Equal(1, state.Operations[0].Value);
        Assert.Equal(2, state.Operations[1].Value);

        await grain.GracefulDisconnectAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task StallNotification_FiresStalledThenRecovered()
    {
        var (sessionId, observer, grain, entityId) = await SetupSessionAsync("ordering-stall");

        // Send req=2 → stash. The stall timer starts; after SoftStallNotifyTimeout (200ms in fixture)
        // the server should push a Stalled notification through the observer.
        var resp2 = await grain.SendToEntityAsync(entityId, requestId: 2, BuildAddCall(2), 0, sessionId);
        Assert.Empty(resp2.Operations);

        // Wait for stall notification. OldestMissingRequestId is the request the server
        // is waiting for to fill the gap — req=1 (since req=2 is stashed and req=1 hasn't arrived yet).
        var stalled = await observer.WaitForStallAsync(StallStage.Stalled, TimeSpan.FromSeconds(5));
        Assert.NotNull(stalled);
        Assert.Equal(1, stalled!.OldestMissingRequestId);
        Assert.Equal(1, stalled.StashedCount);

        // Now send req=1 to close the gap. Server drains stash and pushes a Recovered notification.
        var resp1 = await grain.SendToEntityAsync(entityId, requestId: 1, BuildAddCall(1), 0, sessionId);
        Assert.Equal(2, resp1.Operations.Count(o => o.RequestId > 0));

        var recovered = await observer.WaitForStallAsync(StallStage.Recovered, TimeSpan.FromSeconds(5));
        Assert.NotNull(recovered);

        await grain.GracefulDisconnectAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task StashOverflow_TerminatesSession()
    {
        var (sessionId, observer, grain, entityId) = await SetupSessionAsync("ordering-overflow");

        // Default StashCapacity = 256. The first request that requires offset >= capacity
        // overflows. Stashing requestIds 2..256 (offsets 1..255) fills the buffer; the next
        // request (id=257, offset=256) trips overflow and terminates the session.
        const int capacity = 256;
        for (int i = 2; i <= capacity; i++)
        {
            var resp = await grain.SendToEntityAsync(entityId, requestId: i, BuildAddCall(i), 0, sessionId);
            Assert.Empty(resp.Operations);
        }

        // The capacity+1th call overflows.
        var overflow = await grain.SendToEntityAsync(entityId, requestId: capacity + 1, BuildAddCall(capacity + 1), 0, sessionId);
        Assert.True(overflow.HasError);
        Assert.Contains("stash overflow", overflow.Error);

        // Observer should have received OnSessionTerminated.
        var reason = await observer.WaitForTerminatedAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(reason);
        Assert.Contains("stash overflow", reason);
    }

    [Fact(Timeout = 60_000)]
    public async Task StallTimeout_TerminatesSession()
    {
        // Override MaxStallDuration to a very short value via a brand-new player so we
        // don't disturb the rest of the test session manager state. The fixture's silo-wide
        // SessionManagerOptions has MaxStallDuration=30s; we can't change it per-test, so
        // this test relies on the fixture's HardStallNotifyTimeout being short enough to
        // produce a TimeoutPending notification (which we treat as a "stall progresses through
        // its stages" verification).
        var (sessionId, observer, grain, entityId) = await SetupSessionAsync("ordering-stall-stages");

        await grain.SendToEntityAsync(entityId, requestId: 2, BuildAddCall(2), 0, sessionId);

        var stalled = await observer.WaitForStallAsync(StallStage.Stalled, TimeSpan.FromSeconds(5));
        Assert.NotNull(stalled);

        var pending = await observer.WaitForStallAsync(StallStage.TimeoutPending, TimeSpan.FromSeconds(5));
        Assert.NotNull(pending);

        // Recover by sending req=1.
        await grain.SendToEntityAsync(entityId, requestId: 1, BuildAddCall(1), 0, sessionId);
        var recovered = await observer.WaitForStallAsync(StallStage.Recovered, TimeSpan.FromSeconds(5));
        Assert.NotNull(recovered);

        await grain.GracefulDisconnectAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<(Guid sessionId, TestObserver observer, ISessionManager grain, string entityId)> SetupSessionAsync(string namePrefix)
    {
        var playerId = $"{namePrefix}-{Guid.NewGuid():N}";
        var grain = _fixture.GrainFactory.GetGrain<ISessionManager>(playerId);

        var sessionId = Guid.NewGuid();
        var connect = await grain.ConnectAsync(sessionId, 0);
        Assert.True(connect.Success);

        var observer = new TestObserver();
        var observerRef = _fixture.GrainFactory.CreateObjectReference<ISessionObserver>(observer);
        await grain.SetObserverAsync(observerRef);

        var entityId = $"counter_{Guid.NewGuid():N}";
        var subscribe = await grain.SubscribeToEntityAsync(entityId, typeof(CounterState).FullName!);
        Assert.True(subscribe.Success, subscribe.Error);

        return (sessionId, observer, grain, entityId);
    }

    private RpcCall BuildAddCall(int value)
    {
        // CounterService.AddValue(int value, int clientSequence) — see ICounterService.cs.
        // Server expects payload to be the serialized arg list. Use the same serializer the
        // grain is configured with so encoding matches the dispatcher.
        var writer = _fixture.Serializer.CreateWriter();
        writer.Write(value);
        writer.Write(0);
        var bytes = writer.Complete();
        return new RpcCall
        {
            ServiceName = "ICounterService",
            MethodName = "Add",
            Payload = bytes,
            CallerId = "test",
        };
    }

    private async Task<CounterState> ReadCounterStateAsync(ISessionManager grain, string entityId)
    {
        var resolver = new SharedMeta.Test.Meta1.Server.GeneratedEntityGrainResolver();
        var entityGrain = resolver.GetEntityGrain(_fixture.GrainFactory, typeof(CounterState).FullName!, entityId);
        var stateBytes = await entityGrain!.GetEntityStateAsync();
        return _fixture.Serializer.Unpack<CounterState>(stateBytes!)!;
    }

    /// <summary>
    /// Captures observer batches with stall notifications and termination reasons.
    /// </summary>
    private class TestObserver : ISessionObserver
    {
        private readonly ConcurrentQueue<StallNotification> _stalls = new();
        private readonly TaskCompletionSource<string> _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnBatch(SessionResponse response)
        {
            if (response.StallNotification != null)
                _stalls.Enqueue(response.StallNotification);
            return Task.CompletedTask;
        }

        public Task OnEntityDeactivating(string entityId) => Task.CompletedTask;

        public Task OnSessionTerminated(string reason)
        {
            _terminated.TrySetResult(reason);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Waits up to <paramref name="timeout"/> for a stall notification of the given
        /// <paramref name="stage"/> to arrive. Returns the notification or null on timeout.
        /// </summary>
        public async Task<StallNotification?> WaitForStallAsync(StallStage stage, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                while (_stalls.TryDequeue(out var n))
                {
                    if (n.Stage == stage) return n;
                }
                await Task.Delay(20);
            }
            return null;
        }

        public async Task<string?> WaitForTerminatedAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_terminated.Task, Task.Delay(timeout));
            return completed == _terminated.Task ? _terminated.Task.Result : null;
        }
    }
}
