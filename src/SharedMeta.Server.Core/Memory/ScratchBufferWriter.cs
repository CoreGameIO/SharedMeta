using System;
using System.Buffers;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// Provider-owned growable scratch buffer for INTERMEDIATE serializations within a single
    /// Handle*Async invocation. The provider holds one <see cref="ScratchBufferPool"/>; each
    /// intermediate serialization (recorder replay, patch tree, state snapshot, per-trigger
    /// replay/patch) creates a <see cref="ScratchBufferWriter"/> that captures the pool's
    /// current backing byte[] and tail offset, writes through <see cref="IBufferWriter{T}"/>,
    /// and exposes the produced slice via <see cref="WrittenMemory"/>.
    /// <para>
    /// Growth policy: if a writer needs more space than the current backing has, a brand-new
    /// byte[] is allocated (size = max(current*2, written + sizeHint)) and the bytes written
    /// SO FAR by this writer are copied into it. The pool's <c>Buffer</c> field is updated to
    /// point at the new array, but the OLD backing stays alive through any
    /// <see cref="ReadOnlyMemory{T}"/> slices that earlier writers in the same call already
    /// handed out — GC reclaims it once those slices fall out of scope (typically at the
    /// start of the next Handle*Async, when the embedding response/broadcast has been
    /// serialized and shipped).
    /// </para>
    /// <para>
    /// Reset between calls: provider sets <see cref="ScratchBufferPool.Offset"/> back to 0
    /// at the start of each call. The backing byte[] is kept (warmed-up size). The pool is
    /// not returned to <see cref="ArrayPool{T}.Shared"/> — it stays alive for the grain's
    /// lifetime and is dropped to GC at grain deactivation along with the provider itself.
    /// </para>
    /// </summary>
    public sealed class ScratchBufferPool
    {
        public byte[] Buffer;
        public int Offset;

        public ScratchBufferPool(int initialCapacity = 1024)
        {
            if (initialCapacity <= 0) initialCapacity = 1024;
            Buffer = new byte[initialCapacity];
        }

        /// <summary>Rewind tail to zero for the start of a new Handle*Async invocation.
        /// Any outstanding ROMs from the previous call must already have been embedded
        /// into an outgoing PooledPayload — otherwise this reset trashes their content.</summary>
        public void Reset() => Offset = 0;
    }

    public sealed class ScratchBufferWriter : IBufferWriter<byte>
    {
        private readonly ScratchBufferPool _pool;
        // The byte[] this writer is writing into. Initially captured from pool.Buffer;
        // may switch to a private new[] after a grow (pool also moves to the new buffer).
        private byte[] _backing;
        // Offset within _backing where this writer's slice starts.
        private int _start;
        // Bytes written by this writer.
        private int _written;

        public ScratchBufferWriter(ScratchBufferPool pool)
        {
            _pool = pool;
            _backing = pool.Buffer;
            _start = pool.Offset;
        }

        /// <summary>Slice of the backing byte[] containing the bytes written so far. Valid
        /// until the provider resets the pool at the start of a subsequent Handle*Async call.</summary>
        public ReadOnlyMemory<byte> WrittenMemory => _backing.AsMemory(_start, _written);

        public int WrittenCount => _written;

        public void Advance(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (_start + _written + count > _backing.Length)
                throw new InvalidOperationException("Advance past end of scratch backing.");
            _written += count;
            // Only update pool tail when our backing still matches the pool's — after a
            // grow we own a private byte[]; the pool already moved on to it via Grow().
            if (ReferenceEquals(_backing, _pool.Buffer))
                _pool.Offset = _start + _written;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _backing.AsMemory(_start + _written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _backing.AsSpan(_start + _written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint <= 0) sizeHint = 1;
            int needed = _start + _written + sizeHint;
            if (needed <= _backing.Length) return;

            // Allocate a fresh byte[] sized to cover the request and double the previous
            // backing. Copy the bytes we've already written so this writer's WrittenMemory
            // stays contiguous; previously-issued ROMs by OTHER writers in this call keep
            // the OLD backing alive (GC retains it as long as those ROMs hold the reference).
            int newSize = Math.Max(_backing.Length * 2, _written + sizeHint);
            var newBuf = new byte[newSize];
            if (_written > 0)
                Buffer.BlockCopy(_backing, _start, newBuf, 0, _written);
            _backing = newBuf;
            _start = 0;
            // Hand the new backing to the pool so subsequent writers in this same call
            // start from just after our slice.
            _pool.Buffer = newBuf;
            _pool.Offset = _written;
        }
    }
}
