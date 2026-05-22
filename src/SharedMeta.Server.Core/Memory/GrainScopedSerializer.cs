using System;
using System.Buffers;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// IMetaSerializer wrapper that writes <see cref="Pack{T}(T)"/> results into an internal
    /// <see cref="ScratchBufferPool"/> and returns slices over that pool. Used as the
    /// per-grain <c>Context.Serializer</c>: callers keep doing <c>Context.Serializer.Pack(x)</c>
    /// but the result is a ROM slice over the scratch buffer — zero <c>byte[]</c> allocation
    /// per Pack call.
    /// <para>
    /// <b>Lifetime contract:</b> the returned <see cref="ReadOnlyMemory{T}"/> is valid until
    /// <see cref="ResetScratch"/> is called (typically at the entry of each
    /// <c>Handle*Async</c> on the grain). Callers that need the bytes past that point
    /// must copy via <c>.ToArray()</c> — same discipline already documented on
    /// <see cref="IMetaSerializer.Pack{T}(T)"/>.
    /// </para>
    /// <para>
    /// Multi-value writers (<see cref="CreateWriter"/>), <see cref="Unpack{T}(byte[])"/>,
    /// runtime <see cref="Pack(Type, object)"/>, RpcCall codecs and <see cref="Clone{T}"/>
    /// delegate to the inner codec unchanged — those paths either don't need scratch (Unpack)
    /// or already have their own ArrayPool-rented buffers (multi-value writer).
    /// </para>
    /// </summary>
    public sealed class GrainScopedSerializer : IMetaSerializer
    {
        private readonly IMetaSerializer _inner;
        private readonly ScratchBufferPool _pool;

        public GrainScopedSerializer(IMetaSerializer inner, ScratchBufferPool pool)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        /// <summary>Reset the scratch pool — invalidates all ROMs previously returned by
        /// <see cref="Pack{T}(T)"/>. Call at the start of each grain method (HandleCallAsync,
        /// HandleExternalEventAsync, SubscribeAsync) so the next call starts at offset 0.</summary>
        public void ResetScratch() => _pool.Reset();

        // ── Hot path: zero-alloc Pack via scratch + IBufferWriter overload ──

        public ReadOnlyMemory<byte> Pack<T>(T value)
        {
            var writer = new ScratchBufferWriter(_pool);
            _inner.Pack(value, writer);
            return writer.WrittenMemory;
        }

        public void Pack<T>(T value, IBufferWriter<byte> writer) => _inner.Pack(value, writer);

        public ReadOnlyMemory<byte> Pack(Type type, object value)
        {
            // Runtime-typed Pack is reflection-heavy and rarer than Pack<T>. The inner
            // serializer's Pack(Type, object) already allocates byte[] internally; we
            // forward and wrap as ROM rather than open a writer route (which would
            // require boxing through TPayloadWriter generic dispatch).
            return _inner.Pack(type, value);
        }

        // ── Delegated codec paths ──

        public IPayloadWriter CreateWriter() => _inner.CreateWriter();
        public IPayloadReader CreateReader(byte[] data) => _inner.CreateReader(data);
        public IPayloadReader CreateReader(ReadOnlyMemory<byte> data) => _inner.CreateReader(data);

        public T Unpack<T>(byte[] data) => _inner.Unpack<T>(data);
        public T Unpack<T>(ReadOnlyMemory<byte> data) => _inner.Unpack<T>(data);
        public object? Unpack(Type type, byte[] data) => _inner.Unpack(type, data);

        public byte[] SerializeRpcCall(RpcCall call) => _inner.SerializeRpcCall(call);
        public RpcCall DeserializeRpcCall(byte[] data) => _inner.DeserializeRpcCall(data);

        public T Clone<T>(T value) => _inner.Clone(value);
    }
}
