using SharedMeta.Core.Framework;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Addressing a meta service through a <c>[MetaServiceContract]</c> it inherits — how framework
/// code reaches a player's entity without being able to name the game's service type.
/// </summary>
/// <remarks>
/// Worth its own file because the contract is declared in another assembly, which is the whole
/// reason the mechanism exists: the mirror interface is generated beside the contract, the
/// <c>[MetaMethod]</c>s sit on the implementation, and the resolver binds the two. Nothing else
/// in the suite exercises that chain, and every link has to agree on a method neither side can
/// see declared locally.
/// </remarks>
[Collection(TestClusterCollection.Name)]
public class LobbyNotificationTests
{
    private readonly TestClusterFixture _fixture;

    public LobbyNotificationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The call shape a framework grain uses: one proxy for every entity, named by contract
    /// rather than by service. In a silo this arrives by injection.
    /// </summary>
    private ILobbyListenerServerApi Players(TestClientSetup client) =>
        new SharedMeta.Test.Meta1.Server.GeneratedLobbyListenerServerApi(
            new MetaServerApiFactory(_fixture.GrainFactory, client.Serializer));

    /// <summary>
    /// The end-to-end claim: a contract call applies on the server before the await returns, and
    /// the subscribed client converges by replaying the method body.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ContractCall_AppliesOnServer_AndReplaysOnClient()
    {
        var entityId = $"lobby_found_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);

        await Players(client).OnMatchFoundAsync(entityId, new MatchFoundEvent
        {
            MatchId = "match-42",
            GameMode = "duel",
            PlayerIds = { "alice", "bob" },
            PlayerSlot = 1,
        });

        // The call is awaited, so the server has applied by now; only the broadcast is in flight.
        await WaitFor(() => counter.State.LobbyNotifications == 1);

        Assert.Equal("match-42", counter.State.LastMatchId);
        Assert.Equal(5, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// The reason this is awaited rather than fire-and-forget: a target-side failure has to reach
    /// the caller, so a framework grain can log or retry instead of silently dropping a match.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ContractCall_SurfacesTargetSideFailure()
    {
        var entityId = $"lobby_fail_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);

        // Empty MatchId makes the service body throw.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Players(client).OnMatchFoundAsync(entityId, new MatchFoundEvent { MatchId = "" }));

        Assert.Contains("MatchId is required", ex.Message);
        Assert.Equal(0, counter.State.LobbyNotifications);
    }

    /// <summary>
    /// Two contract methods in a row must each land on their own. A shared method id or a
    /// mis-scoped dispatcher case would show up here as one applying twice.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ContractCalls_OfDifferentKinds_EachDispatchToItsOwnMethod()
    {
        var entityId = $"lobby_seq_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(1, 1);

        var players = Players(client);
        await players.OnMatchFoundAsync(entityId, new MatchFoundEvent { MatchId = "m1" });
        await WaitFor(() => counter.State.LobbyNotifications == 1);
        Assert.Equal("m1", counter.State.LastMatchId);

        await players.OnMatchCancelledAsync(entityId, new MatchCancelledEvent
        {
            Reason = MatchCancelReason.PlayerCancelled,
        });
        await WaitFor(() => counter.State.LobbyNotifications == 2);

        // Cancel clears the match — proves the second broadcast replayed the cancel body and not
        // the found body a second time.
        Assert.Null(counter.State.LastMatchId);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// The lobby fires at a player who is offline. The entity must still apply and persist, so
    /// the outcome is there when they next subscribe.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ContractCall_OnEntityWithNoSubscriber_IsVisibleOnLaterSubscribe()
    {
        var entityId = $"lobby_cold_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();

        await Players(client).OnMatchFoundAsync(entityId, new MatchFoundEvent { MatchId = "match-cold" });

        var resolver = client.CreateResolver();
        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        Assert.Equal(1, counter.State.LobbyNotifications);
        Assert.Equal("match-cold", counter.State.LastMatchId);
    }

    /// <summary>
    /// Contract methods are server-originated only — a client-callable lobby callback would let
    /// any player hand themselves a match.
    /// </summary>
    [Fact]
    public void ContractMethods_HaveNoClientApi()
    {
        var callable = typeof(CounterServiceApiClient)
            .GetMethods()
            .Select(m => m.Name)
            .Where(n => n.StartsWith("OnMatch"))
            .ToList();

        Assert.Empty(callable);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail("Condition not met within timeout.");
    }
}
