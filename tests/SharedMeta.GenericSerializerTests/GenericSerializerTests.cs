using SharedMeta.Debug.InProcess;
using SharedMeta.Test.Meta2.Generic;
using SharedMeta.Test.Meta2.Generic.Client;
using SharedMeta.Test.Meta2.Generic.Server;
using Xunit;

namespace SharedMeta.GenericSerializerTests;

/// <summary>
/// End-to-end coverage for the generic codegen branch — length-prefixed
/// <c>IPayloadWriter</c>/<c>IPayloadReader</c> framing instead of MemoryPack's positional layout.
/// <c>SharedMeta.IntegrationTests</c> can never reach this branch: its meta assembly references
/// MemoryPack and so resolves to the other one.
/// </summary>
[Collection(GenericClusterCollection.Name)]
public class GenericSerializerTests
{
    private readonly GenericClusterFixture _fixture;

    public GenericSerializerTests(GenericClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(GenericTestClient Client, GenericServiceApiClient Api, string EntityId)> ConnectAsync(
        string? playerId = null)
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var client = new GenericTestClient(server, playerId);
        await client.ConnectAsync();
        var entityId = $"generic_{Guid.NewGuid():N}";
        var api = await client.Resolver.GetServiceAsync<GenericServiceApiClient>(entityId);
        return (client, api, entityId);
    }

    /// <summary>Baseline: plain arguments survive the generic wire in Server mode.</summary>
    [Fact(Timeout = 60_000)]
    public async Task PlainArguments_RoundTrip()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        Assert.Equal(10, await api.AddAsync(10, 1));
        Assert.Equal(35, await api.AddAsync(25, 2));

        var state = client.Resolver.GetState<GenericState>(entityId);
        Assert.Equal(35, state.Sum);
        Assert.Equal(2, state.LastTag);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>Optimistic mode runs the body locally first, then ships the same arguments.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Optimistic_PlainArguments_RoundTrip()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        Assert.Equal(7, await api.AddOptimisticAsync(7, 3));

        Assert.Equal(7, client.Resolver.GetState<GenericState>(entityId).Sum);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task ExplicitTransform_RoundTrips()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        Assert.Equal("3:7:unboxed:42", await api.MoveExplicitAsync(new Point { X = 3, Y = 7 }, 42));

        var state = client.Resolver.GetState<GenericState>(entityId);
        Assert.Equal(3, state.LastX);
        Assert.Equal(7, state.LastY);
        Assert.Equal("unboxed", state.LastOrigin);
        Assert.Equal(42, state.LastTag);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task AutoDiscoveredTransform_RoundTrips()
    {
        var (client, api, _) = await ConnectAsync();
        await using var __ = client;

        Assert.Equal("11:22:unboxed:7", await api.MoveAutoAsync(new Point { X = 11, Y = 22 }, 7));
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task SkipTransform_SendsRawValue()
    {
        var (client, api, _) = await ConnectAsync();
        await using var __ = client;

        Assert.Equal("5:9:raw:99", await api.MoveSkipAsync(new Point { X = 5, Y = 9, Origin = "raw" }, 99));
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task MixedArguments_KeepPositions()
    {
        var (client, api, _) = await ConnectAsync();
        await using var __ = client;

        Assert.Equal("-1|12:34:unboxed:56", await api.MoveMixedAsync(-1, new Point { X = 12, Y = 34 }, 56));
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task StateAwareTransform_ResolvesAgainstReceiverState()
    {
        var (client, api, entityId) = await ConnectAsync();
        await using var _ = client;

        await api.AddMarkerAsync(7, "crown");

        // Wrong label on purpose: only the id travels, so each side must use its own state's copy.
        Assert.Equal("7:crown:3", await api.TouchMarkerAsync(new Marker { Id = 7, Label = "stale" }, 3));
        Assert.Equal("crown", client.Resolver.GetState<GenericState>(entityId).LastLabel);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>A second subscriber replays the caller's payload and must unbox it the same way.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Broadcast_UnboxesForOtherSubscribers()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var caller = new GenericTestClient(server, "alice");
        await using var observer = new GenericTestClient(server, "bob");
        await caller.ConnectAsync();
        await observer.ConnectAsync();

        var entityId = $"generic_bcast_{Guid.NewGuid():N}";
        var callerApi = await caller.Resolver.GetServiceAsync<GenericServiceApiClient>(entityId);
        await observer.Resolver.GetServiceAsync<GenericServiceApiClient>(entityId);

        await callerApi.MoveExplicitAsync(new Point { X = 8, Y = 6 }, 21);

        await WaitForAsync(() => observer.Resolver.GetState<GenericState>(entityId).Calls == 1);

        var observed = observer.Resolver.GetState<GenericState>(entityId);
        Assert.Equal(8, observed.LastX);
        Assert.Equal(6, observed.LastY);
        Assert.Equal("unboxed", observed.LastOrigin);
        Assert.Equal(21, observed.LastTag);
        Assert.Empty(observer.DetectedIssues);
    }

    /// <summary>
    /// Query proxies frame their own arguments. Under the generic branch that is the same
    /// length-prefixed envelope the dispatcher reads — the case that was wrong under MemoryPack
    /// and had no coverage in either branch.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Query_WithArguments_RoundTrips()
    {
        var (client, _, entityId) = await ConnectAsync();
        await using var __ = client;

        var query = new GenericServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

        Assert.Equal("100:200", await query.PeekPlainAsync(100, 200));
        Assert.Equal("4:5:unboxed:6", await query.PeekPointAsync(new Point { X = 4, Y = 5 }, 6));
    }

    /// <summary>
    /// Server-originated call through the generated {Service}ServerApi — same dispatcher, a
    /// different sender, so it has to box exactly like the client does.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerApi_TransformedArgument_RoundTrips()
    {
        var (client, _, entityId) = await ConnectAsync();
        await using var __ = client;

        var api = _fixture.ServerApiFactory.GetServerApi<IGenericService>(entityId);
        var result = await api.AdminMoveAsync(new Point { X = 2, Y = 4 }, 13);

        Assert.Equal("2:4:unboxed:13", result);
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
