using System;
using System.Buffers;

namespace SharedMeta.Core
{
    /// <summary>
    /// Writes multiple values into a payload sequentially.
    /// </summary>
    public interface IPayloadWriter : IDisposable
    {
        /// <summary>
        /// Write a value to the payload.
        /// </summary>
        void Write<T>(T value);

        /// <summary>
        /// Finalize and return the completed payload as bytes.
        /// </summary>
        byte[] Complete();

        /// <summary>
        /// True when the writer can transfer its internal pool-rented buffer to the caller
        /// via <see cref="CompleteAsRented"/> without copying. Implementations that don't
        /// pool their working buffer (e.g. MessagePack's MemoryStream-backed writer) return
        /// false and callers must fall back to <see cref="Complete"/>.
        /// </summary>
        bool SupportsRentedComplete { get; }

        /// <summary>
        /// Finalize and transfer ownership of the writer's internal buffer to the caller.
        /// When <see cref="SupportsRentedComplete"/> is true, the returned <paramref name="buffer"/>
        /// was rented from <see cref="ArrayPool{T}.Shared"/> and the caller is responsible for
        /// returning it (either directly or via <c>PooledPayloadRegistry.AcquireExisting</c>
        /// + <c>Release</c>). After this call, <see cref="IDisposable.Dispose"/> is a no-op —
        /// the writer no longer owns a buffer.
        /// </summary>
        void CompleteAsRented(out byte[] buffer, out int length);
    }

    /// <summary>
    /// Reads values from a payload in order.
    /// </summary>
    public interface IPayloadReader : IDisposable
    {
        /// <summary>
        /// Read the next value of type T.
        /// </summary>
        T Read<T>();

        /// <summary>
        /// Read the next value as raw bytes (for deferred deserialization).
        /// Used by argument transformers to read boxed values.
        /// </summary>
        byte[] ReadRaw();

        /// <summary>
        /// True if more values remain to be read.
        /// </summary>
        bool HasMore { get; }
    }

    /// <summary>
    /// Universal serializer supporting multi-value payloads.
    /// <para>
    /// <c>Pack&lt;T&gt;(T)</c> returns <see cref="ReadOnlyMemory{T}"/> instead of <c>byte[]</c>
    /// so implementations can choose ownership: a stock codec returns a freshly-allocated
    /// <c>byte[]</c> wrapped as ROM (GC-managed, lifetime = ever), a grain-scoped variant
    /// writes into an internal scratch buffer and returns a slice (lifetime = until the next
    /// scratch reset). Callers that need actual array ownership do <c>.ToArray()</c> at the
    /// boundary where ownership matters (persistence, cross-grain hop without pooled payload).
    /// </para>
    /// </summary>
    public interface IMetaSerializer
    {
        // Multi-value operations
        IPayloadWriter CreateWriter();
        IPayloadReader CreateReader(byte[] data);
        IPayloadReader CreateReader(ReadOnlyMemory<byte> data);

        /// <summary>
        /// Serialize one value. Returned <see cref="ReadOnlyMemory{T}"/> lifetime is
        /// implementation-defined: stock codec returns ROM over a fresh GC-managed byte[];
        /// grain-scoped serializer returns ROM over its internal scratch buffer, valid only
        /// until the next <c>ResetScratch</c> (called at the start of each Handle*Async).
        /// Caller MUST <c>.ToArray()</c> if it needs to outlive the current invocation.
        /// </summary>
        ReadOnlyMemory<byte> Pack<T>(T value);

        T Unpack<T>(byte[] data);

        // Pool-friendly single-value overloads. Implementations should write directly into
        // the supplied IBufferWriter<byte> (no transient byte[] allocation) where the
        // underlying codec supports it (e.g. MemoryPack via Serialize<T,TWriter>); fallback
        // implementations may serialize to byte[] and copy. Unpack<T>(ReadOnlyMemory<byte>)
        // lets callers feed pool-backed slices without converting to byte[].
        void Pack<T>(T value, IBufferWriter<byte> writer);
        T Unpack<T>(ReadOnlyMemory<byte> data);

        // Runtime type support (for reflection scenarios). Same ROM contract as Pack<T>(T).
        ReadOnlyMemory<byte> Pack(Type type, object value);
        object? Unpack(Type type, byte[] data);

        // RpcCall serialization
        byte[] SerializeRpcCall(RpcCall call);
        RpcCall DeserializeRpcCall(byte[] data);

        // Deep copy via serialization
        T Clone<T>(T value);
    }
}
