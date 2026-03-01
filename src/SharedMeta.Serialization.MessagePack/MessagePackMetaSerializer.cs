using System;
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

        public byte[] Pack<T>(T value)
            => MessagePackSerializer.Serialize(value, Options);

        public T Unpack<T>(byte[] data)
            => MessagePackSerializer.Deserialize<T>(data, Options)!;

        public byte[] Pack(Type type, object value)
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
