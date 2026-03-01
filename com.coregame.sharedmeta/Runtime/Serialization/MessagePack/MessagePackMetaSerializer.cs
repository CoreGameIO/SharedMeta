using System;
using System.IO;
using MessagePack;
using SharedMeta.Core;

namespace SharedMeta.Serialization.MessagePack
{
    /// <summary>
    /// Writes multiple values into a MessagePack payload.
    /// Each value is prefixed with its length for sequential reading.
    /// </summary>
    public class MessagePackPayloadWriter : IPayloadWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private bool _completed;

        public MessagePackPayloadWriter()
        {
            _writer = new BinaryWriter(_stream);
        }

        public void Write<T>(T value)
        {
            if (_completed) throw new InvalidOperationException("Writer already completed.");

            var bytes = MessagePackSerializer.Serialize(value, MetaMessagePackOptions.Instance);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
        }

        public byte[] Complete()
        {
            _completed = true;
            _writer.Flush();
            return _stream.ToArray();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Reads values from a MessagePack payload in order.
    /// </summary>
    public class MessagePackPayloadReader : IPayloadReader
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;

        public MessagePackPayloadReader(byte[] data)
        {
            _stream = new MemoryStream(data);
            _reader = new BinaryReader(_stream);
        }

        public T Read<T>()
        {
            var length = _reader.ReadInt32();
            var bytes = _reader.ReadBytes(length);
            return MessagePackSerializer.Deserialize<T>(bytes, MetaMessagePackOptions.Instance)!;
        }

        public byte[] ReadRaw()
        {
            var length = _reader.ReadInt32();
            return _reader.ReadBytes(length);
        }

        public bool HasMore => _stream.Position < _stream.Length;

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// MessagePack implementation of IMetaSerializer.
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
