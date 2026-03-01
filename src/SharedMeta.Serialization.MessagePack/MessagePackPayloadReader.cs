using System;
using System.IO;
using MessagePack;
using SharedMeta.Core;

namespace SharedMeta.Serialization.MessagePack
{
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
}
