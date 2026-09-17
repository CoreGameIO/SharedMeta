using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Serialization.MemoryPack
{
    /// <summary>
    /// Writes multiple values into a MemoryPack payload, length-prefixed for sequential reading.
    /// <para>
    /// Doubles as <see cref="IBufferWriter{T}"/> so MemoryPack writes directly into our
    /// pooled buffer. Older shape went through <c>MemoryPackSerializer.Serialize&lt;T&gt;(value)</c>
    /// (allocates a fresh <c>byte[]</c> via the internal <c>ReusableLinkedArrayBufferWriter</c>)
    /// then copied those bytes into a <c>MemoryStream</c> — two transient buffer allocations per
    /// value plus stream-growth churn. Now we rent one buffer from <see cref="ArrayPool{T}"/>,
    /// expand in place if needed, and snapshot once at <see cref="Complete"/>.
    /// </para>
    /// <para>
    /// Caller MUST <see cref="Dispose"/> after <see cref="Complete"/> so the pool buffer is
    /// returned. <c>ServerMetaContext.EndOperation</c> / <c>PopNestedOperation</c> own this.
    /// </para>
    /// </summary>
    public sealed class MemoryPackPayloadWriter : IPayloadWriter, IBufferWriter<byte>
    {
        private byte[]? _buffer;
        private int _index;
        private bool _completed;
        // When true, the writer is owned by a container (e.g. cached per-grain in
        // GrainScopedSerializer). Dispose becomes a no-op so the using statement at the
        // call site doesn't return the pool buffer; the container handles lifecycle.
        // Reset() re-arms state for the next use.
        private readonly bool _pooled;

        public MemoryPackPayloadWriter(int initialCapacity = 256, bool pooled = false)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _index = 0;
            _pooled = pooled;
        }

        /// <summary>Re-arm a pooled writer for reuse. Keeps the existing pool buffer if
        /// present (cheap), otherwise rents one of the requested capacity.</summary>
        public void Reset() => Reset(256);

        public void Reset(int initialCapacity)
        {
            if (_buffer == null)
                _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _index = 0;
            _completed = false;
        }

        // ── IBufferWriter<byte> — MemoryPack calls these directly ──
        public void Advance(int count) => _index += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer!.AsMemory(_index);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer!.AsSpan(_index);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint <= 0) sizeHint = 1;
            var available = _buffer!.Length - _index;
            if (sizeHint <= available) return;

            var newSize = Math.Max(_buffer.Length * 2, _index + sizeHint);
            var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _index).CopyTo(newBuffer);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
            _buffer = newBuffer;
        }

        // ── IPayloadWriter ──
        public void Write<T>(T value)
        {
            if (_completed) throw new InvalidOperationException("Writer already completed.");

            // Reserve 4 bytes for the length prefix; backfill after MemoryPack writes the value.
            // Grow BEFORE advancing: _index must never exceed the buffer, or the next growth
            // copies AsSpan(0, _index) out of range. Costs one write per payload to hit —
            // the prefix has to land in the final 3 bytes of the current buffer.
            var lengthStart = _index;
            EnsureCapacity(4);
            _index += 4;
            var contentStart = _index;

            MemoryPackSerializer.Serialize<T, MemoryPackPayloadWriter>(this, value);

            var bytesWritten = _index - contentStart;
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(lengthStart, 4), bytesWritten);
        }

        public ReadOnlyMemory<byte> Complete()
        {
            if (_buffer == null) throw new InvalidOperationException("Writer has no buffer (disposed or already taken).");
            _completed = true;
            // ROM over the pool-rented buffer, sliced to actual length. Lifetime:
            // valid until the next Reset() or Dispose(). Caller MUST consume / copy before then.
            return _buffer.AsMemory(0, _index);
        }

        public void Dispose()
        {
            // Pooled writer: container owns the lifecycle; using-statement Dispose is a no-op.
            // Reset() between uses; container drops to GC at grain deactivation.
            if (_pooled) return;
            if (_buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
                _buffer = null;
            }
        }
    }

    /// <summary>
    /// Reads values from a MemoryPack payload in order.
    /// </summary>
    public class MemoryPackPayloadReader : IPayloadReader
    {
        private MemoryStream _stream;
        private BinaryReader _reader;
        // Same lifecycle convention as MemoryPackPayloadWriter: when pooled, Dispose is a
        // no-op and Rebind() points the reader at a new payload without allocating a new
        // MemoryStream / BinaryReader pair.
        private readonly bool _pooled;

        public MemoryPackPayloadReader(byte[] data, bool pooled = false)
        {
            _stream = new MemoryStream(data);
            _reader = new BinaryReader(_stream);
            _pooled = pooled;
        }

        /// <summary>Re-point this reader at a new payload for reuse. Cheap: swaps the
        /// underlying MemoryStream's buffer in place via a fresh small wrapper. Only valid
        /// on pooled instances; throws otherwise.</summary>
        public void Rebind(byte[] data)
        {
            if (!_pooled) throw new InvalidOperationException("Rebind is only supported on pooled readers.");
            _reader.Dispose();
            _stream.Dispose();
            _stream = new MemoryStream(data);
            _reader = new BinaryReader(_stream);
        }

        public T Read<T>()
        {
            var length = _reader.ReadInt32();
            var bytes = _reader.ReadBytes(length);
            return MemoryPackSerializer.Deserialize<T>(bytes)!;
        }

        public byte[] ReadRaw()
        {
            var length = _reader.ReadInt32();
            return _reader.ReadBytes(length);
        }

        public bool HasMore => _stream.Position < _stream.Length;

        public void Dispose()
        {
            // Pooled reader: container owns lifecycle, using-statement Dispose is a no-op.
            if (_pooled) return;
            _reader.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// MemoryPack implementation of IMetaSerializer.
    /// All methods work directly with byte[] - no IPayload abstraction.
    /// </summary>
    public class MemoryPackMetaSerializer : IMetaSerializer
    {
        public IPayloadWriter CreateWriter() => new MemoryPackPayloadWriter();

        public IPayloadReader CreateReader(byte[] data) => new MemoryPackPayloadReader(data);
        public IPayloadReader CreateReader(ReadOnlyMemory<byte> data) =>
            // Multi-value reader path is non-MemoryPack-fast-path; copy once for the underlying
            // MemoryStream. MemoryPack zero-alloc dispatch goes through Deserialize(span) directly.
            new MemoryPackPayloadReader(data.ToArray());

        public ReadOnlyMemory<byte> Pack<T>(T value)
        {
            return MemoryPackSerializer.Serialize(value);
        }

        // Stock codec already allocates fresh on Pack<T>(T) — no separate "external usage"
        // path needed, just forward. Skips the default interface impl's redundant ToArray().
        public byte[] PackForExternalUsage<T>(T value)
        {
            return MemoryPackSerializer.Serialize(value);
        }

        public T Unpack<T>(byte[] data)
        {
            return MemoryPackSerializer.Deserialize<T>(data)!;
        }

        // Zero-alloc on the data path: MemoryPack writes directly into the caller's
        // IBufferWriter<byte> via the generic Serialize<T, TBufferWriter> overload.
        public void Pack<T>(T value, IBufferWriter<byte> writer)
        {
            MemoryPackSerializer.Serialize(in writer, value);
        }

        public T Unpack<T>(ReadOnlyMemory<byte> data)
        {
            return MemoryPackSerializer.Deserialize<T>(data.Span)!;
        }

        public T Unpack<T>(ReadOnlySpan<byte> data)
        {
            return MemoryPackSerializer.Deserialize<T>(data)!;
        }

        public ReadOnlyMemory<byte> Pack(Type type, object value)
        {
            return MemoryPackSerializer.Serialize(type, value);
        }

        public object? Unpack(Type type, byte[] data)
        {
            return MemoryPackSerializer.Deserialize(type, data);
        }

        public byte[] SerializeRpcCall(RpcCall call)
            => MemoryPackSerializer.Serialize(call);

        public RpcCall DeserializeRpcCall(byte[] data)
            => MemoryPackSerializer.Deserialize<RpcCall>(data)!;

        public T Clone<T>(T value)
        {
            var bytes = MemoryPackSerializer.Serialize(value);
            return MemoryPackSerializer.Deserialize<T>(bytes)!;
        }
    }
}
