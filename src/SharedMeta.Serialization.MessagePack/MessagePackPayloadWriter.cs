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
        private MemoryStream _stream = new();
        private BinaryWriter _writer;
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

        public ReadOnlyMemory<byte> Complete()
        {
            _completed = true;
            _writer.Flush();
            // MemoryStream.GetBuffer exposes the underlying byte[] — slice by Position to
            // produce ROM over the actual content (no copy). ROM is valid until next
            // Reset() or Dispose().
            return new ReadOnlyMemory<byte>(_stream.GetBuffer(), 0, (int)_stream.Position);
        }

        public void Reset()
        {
            _stream.SetLength(0);
            _stream.Position = 0;
            _completed = false;
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}
