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
/// A legacy subscriber told to take a method as a patch cannot run that body — nor the bodies of
/// the triggers it fires. Trigger ops must reach it as patches too: when the triggering method is
/// force-patched, and when only the trigger is.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ForcePatchTriggerTests
{
    private const ushort BumpId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_Bump_v1;
    private const ushort OnBumpedId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_OnBumped_v1;

    private readonly TestClusterFixture _fixture;

    public ForcePatchTriggerTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task ForcePatchedMethod_TriggerOpsCarryPatch()
    {
        var (legacyOp, currentOp) = await BumpObservedByLegacyAndCurrentAsync(legacyAt: BumpId);

        Assert.False(legacyOp.PatchBytes.IsEmpty);
        var legacyTrigger = Assert.Single(legacyOp.Triggers!);
        Assert.False(legacyTrigger.PatchBytes.IsEmpty,
            "Trigger op reached a force-patch subscriber without a patch — it would replay a trigger body it cannot run.");
        Assert.True(legacyTrigger.ReplayPayload.IsEmpty);

        // The replay audience is unchanged: body replay for the method and the trigger.
        Assert.True(currentOp.PatchBytes.IsEmpty);
        Assert.True(Assert.Single(currentOp.Triggers!).PatchBytes.IsEmpty);
    }

    [Fact(Timeout = 60_000)]
    public async Task ForcePatchedTriggerOnly_TriggerOpCarriesPatch_MethodStillReplays()
    {
        var (legacyOp, currentOp) = await BumpObservedByLegacyAndCurrentAsync(legacyAt: OnBumpedId);

        // Bump itself is compatible for this client: no patch, so it replays the body.
        Assert.True(legacyOp.PatchBytes.IsEmpty);
        var legacyTrigger = Assert.Single(legacyOp.Triggers!);
        Assert.False(legacyTrigger.PatchBytes.IsEmpty,
            "Force-patched trigger reached the legacy subscriber without a patch.");

        Assert.True(Assert.Single(currentOp.Triggers!).PatchBytes.IsEmpty);
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatchMode_EverySubscriberGetsThePatch()
    {
        _fixture.ExecutionModeProvider.SetMode(BumpId, ExecutionMode.ServerPatch);
        try
        {
            var (_, currentOp) = await BumpObservedByLegacyAndCurrentAsync(legacyAt: OnBumpedId);
            Assert.False(currentOp.PatchBytes.IsEmpty, "ServerPatch broadcast reached a subscriber as a body replay.");
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    /// <summary>
    /// One Bump from a current-build writer, observed by a legacy subscriber whose signature
    /// claims <paramref name="legacyAt"/> at v0 (force-patched) and by a current-build subscriber.
    /// </summary>
    private async Task<(MetaOperation Legacy, MetaOperation Current)> BumpObservedByLegacyAndCurrentAsync(ushort legacyAt)
    {
        var legacyHash = await RegisterLegacySignatureAsync(legacyAt);
        var entityId = $"fp_trigger_{Guid.NewGuid():N}";
        var stateType = typeof(ForcePatchFixtureState).FullName!;

        var legacyObserver = await ConnectObserverAsync(entityId, stateType, legacyHash);
        var currentObserver = await ConnectObserverAsync(entityId, stateType, 0UL);

        var writerId = $"fp-trigger-writer-{Guid.NewGuid():N}";
        var writer = _fixture.GrainFactory.GetGrain<ISessionManager>(writerId);
        var writerSession = Guid.NewGuid();
        Assert.True((await writer.ConnectAsync(writerSession, 0, SessionConnectMode.StartNew, 0, null, "1.0.0", 0UL)).Success);
        Assert.True((await writer.SubscribeToEntityAsync(entityId, stateType, "1.0.0")).Success);
        Assert.False((await writer.SendToEntityAsync(entityId, stateType, 1, BuildBumpCall(1), 0, writerSession)).HasError);

        return (Unpack(await legacyObserver.WaitForOpAsync(TimeSpan.FromSeconds(5))),
                Unpack(await currentObserver.WaitForOpAsync(TimeSpan.FromSeconds(5))));
    }

    private async Task<CapturingObserver> ConnectObserverAsync(string entityId, string stateType, ulong signatureHash)
    {
        var session = _fixture.GrainFactory.GetGrain<ISessionManager>($"fp-trigger-obs-{Guid.NewGuid():N}");
        Assert.True((await session.ConnectAsync(Guid.NewGuid(), 0, SessionConnectMode.StartNew, 0, null, "1.0.0", signatureHash)).Success);
        var observer = new CapturingObserver();
        await session.SetObserverAsync(_fixture.GrainFactory.CreateObjectReference<ISessionObserver>(observer));
        var sub = await session.SubscribeToEntityAsync(entityId, stateType, "1.0.0", signatureHash);
        Assert.True(sub.Success, sub.Error);
        return observer;
    }

    /// <summary>
    /// The test build's own signature with one method claimed at v0 — an older build. The server
    /// only has v1, so the registry falls back to it and marks the method ForceServerPatch.
    /// </summary>
    private async Task<ulong> RegisterLegacySignatureAsync(ushort legacyMethodId)
    {
        var current = GameServiceDiscoveryBase.ClientSignature;
        var methods = current.KnownMethods.Select(m => new KnownMethodEntry
        {
            ServiceName = m.ServiceName,
            Alias = m.Alias,
            Version = m.GlobalIndex == legacyMethodId ? 0 : m.Version,
            ArgHash = m.ArgHash,
            GlobalIndex = m.GlobalIndex,
        }).ToList();

        var hash = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        var registry = new ClientSignatureRegistry(_fixture.GrainFactory, GameServiceDiscoveryBase.ServerSignature);
        var annotated = await registry.RegisterAsync(new MetaClientSignature
        {
            SignatureHash = hash,
            ClientVersion = "1.0.0",
            KnownMethods = methods,
            KnownStateTypes = current.KnownStateTypes,
        });
        Assert.Equal(MethodStatus.ForceServerPatch, annotated.Statuses[methods.FindIndex(m => m.GlobalIndex == legacyMethodId)]);
        return hash;
    }

    private MetaOperation Unpack(SessionOp op) => _fixture.Serializer.Unpack<MetaOperation>(op.OpBytes.ToArray())!;

    private static RpcCall BuildBumpCall(int value)
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
