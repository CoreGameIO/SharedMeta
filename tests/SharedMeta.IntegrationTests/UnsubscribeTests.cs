using Orleans;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Session;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Disconnecting from an entity used to stay client-side: the transports sent no state type, the
/// server's (entityId, stateType) lookup missed and answered success, and the resolver never called
/// the dispatcher at all. The entity kept the player subscribed and the session reclaimed it on
/// every Resume.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class UnsubscribeTests
{
    private readonly TestClusterFixture _fixture;

    public UnsubscribeTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task Disconnect_EndsServerSideSubscription()
    {
        var entityId = $"unsub_{Guid.NewGuid():N}";
        var aliceId = $"alice-{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, aliceId);
        await alice.ConnectAsync();
        var resolver = alice.CreateResolver();
        await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        await resolver.DisconnectAsync<CounterState>(entityId);

        Assert.False(alice.MetaClient.Dispatcher.IsSubscribed(entityId));
        // The session no longer holds it: a call routed through it is refused.
        var session = _fixture.GrainFactory.GetGrain<ISessionManager>(aliceId);
        // requestId 0 bypasses request ordering, which would otherwise stash an out-of-sequence id.
        var response = await session.SendToEntityAsync(entityId, typeof(CounterState).FullName!, 0,
            new RpcCall { MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Ping_v0, CallerId = aliceId, CallerClientVersion = "1.0.0" },
            0, ((SharedMeta.Client.ClientDispatcher)alice.MetaClient.Dispatcher).SessionId);
        Assert.True(response.HasError);
        Assert.Contains("Not subscribed", response.Error);
    }

    [Fact(Timeout = 30_000)]
    public async Task Disconnect_OneStateType_SiblingOnSameEntityIdKeepsReceiving()
    {
        var entityId = $"unsub_dual_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        var aliceResolver = alice.CreateResolver();
        await aliceResolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        var aliceDesync = await aliceResolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);
        var bobDesync = await bob.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        await aliceResolver.DisconnectAsync<CounterState>(entityId);

        await bobDesync.AddAsync(5);
        await WaitUntilAsync(() => aliceDesync.State.Value == 5);
        Assert.True(alice.MetaClient.Dispatcher.IsSubscribed(entityId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached in 5s.");
            await Task.Delay(20);
        }
    }
}
