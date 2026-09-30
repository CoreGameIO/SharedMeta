using System.Collections.Concurrent;
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
/// <c>[MetaMethod(Mode = ServerPatch | ServerReplace)]</c> is a server-side contract: the server
/// ships a diff or the whole state instead of a body replay. The server used to ignore the
/// declaration and run every method as Optimistic unless the host registered a runtime override.
/// Under ServerPatch every subscriber takes the diff — not only the caller.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class DeclaredServerModeTests
{
    private const ushort BumpReplaceId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_BumpReplace_v0;
    private const ushort BumpPatchId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_BumpPatch_v0;

    private readonly TestClusterFixture _fixture;

    public DeclaredServerModeTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclaredServerReplace_BroadcastCarriesState()
    {
        var op = await CallObservedAsync(BumpReplaceId, 3);
        Assert.False(op.StateBytes.IsEmpty, "Declared ServerReplace reached a subscriber without the state.");
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclaredServerPatch_BroadcastCarriesPatchNotReplay()
    {
        var op = await CallObservedAsync(BumpPatchId, 3);
        Assert.False(op.PatchBytes.IsEmpty, "Declared ServerPatch reached a subscriber without a patch.");
        Assert.True(op.ReplayPayload.IsEmpty);
    }

    [Fact(Timeout = 60_000)]
    public async Task RuntimeOverride_StillWinsOverDeclaration()
    {
        _fixture.ExecutionModeProvider.SetMode(BumpReplaceId, ExecutionMode.Optimistic);
        try
        {
            var op = await CallObservedAsync(BumpReplaceId, 3);
            Assert.True(op.StateBytes.IsEmpty);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    /// <summary>One call from a writer session, as the broadcast a second subscriber receives.</summary>
    private async Task<MetaOperation> CallObservedAsync(ushort methodId, int arg)
    {
        var entityId = $"declared_mode_{Guid.NewGuid():N}";
        var stateType = typeof(ForcePatchFixtureState).FullName!;

        var observerSession = _fixture.GrainFactory.GetGrain<ISessionManager>($"declared-obs-{Guid.NewGuid():N}");
        Assert.True((await observerSession.ConnectAsync(Guid.NewGuid(), 0, SessionConnectMode.StartNew, 0, null, "1.0.0", 0UL)).Success);
        var observer = new CapturingObserver();
        await observerSession.SetObserverAsync(_fixture.GrainFactory.CreateObjectReference<ISessionObserver>(observer));
        Assert.True((await observerSession.SubscribeToEntityAsync(entityId, stateType, "1.0.0")).Success);

        var writer = _fixture.GrainFactory.GetGrain<ISessionManager>($"declared-writer-{Guid.NewGuid():N}");
        var writerSession = Guid.NewGuid();
        Assert.True((await writer.ConnectAsync(writerSession, 0, SessionConnectMode.StartNew, 0, null, "1.0.0", 0UL)).Success);
        Assert.True((await writer.SubscribeToEntityAsync(entityId, stateType, "1.0.0")).Success);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, arg);
        var call = new RpcCall
        {
            MethodId = methodId,
            Payload = buffer.WrittenSpan.ToArray(),
            CallerId = "test",
            CallerClientVersion = "1.0.0",
        };
        Assert.False((await writer.SendToEntityAsync(entityId, stateType, 1, call, 0, writerSession)).HasError);

        var sessionOp = await observer.WaitForOpAsync(TimeSpan.FromSeconds(5));
        return _fixture.Serializer.Unpack<MetaOperation>(sessionOp.OpBytes.ToArray())!;
    }

    private class CapturingObserver : ISessionObserver
    {
        private readonly ConcurrentQueue<SessionOp> _ops = new();

        public Task OnBatch(SessionResponse response)
        {
            foreach (var op in response.Operations)
                if (!op.OpBytes.IsEmpty) _ops.Enqueue(op);
            return Task.CompletedTask;
        }

        public Task OnNotice(SessionNotice notice) => Task.CompletedTask;
        public Task OnEntityDeactivating(string entityId) => Task.CompletedTask;
        public Task OnSessionTerminated(string reason) => Task.CompletedTask;

        public async Task<SessionOp> WaitForOpAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_ops.TryDequeue(out var op)) return op;
                await Task.Delay(20);
            }
            throw new TimeoutException("No broadcast reached the observer.");
        }
    }
}
