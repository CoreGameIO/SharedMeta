using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Round-trip coverage for argument transformers. The service echoes back what the dispatcher
/// handed the method body, so each test can tell apart three outcomes that used to look alike:
/// the transformer ran (Origin == "unboxed"), the raw value crossed the wire (Origin == ""),
/// or the payload was misframed (wrong coordinates / wrong trailing tag / throw).
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ArgumentTransformerTests
{
    private readonly TestClusterFixture _fixture;

    public ArgumentTransformerTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(TestClientSetup Client, TransformServiceApiClient Api, string EntityId)> ConnectAsync()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var client = new TestClientSetup(server);
        await client.ConnectAsync();
        var entityId = $"transform_{Guid.NewGuid():N}";
        var api = await client.CreateResolver().GetServiceAsync<TransformServiceApiClient>(entityId);
        return (client, api, entityId);
    }

    /// <summary>
    /// Explicit <c>[Transform(typeof(CoordTransformer))]</c>: the transformer must run on both
    /// sides, and the trailing <c>tag</c> must survive the boxed argument that precedes it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ExplicitTransform_RoundTrips()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        var result = await api.MoveExplicitAsync(new Coord { X = 3, Y = 7 }, 42);

        Assert.Equal("3:7:unboxed:42", result);

        var state = client.CreateResolver().GetState<TransformState>(entityId);
        Assert.Equal(3, state.LastX);
        Assert.Equal(7, state.LastY);
        Assert.Equal("unboxed", state.LastOrigin);
        Assert.Equal(42, state.LastTag);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// No parameter attribute: <see cref="CoordTransformer"/> is discovered from the compilation
    /// and applies anyway. Origin == "" here would mean the transformer silently never ran.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AutoDiscoveredTransform_RoundTrips()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        var result = await api.MoveAutoAsync(new Coord { X = 11, Y = 22 }, 7);

        Assert.Equal("11:22:unboxed:7", result);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// <c>[SkipTransform]</c> means "no transformation" — the raw <c>Coord</c> crosses the wire
    /// intact, Origin stays "", and the framing is unchanged from any other plain argument.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task SkipTransform_SendsRawValue()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        var result = await api.MoveSkipAsync(new Coord { X = 5, Y = 9, Origin = "raw" }, 99);

        Assert.Equal("5:9:raw:99", result);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// A transformed argument between two plain ones — the boxed member must occupy exactly one
    /// slot so both neighbours land where the reader expects them.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MixedArguments_KeepPositions()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        var result = await api.MoveMixedAsync(-1, new Coord { X = 12, Y = 34 }, 56);

        Assert.Equal("-1|12:34:unboxed:56", result);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// A state-aware transformer sends only the id and rebuilds the token from the receiver's own
    /// state. "missing" would mean the lookup ran against a state that never saw the token.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StateAwareTransform_ResolvesAgainstReceiverState()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        await api.AddTokenAsync(7, "crown");

        // Deliberately a different instance carrying a wrong label: only the id is transmitted, so
        // both sides must end up with the token their own state holds.
        var result = await api.TouchTokenAsync(new Token { Id = 7, Label = "stale" }, 3);

        Assert.Equal("7:crown:3", result);
        Assert.Equal("crown", client.CreateResolver().GetState<TransformState>(entityId).LastLabel);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// A second subscriber replays the originating client's argument payload verbatim, so its
    /// broadcast handler has to unbox exactly the way the server dispatcher did.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Broadcast_UnboxesForOtherSubscribers()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var caller = new TestClientSetup(server, "alice");
        await using var observer = new TestClientSetup(server, "bob");
        await caller.ConnectAsync();
        await observer.ConnectAsync();

        var entityId = $"transform_bcast_{Guid.NewGuid():N}";
        var callerResolver = caller.CreateResolver();
        var observerResolver = observer.CreateResolver();
        await callerResolver.GetServiceAsync<TransformServiceApiClient>(entityId);
        await observerResolver.GetServiceAsync<TransformServiceApiClient>(entityId);

        var callerApi = await callerResolver.GetServiceAsync<TransformServiceApiClient>(entityId);
        await callerApi.MoveExplicitAsync(new Coord { X = 8, Y = 6 }, 21);

        await WaitForAsync(() => observerResolver.GetState<TransformState>(entityId).Calls == 1);

        var observed = observerResolver.GetState<TransformState>(entityId);
        Assert.Equal(8, observed.LastX);
        Assert.Equal(6, observed.LastY);
        Assert.Equal("unboxed", observed.LastOrigin);
        Assert.Equal(21, observed.LastTag);
        Assert.Empty(observer.DetectedIssues);
    }

    /// <summary>
    /// Queries reach the same dispatcher as any RPC, so their arguments must be framed and boxed
    /// the same way — a query proxy holds no subscription, but the wire contract is identical.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Query_WithArguments_RoundTrips()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();
        var entityId = $"transform_query_{Guid.NewGuid():N}";
        await client.CreateResolver().GetServiceAsync<TransformServiceApiClient>(entityId);

        var query = new TransformServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

        Assert.Equal("100:200", await query.PeekPlainAsync(100, 200));
        Assert.Equal("4:5:unboxed:6", await query.PeekCoordAsync(new Coord { X = 4, Y = 5 }, 6));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "Condition was not met before the timeout.");
    }
}
