using SharedMeta.Server.Core.Session;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Focused unit tests for <see cref="RpcOrderingBuffer{T}"/>. These do not need a real
/// Orleans cluster — they cover ring-buffer mechanics in isolation: classify boundaries,
/// in-order vs stash dispatch, head wraparound, overflow / duplicate / stale results,
/// and reset.
/// </summary>
public class RpcOrderingBufferTests
{
    private sealed class Item
    {
        public string Tag { get; set; } = "";
    }

    private static RpcOrderingBuffer<Item> NewBuffer(int capacity = 8) =>
        new RpcOrderingBuffer<Item>(capacity);

    [Fact]
    public void Classify_FreshBuffer_ReturnsExpected()
    {
        var buf = NewBuffer();

        Assert.Equal(RequestPosition.NextExpected, buf.Classify(1));
        Assert.Equal(RequestPosition.OutOfOrder, buf.Classify(2));
        Assert.Equal(RequestPosition.OutOfOrder, buf.Classify(7));
        // Negative or zero is "stale" by definition (LastDispatched == 0).
        Assert.Equal(RequestPosition.Stale, buf.Classify(0));
    }

    [Fact]
    public void MarkDispatchedInOrder_AdvancesBaseline()
    {
        var buf = NewBuffer();

        buf.MarkDispatchedInOrder(1);
        Assert.Equal(1, buf.LastDispatchedRequestId);
        Assert.Equal(2, buf.NextExpectedRequestId);
        Assert.Equal(RequestPosition.NextExpected, buf.Classify(2));

        buf.MarkDispatchedInOrder(2);
        Assert.Equal(2, buf.LastDispatchedRequestId);
        Assert.True(buf.IsEmpty);
    }

    [Fact]
    public void MarkDispatchedInOrder_OutOfOrder_SilentlyIgnored()
    {
        var buf = NewBuffer();

        buf.MarkDispatchedInOrder(5);
        Assert.Equal(0, buf.LastDispatchedRequestId);
    }

    [Fact]
    public void TryStash_OutOfOrder_StoresAndDequeues()
    {
        var buf = NewBuffer();

        // Stash req=2, then req=3.
        Assert.Equal(StashResult.Stashed, buf.TryStash(2, new Item { Tag = "two" }));
        Assert.Equal(StashResult.Stashed, buf.TryStash(3, new Item { Tag = "three" }));
        Assert.Equal(2, buf.Count);

        // Cannot dequeue yet — head expects req=1.
        Assert.False(buf.TryDequeueNext(out _, out _));

        // Process req=1 in-order.
        buf.MarkDispatchedInOrder(1);

        // Now drain.
        Assert.True(buf.TryDequeueNext(out var id1, out var p1));
        Assert.Equal(2, id1);
        Assert.Equal("two", p1!.Tag);

        Assert.True(buf.TryDequeueNext(out var id2, out var p2));
        Assert.Equal(3, id2);
        Assert.Equal("three", p2!.Tag);

        Assert.False(buf.TryDequeueNext(out _, out _));
        Assert.True(buf.IsEmpty);
        Assert.Equal(3, buf.LastDispatchedRequestId);
    }

    [Fact]
    public void TryStash_DuplicateSameRequestId_ReturnsDuplicate()
    {
        var buf = NewBuffer();

        Assert.Equal(StashResult.Stashed, buf.TryStash(5, new Item { Tag = "first" }));
        Assert.Equal(StashResult.Duplicate, buf.TryStash(5, new Item { Tag = "second" }));
        Assert.Equal(1, buf.Count);

        // Drain to verify the FIRST entry won and the duplicate didn't overwrite it.
        for (int i = 1; i < 5; i++) buf.MarkDispatchedInOrder(i);
        Assert.True(buf.TryDequeueNext(out var id, out var p));
        Assert.Equal(5, id);
        Assert.Equal("first", p!.Tag);
    }

