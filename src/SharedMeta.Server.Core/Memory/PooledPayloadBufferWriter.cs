using System;
using System.Buffers;
using SharedMeta.Core.Memory;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// <see cref="IBufferWriter{T}"/> over a slot in a <see cref="PooledPayloadRegistry"/>.
    /// Lets a serializer (MemoryPack, MessagePack) write directly into the pool-rented buffer
    /// without any intermediate <c>byte[]</c> allocation.
    /// <para>
    /// Lifetime: caller acquires the writer via <see cref="PooledPayloadRegistry.AcquireWriter"/>,
    /// writes through <see cref="IBufferWriter{T}"/>, then either calls <see cref="Complete"/>
    /// to obtain the <see cref="PooledPayload"/> (transferring ownership of the slot's 1 ref to
    /// the returned payload) or <see cref="Dispose"/> to release the slot (used when Complete
    /// was never reached, e.g. an exception during serialization).
    /// </para>
    /// </summary>
    public sealed class PooledPayloadBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly PooledPayloadRegistry _registry;
        private readonly int _slotIndex;
        private int _written;
        private bool _completed;
        private bool _disposed;

        internal PooledPayloadBufferWriter(PooledPayloadRegistry registry, int slotIndex)
        {
            _registry = registry;
            _slotIndex = slotIndex;
        }

        /// <summary>Bytes written so far (not yet finalized).</summary>
        public int WrittenCount => _written;

        public void Advance(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (_completed) throw new InvalidOperationException("Writer already completed.");
            ref var slot = ref _registry.SlotRef(_slotIndex);
            var buf = slot.Buffer ?? throw new InvalidOperationException("Slot buffer not allocated.");
            if (_written + count > buf.Length)
                throw new InvalidOperationException("Advance past end of buffer.");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            var buf = _registry.SlotRef(_slotIndex).Buffer!;
            return buf.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            var buf = _registry.SlotRef(_slotIndex).Buffer!;
            return buf.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (_completed) throw new InvalidOperationException("Writer already completed.");
            if (sizeHint <= 0) sizeHint = 1;
            ref var slot = ref _registry.SlotRef(_slotIndex);
            var buf = slot.Buffer ?? throw new InvalidOperationException("Slot buffer not allocated.");
            int available = buf.Length - _written;
            if (sizeHint > available)
                _registry.GrowSlotBuffer(_slotIndex, _written + sizeHint, bytesToPreserve: _written);
        }

        /// <summary>
        /// Finalize the slot length and transfer ownership of the slot's 1 ref to the returned
        /// <see cref="PooledPayload"/>. After this call <see cref="Dispose"/> is a no-op.
        /// </summary>
        public PooledPayload Complete()
        {
            if (_completed) throw new InvalidOperationException("Writer already completed.");
            _completed = true;
            _registry.SetSlotLength(_slotIndex, _written);
            var buf = _registry.SlotRef(_slotIndex).Buffer!;
            var refId = _registry.MakeRef(_slotIndex);
            return new PooledPayload(buf.AsMemory(0, _written), refId);
        }

        /// <summary>
        /// Releases the slot if <see cref="Complete"/> was not called. After Complete this is
        /// a no-op — the returned payload owns the ref-count.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_completed)
            {
                // Release the initial ref so the slot's buffer is recycled.
                _registry.Release(new PooledPayload(default, _registry.MakeRef(_slotIndex)));
            }
        }
    }
}
