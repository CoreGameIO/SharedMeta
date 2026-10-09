using System;
using Newtonsoft.Json;

namespace SharedMeta.Client.Network
{
    /// <summary>
    /// Newtonsoft.Json converter for <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/>.
    /// <para>
    /// Required since wire DTOs moved from <c>byte[]</c> to <c>ReadOnlyMemory&lt;byte&gt;</c>:
    /// Newtonsoft has no built-in support for ROM, and would otherwise serialize the struct's
    /// fields as a JSON object — the server's <c>System.Text.Json</c> deserializer expects a
    /// base64 string (matching STJ's built-in <c>ReadOnlyMemoryByteConverter</c> in .NET 8+).
    /// This converter mirrors STJ's wire shape on both sides.
    /// </para>
    /// </summary>
    public sealed class RomByteJsonConverter : JsonConverter<ReadOnlyMemory<byte>>
    {
        public override void WriteJson(JsonWriter writer, ReadOnlyMemory<byte> value, JsonSerializer serializer)
        {
            if (value.IsEmpty)
            {
                writer.WriteValue(string.Empty);
                return;
            }
            // Allocates a base64 string and one byte[] for Span.ToArray() — acceptable on a
            // JSON wire (Newtonsoft is already string-heavy); MemoryPack/MessagePack stay on
            // the zero-alloc bin tag path.
            writer.WriteValue(Convert.ToBase64String(value.Span));
        }

        public override ReadOnlyMemory<byte> ReadJson(JsonReader reader, Type objectType, ReadOnlyMemory<byte> existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return default;
            if (reader.TokenType == JsonToken.Bytes)
            {
                // Newtonsoft already decoded base64 → byte[] for us.
                return (byte[]?)reader.Value ?? Array.Empty<byte>();
            }
            var s = reader.Value as string;
            if (string.IsNullOrEmpty(s)) return default;
            return Convert.FromBase64String(s);
        }
    }
}