    [Fact]
    public void TryStash_StaleRequest_ReturnsStale()
    {
        var buf = NewBuffer();
        buf.MarkDispatchedInOrder(1);
        buf.MarkDispatchedInOrder(2);
        buf.MarkDispatchedInOrder(3);

        // RequestIds 1..3 are now in the past; reachable through TryStash → Stale.
        Assert.Equal(StashResult.Stale, buf.TryStash(2, new Item()));
        Assert.Equal(StashResult.Stale, buf.TryStash(3, new Item()));
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void TryStash_BeyondCapacity_ReturnsOverflow()
    {
        var buf = NewBuffer(capacity: 4);

        // Stash offsets 1..3 (RequestIds 2..4) — fills 3 slots.
        Assert.Equal(StashResult.Stashed, buf.TryStash(2, new Item()));
        Assert.Equal(StashResult.Stashed, buf.TryStash(3, new Item()));
        Assert.Equal(StashResult.Stashed, buf.TryStash(4, new Item()));
        Assert.Equal(3, buf.Count);

        // RequestId=5 has offset=4 which equals capacity → still fits? No, indexing is
        // [0..capacity-1], so offset 4 is out of range for capacity=4.
        Assert.Equal(StashResult.Overflow, buf.TryStash(5, new Item()));
        Assert.Equal(3, buf.Count); // unchanged
    }

    [Fact]
    public void RingBuffer_HeadWrapsAround_OnFullCycle()
    {
        var buf = NewBuffer(capacity: 4);

        // Process more requests than capacity in-order so the head walks around the
        // ring multiple times. Each step verifies the head doesn't fall behind.
        for (int i = 1; i <= 12; i++)
        {
            Assert.Equal(RequestPosition.NextExpected, buf.Classify(i));
            buf.MarkDispatchedInOrder(i);
        }
        Assert.Equal(12, buf.LastDispatchedRequestId);

        // After wraparound, parking still works for the new "next" range.
        Assert.Equal(StashResult.Stashed, buf.TryStash(14, new Item { Tag = "fourteen" }));
        Assert.Equal(StashResult.Stashed, buf.TryStash(15, new Item { Tag = "fifteen" }));

        buf.MarkDispatchedInOrder(13);

        Assert.True(buf.TryDequeueNext(out var id14, out var p14));
        Assert.Equal(14, id14);
        Assert.Equal("fourteen", p14!.Tag);

        Assert.True(buf.TryDequeueNext(out var id15, out var p15));
        Assert.Equal(15, id15);
        Assert.Equal("fifteen", p15!.Tag);
    }

    [Fact]
    public void Mixed_InOrderAndStashed_DrainsCorrectly()
    {
        var buf = NewBuffer(capacity: 8);

        // Park 3, 5, 4, 2 — out of order arrivals.
        buf.TryStash(3, new Item { Tag = "3" });
        buf.TryStash(5, new Item { Tag = "5" });
        buf.TryStash(4, new Item { Tag = "4" });
        buf.TryStash(2, new Item { Tag = "2" });
        Assert.Equal(4, buf.Count);

        // req=1 arrives in-order; cascade should drain everything.
        buf.MarkDispatchedInOrder(1);
        var dispatched = new System.Collections.Generic.List<long>();
        while (buf.TryDequeueNext(out var id, out var _))
            dispatched.Add(id);

        Assert.Equal(new long[] { 2, 3, 4, 5 }, dispatched);
        Assert.True(buf.IsEmpty);
        Assert.Equal(5, buf.LastDispatchedRequestId);
    }

    [Fact]
    public void Reset_ClearsAllStateAndAllowsReuse()
    {
        var buf = NewBuffer(capacity: 8);

        for (int i = 1; i <= 4; i++) buf.MarkDispatchedInOrder(i);
        buf.TryStash(6, new Item { Tag = "six" });
        buf.TryStash(7, new Item { Tag = "seven" });
        Assert.Equal(2, buf.Count);
        Assert.Equal(4, buf.LastDispatchedRequestId);

        buf.Reset();

        Assert.True(buf.IsEmpty);
        Assert.Equal(0, buf.LastDispatchedRequestId);
        Assert.Equal(1, buf.NextExpectedRequestId);

        // Buffer is reusable as if newly constructed.
        Assert.Equal(StashResult.Stashed, buf.TryStash(2, new Item { Tag = "fresh-two" }));
        buf.MarkDispatchedInOrder(1);
        Assert.True(buf.TryDequeueNext(out var id, out var p));
        Assert.Equal(2, id);
        Assert.Equal("fresh-two", p!.Tag);
    }

    [Fact]
    public void TryDequeueNext_EmptyBuffer_ReturnsFalse()
    {
        var buf = NewBuffer();
        Assert.False(buf.TryDequeueNext(out _, out _));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new RpcOrderingBuffer<Item>(0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new RpcOrderingBuffer<Item>(-1));
    }

    // 2026-05-27: dropped Constraint `where T : class` and the corresponding null-throw
    // guard so the buffer can hold struct StashedRpcCall. Null callers on a class T param
    // just get a null payload entry — caller-side discipline.
}
