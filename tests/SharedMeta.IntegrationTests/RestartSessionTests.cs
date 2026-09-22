using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// <c>MetaClient.RestartSessionAsync</c> is the documented way back after a supersede:
/// "clears all state, reconnects session — re-subscribe to entities as if connecting from
/// scratch".
///
/// It could not succeed against a healthy server. It passed a freshly-minted
/// <c>Guid.NewGuid()</c> with no explicit mode; the dispatcher's default resolves any non-empty
/// id to <c>Resume</c>, and the server answers <c>SessionUnknown</c> for an id it never issued —
/// so the method always threw "Failed to restart session". Nothing covered it.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class RestartSessionTests
{
    private readonly TestClusterFixture _fixture;

    public RestartSessionTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task RestartSession_SucceedsAgainstHealthyServer()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();

        await client.MetaClient.RestartSessionAsync();

        Assert.True(client.MetaClient.Dispatcher.IsSessionConnected);
    }

    /// <summary>
    /// The documented purpose end to end: after a restart the client re-acquires services and
    /// keeps working. Asserting only that the handshake returned would pass even if the session
    /// were left unusable.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AfterRestart_ClientCanResubscribeAndCall()
    {
        var entityId = $"restart_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();

        var before = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await before.AddValueAsync(4, 1);
        Assert.Equal(4, before.State.Sum);

        await client.MetaClient.RestartSessionAsync();

        // RestartSessionAsync clears every connection, so this is a genuinely fresh subscribe.
        // The server-side entity survived the session, so its accumulated state comes back.
        var after = await client.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        Assert.Equal(4, after.State.Sum);

        await after.AddValueAsync(6, 1);
        Assert.Equal(10, after.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }
}
