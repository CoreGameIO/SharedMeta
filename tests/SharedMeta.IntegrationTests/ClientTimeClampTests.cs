using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// <c>RpcCall.ServerTimeTicks</c> is what every time-based mechanic reads through
/// <c>Context.ServerTimeTicks</c> — cooldowns, timers, energy regeneration. It has to travel on
/// the wire so the optimistic client and the authoritative server compute the same instant, which
/// also means a modified client can put any value in it.
///
/// Threat model matches <see cref="ClientApiSecurityTests"/>: the attacker crafts an
/// <see cref="RpcCallRequest"/> by hand. Here they set the clock years forward to collect every
/// regeneration tick at once. The server must execute under its own clock instead.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ClientTimeClampTests
{
    private readonly TestClusterFixture _fixture;

    public ClientTimeClampTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A forged far-future timestamp must not reach the service body. The call still executes —
    /// rejecting it would turn a clock hiccup into a failed purchase — but under the silo clock.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ForgedFutureClientTime_IsReplacedWithSiloClock()
    {
        var entityId = $"clk_future_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory(
            new MetaTransportOptions { MaxClientTimeSkew = TimeSpan.FromSeconds(30) }));
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(1, 1);

        var forgedTicks = DateTime.UtcNow.AddYears(5).Ticks;
        var before = DateTime.UtcNow.Ticks;
        await client.Connection.RpcCallAsync(new RpcCallRequest
        {
            EntityId = entityId,
            // The session grain keys subscriptions by (entityId, stateType); without this the
            // forged call is refused as "not subscribed" before it can reach the body.
            StateTypeName = typeof(CounterState).FullName!,
            RequestId = 2,
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Add_v0,
            Payload = PackAddValueArgs(5, 2),
            ServerTimeTicks = forgedTicks
        });
        var after = DateTime.UtcNow.Ticks;

        var state = await ReadServerStateAsync(entityId);
        Assert.NotEqual(forgedTicks, state.LastServerTimeTicks);
        Assert.InRange(state.LastServerTimeTicks, before, after);
    }

    /// <summary>
    /// The honest path must be untouched. A real client derives this value from the last server
    /// sync plus local elapsed time, so it lands within round-trip latency of the silo — the
    /// server has to pass it through verbatim, or every optimistic result would disagree with the
    /// server's and flood the desync channel.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ClientTimeWithinWindow_PassesThroughUnchanged()
    {
        var entityId = $"clk_ok_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory(
            new MetaTransportOptions { MaxClientTimeSkew = TimeSpan.FromSeconds(30) }));
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(1, 1);

        // Two seconds of drift is well inside the window and must survive verbatim.
        var honestTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
        await client.Connection.RpcCallAsync(new RpcCallRequest
        {
            EntityId = entityId,
            // The session grain keys subscriptions by (entityId, stateType); without this the
            // forged call is refused as "not subscribed" before it can reach the body.
            StateTypeName = typeof(CounterState).FullName!,
            RequestId = 2,
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Add_v0,
            Payload = PackAddValueArgs(5, 2),
            ServerTimeTicks = honestTicks
        });

        var state = await ReadServerStateAsync(entityId);
        Assert.Equal(honestTicks, state.LastServerTimeTicks);
    }

    /// <summary>
    /// The bound must hold for a host that never registered <see cref="MetaTransportOptions"/> —
    /// most of the suite constructs its handler factory that way, and so does a first-run server.
    /// An opt-in guard protects only the deployments that already thought about it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ForgedFutureClientTime_IsClamped_WithoutRegisteredTransportOptions()
    {
        var entityId = $"clk_noopt_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(1, 1);

        var forgedTicks = DateTime.UtcNow.AddYears(5).Ticks;
        var before = DateTime.UtcNow.Ticks;
        await client.Connection.RpcCallAsync(new RpcCallRequest
        {
            EntityId = entityId,
            // The session grain keys subscriptions by (entityId, stateType); without this the
            // forged call is refused as "not subscribed" before it can reach the body.
            StateTypeName = typeof(CounterState).FullName!,
            RequestId = 2,
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Add_v0,
            Payload = PackAddValueArgs(5, 2),
            ServerTimeTicks = forgedTicks
        });
        var after = DateTime.UtcNow.Ticks;

        var state = await ReadServerStateAsync(entityId);
        Assert.InRange(state.LastServerTimeTicks, before, after);
    }

    /// <summary>
    /// Escape hatch: a host that must keep the pre-0.41.0 "trust the client" behaviour can set the
    /// window to zero. Pinned so the disable path doesn't rot.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SkewCheckDisabled_TrustsClientTime()
    {
        var entityId = $"clk_off_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory(
            new MetaTransportOptions { MaxClientTimeSkew = TimeSpan.Zero }));
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(1, 1);

        var forgedTicks = DateTime.UtcNow.AddYears(5).Ticks;
        await client.Connection.RpcCallAsync(new RpcCallRequest
        {
            EntityId = entityId,
            // The session grain keys subscriptions by (entityId, stateType); without this the
            // forged call is refused as "not subscribed" before it can reach the body.
            StateTypeName = typeof(CounterState).FullName!,
            RequestId = 2,
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Add_v0,
            Payload = PackAddValueArgs(5, 2),
            ServerTimeTicks = forgedTicks
        });

        var state = await ReadServerStateAsync(entityId);
        Assert.Equal(forgedTicks, state.LastServerTimeTicks);
    }

    /// <summary>
    /// Args for <c>AddValue(int value, int clientSequence)</c> in the wire form the generated
    /// client produces: each parameter appended to one buffer, no envelope.
    /// </summary>
    private static byte[] PackAddValueArgs(int value, int clientSequence)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        MemoryPack.MemoryPackSerializer.Serialize(buffer, value);
        MemoryPack.MemoryPackSerializer.Serialize(buffer, clientSequence);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Read the authoritative state straight off the grain — the forged RPC bypasses the typed
    /// client, so the local container never saw the call.
    /// </summary>
    private async Task<CounterState> ReadServerStateAsync(string entityId)
    {
        var grain = _fixture.GrainFactory.GetGrain<SharedMeta.Server.Core.Grains.IEntityGrain<CounterState>>(entityId);
        var bytes = await grain.GetEntityStateAsync();
        Assert.NotNull(bytes);
        return _fixture.Serializer.Unpack<CounterState>(bytes!)!;
    }
}
