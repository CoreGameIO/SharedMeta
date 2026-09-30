using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Session;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// A resent RPC (reconnect, lost reply) must return the response it already produced, never run
/// again. The cache is the unacknowledged packet tail; once the client acknowledges a packet its
/// requests are no longer answerable from it and must still not re-execute.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SessionIdempotencyTests
{
    private const ushort BumpId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_Bump_v1;
    private readonly TestClusterFixture _fixture;

    public SessionIdempotencyTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task ResentRequest_ReturnsCachedResponse_WithoutReExecuting()
    {
        var (session, sessionId, entityId, stateType) = await ConnectAsync();

        var first = await session.SendToEntityAsync(entityId, stateType, 1, Bump(1), 0, sessionId);
        var resent = await session.SendToEntityAsync(entityId, stateType, 1, Bump(1), 0, sessionId);
        Assert.Equal(first.SequenceNumber, resent.SequenceNumber);

        var next = await session.SendToEntityAsync(entityId, stateType, 2, Bump(1), 0, sessionId);
        Assert.Equal(2, ResultOf(next, 2));
    }

    [Fact(Timeout = 30_000)]
    public async Task ResentRequest_AfterItsPacketWasAcknowledged_DoesNotReExecute()
    {
        var (session, sessionId, entityId, stateType) = await ConnectAsync();

        var first = await session.SendToEntityAsync(entityId, stateType, 1, Bump(1), 0, sessionId);
        var second = await session.SendToEntityAsync(entityId, stateType, 2, Bump(1), first.SequenceNumber, sessionId);
        Assert.Equal(2, ResultOf(second, 2));

        // Packet of request 1 is acknowledged and gone from the cache: a resend is answered with
        // an error, not a second Bump.
        var resent = await session.SendToEntityAsync(entityId, stateType, 1, Bump(1), first.SequenceNumber, sessionId);
        Assert.True(resent.Operations.Exists(op => op.RequestId == 1 && op.HasError));

        var third = await session.SendToEntityAsync(entityId, stateType, 3, Bump(1), second.SequenceNumber, sessionId);
        Assert.Equal(3, ResultOf(third, 3));
    }

    private async Task<(ISessionManager Session, Guid SessionId, string EntityId, string StateType)> ConnectAsync()
    {
        var entityId = $"idem_{Guid.NewGuid():N}";
        var stateType = typeof(ForcePatchFixtureState).FullName!;
        var session = _fixture.GrainFactory.GetGrain<ISessionManager>($"idem-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid();
        Assert.True((await session.ConnectAsync(sessionId, 0, SessionConnectMode.StartNew, 0, null, "1.0.0", 0UL)).Success);
        Assert.True((await session.SubscribeToEntityAsync(entityId, stateType, "1.0.0")).Success);
        return (session, sessionId, entityId, stateType);
    }

    private int ResultOf(SessionResponse response, long requestId)
    {
        var op = response.Operations.Single(o => o.RequestId == requestId);
        Assert.False(op.HasError, op.ErrorMessage);
        var metaOp = _fixture.Serializer.Unpack<MetaOperation>(op.OpBytes.ToArray())!;
        return _fixture.Serializer.Unpack<int>(metaOp.ResultBytes.ToArray());
    }

    private static RpcCall Bump(int value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, value);
        return new RpcCall
        {
            MethodId = BumpId,
            Payload = buffer.WrittenSpan.ToArray(),
            CallerId = "test",
            CallerClientVersion = "1.0.0",
        };
    }
}
