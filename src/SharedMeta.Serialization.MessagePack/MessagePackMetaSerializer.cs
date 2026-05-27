using System;
using System.Buffers;
using MessagePack;
using SharedMeta.Core;

namespace SharedMeta.Serialization.MessagePack
{
    /// <summary>
    /// MessagePack implementation of IMetaSerializer.
    /// Uses OrleansIdResolver to handle types with [Id(n)] attributes.
    /// Drop-in replacement for MemoryPackMetaSerializer.
    /// </summary>
    public class MessagePackMetaSerializer : IMetaSerializer
    {
        private static MessagePackSerializerOptions Options => MetaMessagePackOptions.Instance;

        public IPayloadWriter CreateWriter() => new MessagePackPayloadWriter();

        public IPayloadReader CreateReader(byte[] data) => new MessagePackPayloadReader(data);
        public IPayloadReader CreateReader(ReadOnlyMemory<byte> data) => new MessagePackPayloadReader(data.ToArray());

        public ReadOnlyMemory<byte> Pack<T>(T value)
            => MessagePackSerializer.Serialize(value, Options);

        public byte[]  PackForExternalUsage<T>(T value)
            => MessagePackSerializer.Serialize(value, Options);

        public T Unpack<T>(byte[] data)
            => MessagePackSerializer.Deserialize<T>(data, Options)!;

        // MessagePack-CSharp supports IBufferWriter<byte> natively, but we go through
        // a transient byte[] copy here because the wider plan accepts a single alloc on
        // the MessagePack path (see plan §"Wire breaking" — agreed trade-off vs. wiring
        // custom MessagePackWriter into Unity-side stubs).
        public void Pack<T>(T value, IBufferWriter<byte> writer)
        {
            var bytes = MessagePackSerializer.Serialize(value, Options);
            var span = writer.GetSpan(bytes.Length);
            bytes.AsSpan().CopyTo(span);
            writer.Advance(bytes.Length);
        }

        public T Unpack<T>(ReadOnlyMemory<byte> data)
            => MessagePackSerializer.Deserialize<T>(data, Options)!;

        public T Unpack<T>(ReadOnlySpan<byte> data)
        {
            // MessagePack-CSharp's primary entry takes ROM/ReadOnlySequence. Wrap via ToArray —
            // the Span overload's main win is on MemoryPack projects; MessagePack callers
            // already pay a higher codec overhead per call.
            return MessagePackSerializer.Deserialize<T>(data.ToArray(), Options)!;
        }

        public ReadOnlyMemory<byte> Pack(Type type, object value)
            => MessagePackSerializer.Serialize(type, value, Options);

        public object? Unpack(Type type, byte[] data)
            => MessagePackSerializer.Deserialize(type, new ReadOnlyMemory<byte>(data), Options);

        /// <summary>
        /// RpcCall has [Id(0-8)] attributes — OrleansIdResolver handles it directly.
        /// No wrapper DTO needed (unlike MemoryPack which requires [MemoryPackable]).
        /// </summary>
        public byte[] SerializeRpcCall(RpcCall call)
            => MessagePackSerializer.Serialize(call, Options);

        public RpcCall DeserializeRpcCall(byte[] data)
            => MessagePackSerializer.Deserialize<RpcCall>(data, Options)!;

        public T Clone<T>(T value)
        {
            var bytes = MessagePackSerializer.Serialize(value, Options);
            return MessagePackSerializer.Deserialize<T>(bytes, Options)!;
        }
    }
}
