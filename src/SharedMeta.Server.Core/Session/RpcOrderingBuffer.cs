using System;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Fixed-capacity ring buffer that holds out-of-order RPC payloads keyed by their
    /// monotonically increasing <c>RequestId</c>. Used by <see cref="SessionManagerGrain"/>
    /// to defer execution of requests that arrived ahead of their predecessors until the
    /// gap is filled.
    ///
    /// <para>
    /// Indexing is O(1) — the slot for <c>requestId = LastDispatchedRequestId + 1 + offset</c>
    /// lives at <c>(_head + offset) % Capacity</c>. Both insertion and head dequeue are
    /// constant-time, and the buffer never allocates after construction.
    /// </para>
    ///
    /// <para>
    /// Not thread-safe — designed for use inside a single Orleans grain activation, which
    /// already provides single-threaded access.
    /// </para>
    /// </summary>
    /// <typeparam name="T">
    /// Payload type. Typically a small DTO carrying the original RPC call and any
    /// per-request metadata the caller needs to replay later.
    /// </typeparam>
    public sealed class RpcOrderingBuffer<T> where T : class
    {
        private struct Slot
        {
            public bool HasValue;
            public long RequestId;
            public T? Payload;
        }

        private readonly Slot[] _slots;
        private int _head;
        private int _count;
        private long _lastDispatchedRequestId;

        /// <summary>
        /// Total ring capacity. Calls beyond <see cref="LastDispatchedRequestId"/> + Capacity
        /// cannot be stashed and will return <see cref="StashResult.Overflow"/>.
        /// </summary>
        public int Capacity => _slots.Length;

        /// <summary>Number of currently parked calls.</summary>
        public int Count => _count;

        /// <summary>True when no calls are parked.</summary>
        public bool IsEmpty => _count == 0;

        /// <summary>The highest <c>RequestId</c> that has been dispatched (drained or processed in-order).</summary>
        public long LastDispatchedRequestId => _lastDispatchedRequestId;

        /// <summary>The next <c>RequestId</c> the buffer is willing to dispatch in-line.</summary>
        public long NextExpectedRequestId => _lastDispatchedRequestId + 1;

        public RpcOrderingBuffer(int capacity)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");
            _slots = new Slot[capacity];
        }

        /// <summary>
        /// Classify <paramref name="requestId"/> against the current dispatch baseline.
        /// Pure query — does not mutate state.
        /// </summary>
        public RequestPosition Classify(long requestId)
        {
            if (requestId <= _lastDispatchedRequestId) return RequestPosition.Stale;
            if (requestId == _lastDispatchedRequestId + 1) return RequestPosition.NextExpected;
            return RequestPosition.OutOfOrder;
        }

        /// <summary>
        /// Park an out-of-order call. The caller is expected to have already classified
        /// <paramref name="requestId"/> as <see cref="RequestPosition.OutOfOrder"/>; passing
        /// other positions either no-ops (Stale) or returns <see cref="StashResult.Stashed"/>
        /// without actually parking (NextExpected — but the caller should not stash those).
        /// </summary>
        public StashResult TryStash(long requestId, T payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            long offset = requestId - (_lastDispatchedRequestId + 1);
            if (offset < 0)
                return StashResult.Stale;
            if (offset >= _slots.Length)
                return StashResult.Overflow;

            int slotIndex = (_head + (int)offset) % _slots.Length;
            ref var slot = ref _slots[slotIndex];
            if (slot.HasValue && slot.RequestId == requestId)
                return StashResult.Duplicate;

            slot.HasValue = true;
            slot.RequestId = requestId;
            slot.Payload = payload;
            _count++;
            return StashResult.Stashed;
        }

        /// <summary>
        /// If the head slot holds the next expected <c>RequestId</c>, pop it and advance
        /// both the head and <see cref="LastDispatchedRequestId"/>. Returns false if the
        /// head is empty (gap not yet filled) or the buffer is empty.
        /// </summary>
        public bool TryDequeueNext(out long requestId, out T? payload)
        {
            if (_count == 0)
            {
                requestId = 0;
                payload = null;
                return false;
            }

            ref var slot = ref _slots[_head];
            if (!slot.HasValue || slot.RequestId != _lastDispatchedRequestId + 1)
            {
                requestId = 0;
                payload = null;
                return false;
            }

            requestId = slot.RequestId;
            payload = slot.Payload;

            slot.HasValue = false;
            slot.RequestId = 0;
            slot.Payload = null;
            _count--;
            _head = (_head + 1) % _slots.Length;
            _lastDispatchedRequestId = requestId;
            return true;
        }

        /// <summary>
        /// Mark <paramref name="requestId"/> as dispatched in-line (not via the stash) and
        /// advance the head past its conceptual slot. The caller must have observed that
        /// <see cref="Classify"/> returned <see cref="RequestPosition.NextExpected"/> for
        /// this <paramref name="requestId"/>; mismatched calls are silently ignored to
        /// keep the buffer self-consistent under unexpected control flow.
        /// </summary>
        public void MarkDispatchedInOrder(long requestId)
        {
            if (requestId != _lastDispatchedRequestId + 1)
                return;

            _lastDispatchedRequestId = requestId;
            _head = (_head + 1) % _slots.Length;
            // The slot at the old head was conceptually the home of this requestId, but
            // since the call arrived in-order it was never populated — nothing to clear.
        }

        /// <summary>
        /// Drop all parked calls and reset the dispatch baseline to zero. Used when the
        /// containing session is reset (supersede / disconnect / hard terminate).
        /// </summary>
        public void Reset()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i].HasValue = false;
                _slots[i].Payload = null;
                _slots[i].RequestId = 0;
            }
            _head = 0;
            _count = 0;
            _lastDispatchedRequestId = 0;
        }
    }

    /// <summary>
    /// Position of a <c>RequestId</c> relative to the buffer's dispatch baseline.
    /// </summary>
    public enum RequestPosition
    {
        /// <summary>Already dispatched (resend / late duplicate).</summary>
        Stale,

        /// <summary>Exactly the next expected request — safe to process in-line.</summary>
        NextExpected,

        /// <summary>Future request — must be stashed until predecessors arrive.</summary>
        OutOfOrder,
    }

    /// <summary>
    /// Outcome of <see cref="RpcOrderingBuffer{T}.TryStash"/>.
    /// </summary>
    public enum StashResult
    {
        /// <summary>The call was successfully parked.</summary>
        Stashed,

        /// <summary>An entry with the same <c>RequestId</c> was already parked. Caller may log.</summary>
        Duplicate,

        /// <summary>
        /// The call's offset from <c>NextExpectedRequestId</c> exceeds capacity. The buffer
        /// has not been mutated; the caller should treat this as a fatal stall and terminate
        /// the session.
        /// </summary>
        Overflow,

        /// <summary>
        /// The call is older than <c>LastDispatchedRequestId</c> (resend / duplicate that
        /// missed the idempotency cache). The buffer has not been mutated.
        /// </summary>
        Stale,
    }
}
