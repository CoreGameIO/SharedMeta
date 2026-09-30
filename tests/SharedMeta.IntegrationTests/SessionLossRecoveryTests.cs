using Orleans;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Session;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The default session-loss recovery (<c>SessionRecoveryAction.Reconnect</c>) starts a new session
/// and re-subscribes every known entity. It used to discard the snapshot each re-subscribe
/// returned, so the client kept running on whatever it had locally — including anything written
/// on the server while it was away.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SessionLossRecoveryTests
{
    private readonly TestClusterFixture _fixture;

    public SessionLossRecoveryTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task DefaultReconnect_InstallsServerStateFromResubscribe()
    {
        var entityId = $"session_loss_{Guid.NewGuid():N}";
        var aliceId = $"alice-{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        await using var alice = new TestClientSetup(server, aliceId);
        await alice.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await aliceApi.AddValueAsync(4, 1);
        Assert.Equal(4, aliceApi.State.Sum);

        // Server loses alice's session: the grain drops it and unsubscribes her from the entity,
        // so nothing written from here on reaches her as a broadcast.
        await _fixture.GrainFactory.GetGrain<ISessionManager>(aliceId).GracefulDisconnectAsync(Guid.Empty);

        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await bob.ConnectAsync();
        var bobApi = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await bobApi.AddValueAsync(10, 1);
        Assert.Equal(14, bobApi.State.Sum);

        // Resume → SessionUnknown → default handler → StartNew + re-subscribe.
        await alice.MetaClient.ResumeSessionAsync();

        Assert.True(alice.MetaClient.Dispatcher.IsSessionConnected);
        Assert.Equal(14, aliceApi.State.Sum);
    }
}
