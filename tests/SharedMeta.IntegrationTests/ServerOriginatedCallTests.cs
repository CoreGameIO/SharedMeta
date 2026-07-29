using Orleans;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
// The server API is compiled into the server project (the shared assembly fences it behind
// SHAREDMETA_SERVER, which shared projects do not define).
using SharedMeta.Test.Meta1.Server;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Server-originated calls: what a generated <c>{Service}ServerApi</c>, a framework grain or a
/// background job produces — a call into <c>HandleCallFromEntityAsync</c> with no client behind it.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ServerOriginatedCallTests
{
    private readonly TestClusterFixture _fixture;

    public ServerOriginatedCallTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Regression: a server-originated call carries no <c>CallerClientVersion</c>, and config
    /// resolution rejects a missing version outright ("clientAppVersion is required"). The grain
    /// must substitute the server's current version, otherwise every admin action against a
    /// config-bound service throws before reaching the method body.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerOriginatedCall_WithoutCallerClientVersion_ResolvesConfig()
    {
        var entityId = $"srv_originated_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // Subscribe so the entity exists and has a live subscriber to broadcast to.
        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);
        Assert.Equal(5, counter.State.Sum);

        // AddClamped is GenerateClientApi = false and reads Config.MaxValue — exactly the shape of
        // an admin method. Call it the way the generated server API does: straight at the grain,
        // with CallerClientVersion left unset.
        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<CounterState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_AddClamped_v0,
            Payload = client.Serializer.Pack(42),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        });

        Assert.False(result.HasError, result.Error);
    }

    /// <summary>
    /// Diagnostic control: same call with an explicit version. Isolates whether a failure comes
    /// from the version substitution or from the direct-grain call shape itself.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerOriginatedCall_WithExplicitCallerClientVersion_ResolvesConfig()
    {
        var entityId = $"srv_explicit_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);

        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<CounterState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_AddClamped_v0,
            Payload = client.Serializer.Pack(42),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
            CallerClientVersion = "1.0.0",
        });

        Assert.False(result.HasError, result.Error);
    }

    /// <summary>
    /// The effect must reach subscribers: a server-originated call is a normal dispatch, so the
    /// broadcast fans out and a connected client converges without asking for anything.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerOriginatedCall_BroadcastsToSubscribers()
    {
        var entityId = $"srv_broadcast_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);
        Assert.Equal(5, counter.State.Sum);

        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<CounterState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_AddClamped_v0,
            Payload = client.Serializer.Pack(37),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        });
        Assert.False(result.HasError, result.Error);

        await Task.Delay(200);
        Assert.Equal(42, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// The generated API is the supported entry point — exercise it rather than the raw grain call,
    /// including the explicit version override used to act as a specific client build.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GeneratedServerApi_AppliesCall_AndHonoursExplicitVersion()
    {
        var entityId = $"srv_api_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);

        // Default: no version passed — the entity resolves its own.
        var api = _fixture.GrainFactory.GetServerApi<SharedMeta.Test.Meta1.ICounterService>(entityId);
        var clamped = await api.AddClampedAsync(37);
        Assert.Equal(37, clamped);

        // Explicit override: same call pinned to a named client build.
        var pinnedApi = _fixture.GrainFactory.GetServerApi<SharedMeta.Test.Meta1.ICounterService>(entityId, "1.0.0");
        var clampedPinned = await pinnedApi.AddClampedAsync(1);
        Assert.Equal(1, clampedPinned);

        await Task.Delay(200);
        Assert.Equal(43, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// A server-originated call runs as the entity's owner, not as the server. The service body
    /// reads <c>Context.CallerClientVersion</c>, so the subscriber's version must reach it even
    /// though no client made this call.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerOriginatedCall_RunsUnderOwnerClientVersion()
    {
        var entityId = $"srv_owner_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // Subscribing records this client's app version on the persisted subscriber.
        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);

        // Drops the runtime config pin but leaves the persisted subscriber version — the shape an
        // admin action against an offline player hits.
        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<CounterState>>(entityId);
        await grain.UnsubscribeAsync("alice");

        var api = _fixture.GrainFactory.GetServerApi<SharedMeta.Test.Meta1.ICounterService>(entityId);
        var clamped = await api.AddClampedAsync(9);

        // Reaching the body at all proves config resolved; the clamp proves it was a real config
        // (MaxValue), not a default-constructed one.
        Assert.Equal(9, clamped);
    }

    /// <summary>
    /// A complex argument must survive the server API's own packing. Admin methods are
    /// <c>GenerateClientApi = false</c>, so no client call exercises collection members over
    /// this path — a dictionary arriving empty would otherwise look like a caller mistake.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GeneratedServerApi_ComplexArgument_ArrivesIntact()
    {
        var entityId = $"srv_dto_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);

        var api = _fixture.GrainFactory.GetServerApi<SharedMeta.Test.Meta1.ICounterService>(entityId);
        var received = await api.ApplyGrantAsync(new SharedMeta.Test.Meta1.GrantRequest
        {
            Reason = "admin grant",
            Currencies =
            {
                [SharedMeta.Test.Meta1.GrantCurrency.Gold] = 100,
                [SharedMeta.Test.Meta1.GrantCurrency.Gems] = 7,
            },
        });

        // The service returns what it actually saw, so an empty dictionary fails here rather than
        // silently applying nothing.
        Assert.Equal(2, received);

        await Task.Delay(200);
        Assert.Equal(112, counter.State.Sum);
    }

    /// <summary>
    /// The DI handle is the call shape an admin grain or endpoint should use: no serializer passed
    /// by hand, and identical whichever serializer the service's assembly declared.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MetaServerApiFactory_ResolvesFromDi_AndApplies()
    {
        var entityId = $"srv_factory_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(5, 1);

        var factory = new SharedMeta.Server.Core.MetaServerApiFactory(_fixture.GrainFactory, client.Serializer);
        var clamped = await factory.GetServerApi<SharedMeta.Test.Meta1.ICounterService>(entityId).AddClampedAsync(12);

        Assert.Equal(12, clamped);
    }

    /// <summary>
    /// No subscriber, never activated: the admin case. The grain must still run the call, so the
    /// mutation is durable and visible to whoever subscribes later.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ServerOriginatedCall_OnColdEntity_Succeeds()
    {
        var entityId = $"srv_cold_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();

        // Deliberately no subscribe before the call.
        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<CounterState>>(entityId);
        var result = await grain.HandleCallFromEntityAsync(new RpcCall
        {
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_AddClamped_v0,
            Payload = client.Serializer.Pack(11),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        });

        Assert.False(result.HasError, result.Error);

        var resolver = client.CreateResolver();
        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        Assert.Equal(11, counter.State.Sum);
    }
}
