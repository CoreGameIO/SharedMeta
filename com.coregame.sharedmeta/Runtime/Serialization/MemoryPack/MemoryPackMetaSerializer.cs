using System;
using System.Collections.Generic;
using System.IO;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Serialization.MemoryPack
{
    /// <summary>
    /// Writes multiple values into a MemoryPack payload.
    /// Each value is prefixed with its length for sequential reading.
    /// </summary>
    public class MemoryPackPayloadWriter : IPayloadWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        private bool _completed;

        public MemoryPackPayloadWriter()
        {
            _writer = new BinaryWriter(_stream);
        }

        public void Write<T>(T value)
        {
            if (_completed) throw new InvalidOperationException("Writer already completed.");

            var bytes = MemoryPackSerializer.Serialize(value);
            _writer.Write(bytes.Length);  // Length prefix
            _writer.Write(bytes);          // Data
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
    /// Reads values from a MemoryPack payload in order.
    /// </summary>
    public class MemoryPackPayloadReader : IPayloadReader
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;

        public MemoryPackPayloadReader(byte[] data)
        {
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

        public byte[] Pack<T>(T value)
        {
            return MemoryPackSerializer.Serialize(value);
        }

        public T Unpack<T>(byte[] data)
        {
            return MemoryPackSerializer.Deserialize<T>(data)!;
        }

        public byte[] Pack(Type type, object value)
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
