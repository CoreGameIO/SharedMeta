using System;
using Newtonsoft.Json;

namespace SharedMeta.Transport.BestHttp
{
    /// <summary>
    /// Newtonsoft.Json converter for <see cref="ReadOnlyMemory{T}"/> of <see cref="byte"/>.
    /// Mirrors <see cref="System.Text.Json"/>'s base64-string shape so the BestHTTP-backed
    /// Unity client and the <c>System.Text.Json</c>-backed server agree on the wire.
    /// <para>
    /// Duplicate of <c>SharedMeta.Client.Network.RomByteJsonConverter</c> in the
    /// <c>SharedMeta.Transport.Http</c> asmdef — kept here because <c>SharedMeta.Transport.BestHttp</c>
    /// doesn't reference the Http asmdef and the file is tiny / no logic to keep in sync.
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
            writer.WriteValue(Convert.ToBase64String(value.Span));
        }

        public override ReadOnlyMemory<byte> ReadJson(JsonReader reader, Type objectType, ReadOnlyMemory<byte> existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return default;
            if (reader.TokenType == JsonToken.Bytes)
            {
                return (byte[]?)reader.Value ?? Array.Empty<byte>();
            }
            var s = reader.Value as string;
            if (string.IsNullOrEmpty(s)) return default;
            return Convert.FromBase64String(s);
        }
    }
}
