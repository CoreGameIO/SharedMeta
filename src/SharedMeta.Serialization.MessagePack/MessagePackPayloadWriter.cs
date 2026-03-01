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
}
