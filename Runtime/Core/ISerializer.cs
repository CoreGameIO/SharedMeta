using System;
using System.Buffers;

namespace SharedMeta.Core
{
    /// <summary>
    /// Writes multiple values into a payload sequentially. Backed by a pool-rented buffer
    /// where the implementation supports it (MemoryPack); MessagePack falls back to its
    /// own MemoryStream and copies once on <see cref="Complete"/>.
    /// <para>
    /// Two lifecycle modes:
    /// <list type="bullet">
    ///   <item><b>Pooled (cached):</b> container owns the writer, calls <see cref="Reset"/>
    ///     between uses. <see cref="IDisposable.Dispose"/> is a no-op. The
    ///     <see cref="ReadOnlyMemory{T}"/> returned by <see cref="Complete"/> stays valid
    ///     until the NEXT <see cref="Reset"/>. Typical: <c>IServerRecordContext.AcquireWriter()</c>.</item>
    ///   <item><b>Transient:</b> caller owns via <c>using</c>. <see cref="IDisposable.Dispose"/>
    ///     returns the pool buffer to <see cref="ArrayPool{T}"/>. ROM lifetime = until Dispose.
    ///     Typical: ad-hoc test code, one-shot scripts.</item>
    /// </list>
    /// </para>
    /// </summary>
    public interface IPayloadWriter : IDisposable
    {
        /// <summary>
        /// Write a value to the payload.
        /// </summary>
        void Write<T>(T value);

        /// <summary>
        /// Finalize and return the completed payload as ROM over the writer's internal buffer.
        /// ROM is valid until the next <see cref="Reset"/> or <see cref="IDisposable.Dispose"/>
        /// — caller MUST consume (or <c>.ToArray()</c>) before either is called.
        /// </summary>
        ReadOnlyMemory<byte> Complete();

        /// <summary>
        /// Re-arm the writer for next use. Cheap; keeps the existing pool buffer.
        /// Cached/pooled writers call this between sequential <see cref="Complete"/>+use cycles.
        /// </summary>
        void Reset();
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
        /// Caller MUST <c>.ToArray()</c> if it needs to outlive the current invocation —
        /// or use <see cref="PackForExternalUsage{T}"/> which encodes that intent in one call.
        /// </summary>
        ReadOnlyMemory<byte> Pack<T>(T value);

        /// <summary>
        /// Serialize one value AS A GRAIN-EXIT PAYLOAD — returns a fresh GC-managed
        /// <c>byte[]</c> whose lifetime is independent of any per-grain scratch buffer.
        /// Use at every site where the result crosses an Orleans grain boundary, lands in
        /// a wire DTO, or is otherwise observed past the current grain method.
        /// <para>
        /// Return type is <c>byte[]</c> (not <see cref="ReadOnlyMemory{T}"/>) so callers
        /// that need a byte array don't pay a second <c>.ToArray()</c> copy — and so the
        /// "independent allocation" contract is encoded in the method signature itself.
        /// </para>
        /// </summary>
        byte[] PackForExternalUsage<T>(T value);

        T Unpack<T>(byte[] data);

        // Pool-friendly single-value overloads. Implementations should write directly into
        // the supplied IBufferWriter<byte> (no transient byte[] allocation) where the
        // underlying codec supports it (e.g. MemoryPack via Serialize<T,TWriter>); fallback
        // implementations may serialize to byte[] and copy. Unpack<T>(ReadOnlyMemory<byte>)
        // lets callers feed pool-backed slices without converting to byte[].
        void Pack<T>(T value, IBufferWriter<byte> writer);
        T Unpack<T>(ReadOnlyMemory<byte> data);

        // Span overload — for sync codec sites where the source is already a span
        // (e.g. generated server dispatcher reads payload.Span). Saves one Span accessor
        // hop relative to the ROM overload. Cannot cross await, ref-struct-clean.
        T Unpack<T>(ReadOnlySpan<byte> data);

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
