using System;
using System.Buffers;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// Server-side wrapper around <see cref="IMetaSerializer"/>. Backs
    /// <see cref="Pack{T}(T)"/> with a per-grain <see cref="ScratchBufferPool"/> +
    /// reusable <see cref="ScratchBufferWriter"/> — zero <c>byte[]</c> allocation per Pack
    /// call inside the grain.
    /// <para>
    /// <b>Lifetime contract — strict:</b> the returned <see cref="ReadOnlyMemory{T}"/> is
    /// valid only until <see cref="ResetScratch"/> runs at the next grain method entry. ROMs
    /// that MUST live past that point — anything crossing the Orleans grain boundary — must
    /// be copied at the boundary with <c>.ToArray()</c>. Known escape points already handled:
    /// <see cref="MetaProviderBase{TState}.PackBytes"/>, GetEntityStateAsync, generated
    /// dispatcher ResultBytes (see ServerDispatcherGenerator emit). Adding a new return-path
    /// that hands a Pack-result outward MUST do the same copy.
    /// </para>
    /// </summary>
    public sealed class GrainScopedSerializer : IServerMetaSerializer
    {
        private readonly IMetaSerializer _inner;
        private readonly ScratchBufferPool _pool;
        // One writer reused across every Pack<T>(T) on this grain. WrittenMemory captured by
        // earlier callers stays valid as a struct snapshot of (array, start, length) — only
        // the writer's forward cursor moves on Reset(), the bytes are still alive at their
        // captured offsets until the next ResetScratch().
        private readonly ScratchBufferWriter _packWriter;

        public GrainScopedSerializer(IMetaSerializer inner, ScratchBufferPool pool)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _packWriter = new ScratchBufferWriter(pool);
        }

        /// <summary>Reset the scratch pool — invalidates all ROMs previously returned by
        /// <see cref="Pack{T}(T)"/>. Call at the start of each grain method.</summary>
        public void ResetScratch() => _pool.Reset();

        public ReadOnlyMemory<byte> Pack<T>(T value)
        {
            _packWriter.Reset();
            _inner.Pack(value, _packWriter);
            return _packWriter.WrittenMemory;
        }

        // Skip scratch entirely — caller has declared the result will cross the grain
        // boundary, so we delegate to the inner codec (which allocates a fresh byte[])
        // and return it directly. No scratch-then-ToArray double-buffer dance.
        public byte[] PackForExternalUsage<T>(T value) => _inner.PackForExternalUsage(value);

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
        public T Unpack<T>(ReadOnlySpan<byte> data) => _inner.Unpack<T>(data);
        public object? Unpack(Type type, byte[] data) => _inner.Unpack(type, data);

        public byte[] SerializeRpcCall(RpcCall call) => _inner.SerializeRpcCall(call);
        public RpcCall DeserializeRpcCall(byte[] data) => _inner.DeserializeRpcCall(data);

        public T Clone<T>(T value) => _inner.Clone(value);
    }
}
