using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Memory;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core.Memory;
using Xunit;

namespace SharedMeta.IntegrationTests.Memory;

/// <summary>
/// Unit tests for <see cref="PooledPayloadRegistry"/>, <see cref="PooledPayloadBufferWriter"/>,
/// <see cref="PooledPayload"/>, and the <c>PackPooled</c> extension. No Orleans cluster needed
/// — these cover the slot lifecycle, ref-count discipline, encoding round-trips, and the
/// serializer bridge.
/// </summary>
public class PooledPayloadRegistryTests
{
    [Fact]
    public void EncodeRef_RoundTrip_PreservesSiloAndSlot()
    {
        var refId = PooledPayload.EncodeRef(siloId: 7, slotIndex: 12345);
        Assert.Equal(7, PooledPayload.DecodeSiloId(refId));
        Assert.Equal(12345, PooledPayload.DecodeSlotIndex(refId));
    }

    [Fact]
    public void EncodeRef_SiloOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PooledPayload.EncodeRef(32, 0));
    }

    [Fact]
    public void EncodeRef_SlotOutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PooledPayload.EncodeRef(0, PooledPayload.MaxSlotsPerSilo));
    }

    [Fact]
    public void AcquireWriter_FreshSlot_RefCountIsOneAndCountIncreases()
    {
        var reg = PooledPayloadRegistry.CreatePinned(siloId: 3);
        Assert.Equal(0, reg.AllocatedSlotCount);

        var writer = reg.AcquireWriter(64);
        var span = writer.GetSpan(4);
        span[0] = 0xAA; span[1] = 0xBB; span[2] = 0xCC; span[3] = 0xDD;
        writer.Advance(4);
        var payload = writer.Complete();

        Assert.Equal(1, reg.AllocatedSlotCount);
        Assert.Equal(1, reg.GetRefCount(payload));
        Assert.Equal(3, payload.SiloId);
        Assert.False(payload.IsEmpty);
        Assert.Equal(4, payload.Length);
        var bytes = payload.Memory.ToArray();
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, bytes);

        reg.Release(payload);
        Assert.Equal(0, reg.AllocatedSlotCount);
    }

    [Fact]
    public void IncrementRefThenRelease_FreesAtZero()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        var writer = reg.AcquireWriter(16);
        writer.GetSpan(1)[0] = 1;
        writer.Advance(1);
        var payload = writer.Complete();

        reg.IncrementRef(payload, 2);
        Assert.Equal(3, reg.GetRefCount(payload));

        reg.Release(payload);
        reg.Release(payload);
        Assert.Equal(1, reg.GetRefCount(payload));
        Assert.Equal(1, reg.AllocatedSlotCount);

        reg.Release(payload);
        Assert.Equal(0, reg.AllocatedSlotCount);
    }

    [Fact]
    public void DoubleRelease_Throws()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        var writer = reg.AcquireWriter(8);
        writer.Advance(0);
        var payload = writer.Complete();

        reg.Release(payload);
        Assert.Throws<InvalidOperationException>(() => reg.Release(payload));
    }

    [Fact]
    public void IncrementRefOnFreedSlot_Throws()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        var writer = reg.AcquireWriter(8);
        writer.Advance(0);
        var payload = writer.Complete();
        reg.Release(payload);

        Assert.Throws<InvalidOperationException>(() => reg.IncrementRef(payload, 1));
    }

    [Fact]
    public void WriterDispose_WithoutComplete_ReleasesSlot()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        var writer = reg.AcquireWriter(32);
        writer.GetSpan(4); writer.Advance(4);

        Assert.Equal(1, reg.AllocatedSlotCount);
        writer.Dispose();
        Assert.Equal(0, reg.AllocatedSlotCount);
    }

    [Fact]
    public void WriterDispose_AfterComplete_IsNoop()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        var writer = reg.AcquireWriter(32);
        writer.Advance(0);
        var payload = writer.Complete();

        writer.Dispose(); // should NOT release the payload's ref
        Assert.Equal(1, reg.AllocatedSlotCount);
        Assert.Equal(1, reg.GetRefCount(payload));

        reg.Release(payload);
        Assert.Equal(0, reg.AllocatedSlotCount);
    }

    [Fact]
    public void Writer_GrowsBeyondInitialCapacity()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        // Initial capacity = 8, then write 100 bytes — forces buffer grow.
        var writer = reg.AcquireWriter(8);
        for (int i = 0; i < 100; i++)
        {
            writer.GetSpan(1)[0] = (byte)(i & 0xFF);
            writer.Advance(1);
        }
        var payload = writer.Complete();

        Assert.Equal(100, payload.Length);
        var bytes = payload.Memory.ToArray();
        for (int i = 0; i < 100; i++)
            Assert.Equal((byte)(i & 0xFF), bytes[i]);

        reg.Release(payload);
    }

    [Fact]
    public void ReleasedSlot_IsReused()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        var w1 = reg.AcquireWriter(16);
        w1.Advance(0);
        var p1 = w1.Complete();
        var slot1 = p1.SlotIndex;
        reg.Release(p1);

        var w2 = reg.AcquireWriter(16);
        w2.Advance(0);
        var p2 = w2.Complete();
        Assert.Equal(slot1, p2.SlotIndex);
        reg.Release(p2);
    }

    [Fact]
    public void PackPooled_RoundTripsThroughMemoryPack()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        var ser = new MemoryPackMetaSerializer();

        // Use RpcCall as a realistic DTO — already [MemoryPackable] in the codebase.
        var original = new RpcCall { MethodId = 42, Payload = new byte[] { 1, 2, 3, 4, 5 } };

        var payload = ser.PackPooled(original, reg, initialCapacity: 32);
        Assert.False(payload.IsEmpty);
        Assert.Equal(reg.SiloId, payload.SiloId);

        var clone = ser.Unpack<RpcCall>(payload.Memory);
        Assert.Equal(original.MethodId, clone.MethodId);
        Assert.Equal(original.Payload, clone.Payload);

        reg.Release(payload);
        Assert.Equal(0, reg.AllocatedSlotCount);
    }

    [Fact]
    public async Task ConcurrentAcquireRelease_DoesNotLeakOrCorrupt()
    {
        var reg = PooledPayloadRegistry.CreatePinned(0);
        const int threads = 8;
        const int iterations = 500;

        await Task.WhenAll(System.Linq.Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var w = reg.AcquireWriter(64);
                int n = (t * 31 + i) & 0x3F; // 0..63 bytes
                var span = w.GetSpan(n);
                for (int k = 0; k < n; k++) span[k] = (byte)k;
                w.Advance(n);
                var p = w.Complete();
                Assert.Equal(n, p.Length);
                reg.Release(p);
            }
        })));

        Assert.Equal(0, reg.AllocatedSlotCount);
    }
}

