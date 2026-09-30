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
/// A transport disconnect unsubscribes the session from every entity; a Resume at an unchanged
/// entity sequence answers Continued. That Continued path must re-register the subscriber in
/// full — a legacy client told to take a method as a patch must keep getting patches, not the
/// replay variant of a body it cannot run.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ReclaimRegistrationTests
{
    private readonly TestClusterFixture _fixture;

    public ReclaimRegistrationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task ContinuedReclaim_KeepsForcePatchForLegacySubscriber()
    {
        var legacyHash = await RegisterLegacySignatureAsync();
        var entityId = $"reclaim_fp_{Guid.NewGuid():N}";
        var stateType = typeof(ForcePatchFixtureState).FullName!;

        // Legacy observer: its signature says ForceServerPatch for ForcePatchFixture.Bump.
        var legacyId = $"reclaim-legacy-{Guid.NewGuid():N}";
        var legacy = _fixture.GrainFactory.GetGrain<ISessionManager>(legacyId);
        var legacySession = Guid.NewGuid();
        Assert.True((await legacy.ConnectAsync(legacySession, 0, SessionConnectMode.StartNew, 0, null, "1.0.0", legacyHash)).Success);
        var legacyObserver = new CapturingObserver();
        await legacy.SetObserverAsync(_fixture.GrainFactory.CreateObjectReference<ISessionObserver>(legacyObserver));
        var legacySub = await legacy.SubscribeToEntityAsync(entityId, stateType, "1.0.0", legacyHash);
        Assert.True(legacySub.Success, legacySub.Error);

        // Current-build writer.
        var writerId = $"reclaim-writer-{Guid.NewGuid():N}";
        var writer = _fixture.GrainFactory.GetGrain<ISessionManager>(writerId);
        var writerSession = Guid.NewGuid();
        Assert.True((await writer.ConnectAsync(writerSession, 0, SessionConnectMode.StartNew, 0, null, "1.0.0", 0UL)).Success);
        Assert.True((await writer.SubscribeToEntityAsync(entityId, stateType, "1.0.0")).Success);

        // Baseline: before any disconnect the legacy subscriber receives the patch variant.
        Assert.False((await writer.SendToEntityAsync(entityId, stateType, 1, BuildAddCall(1), 0, writerSession)).HasError);
        var before = await legacyObserver.WaitForOpAsync(TimeSpan.FromSeconds(5));
        Assert.False(Unpack(before).PatchBytes.IsEmpty);

        // Transport drop + Resume at the sequence the legacy client last saw → Continued.
        await legacy.OnTransportDisconnectedAsync();
        var claims = new List<SubscriptionClaim>
        {
            new() { EntityId = entityId, StateTypeName = stateType, LastKnownEntitySequence = before.EntitySequenceNumber },
        };
        var resume = await legacy.ConnectAsync(legacySession, 0, SessionConnectMode.Resume, 0, claims, "1.0.0", legacyHash);
        Assert.True(resume.Success, resume.Error);
        Assert.Equal(SubscriptionStatus.Continued, Assert.Single(resume.Subscriptions!).Status);
        await legacy.SetObserverAsync(_fixture.GrainFactory.CreateObjectReference<ISessionObserver>(legacyObserver));

        // After the reclaim the legacy subscriber must still get the patch variant.
        Assert.False((await writer.SendToEntityAsync(entityId, stateType, 2, BuildAddCall(2), 0, writerSession)).HasError);
        var after = await legacyObserver.WaitForOpAsync(TimeSpan.FromSeconds(5));
        Assert.False(Unpack(after).PatchBytes.IsEmpty,
            "Continued reclaim dropped the legacy subscriber's force-patch contributions — it received the replay variant.");
    }

    /// <summary>
    /// The test build's own signature with ForcePatchFixture.Bump claimed at v0 — an older build.
    /// The server only has v1, so the registry falls back to it and marks the method
    /// ForceServerPatch for this client.
    /// </summary>
    private async Task<ulong> RegisterLegacySignatureAsync()
    {
        var current = GameServiceDiscoveryBase.ClientSignature;
        var addId = AddMethodId;
        var methods = current.KnownMethods.Select(m => new KnownMethodEntry
        {
            ServiceName = m.ServiceName,
            Alias = m.Alias,
            Version = m.GlobalIndex == addId ? 0 : m.Version,
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
        });
        Assert.Equal(MethodStatus.ForceServerPatch, annotated.Statuses[methods.FindIndex(m => m.GlobalIndex == addId)]);
        return hash;
    }

    private MetaOperation Unpack(SessionOp op) => _fixture.Serializer.Unpack<MetaOperation>(op.OpBytes.ToArray())!;

    private const ushort AddMethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_Bump_v1;

    private static RpcCall BuildAddCall(int value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        global::MemoryPack.MemoryPackSerializer.Serialize(buffer, value);
        return new RpcCall
        {
            MethodId = AddMethodId,
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
