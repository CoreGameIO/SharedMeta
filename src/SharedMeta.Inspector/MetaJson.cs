using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharedMeta.Inspector;

/// <summary>
/// Turns arbitrary game values (state, arguments, results) into JSON for inspection. Never throws:
/// what cannot be serialized becomes <c>"&lt;unserializable: T&gt;"</c>. Members whose name matches a
/// redaction pattern — properties and dictionary keys alike — become <c>"***"</c>.
/// </summary>
public sealed class MetaJson
{
    private readonly Regex[] _redact;
    private readonly int _maxBinary;

    public MetaJson(IEnumerable<string> redactPatterns, int maxBinaryBytes = 256)
    {
        _redact = redactPatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        _maxBinary = maxBinaryBytes;
        Options = CreateOptions(maxBinaryBytes);
    }

    /// <summary>Options for reading and writing game values (fields included, cycles ignored).</summary>
    public JsonSerializerOptions Options { get; }

    public JsonNode? ToNode(object? value, Type? type = null)
    {
        if (value == null) return null;
        try
        {
            var node = JsonSerializer.SerializeToNode(value, type ?? value.GetType(), Options);
            Redact(node);
            return node;
        }
        catch (Exception ex)
        {
            return JsonValue.Create($"<unserializable: {(type ?? value.GetType()).Name}: {ex.GetType().Name}>");
        }
    }

    /// <summary>Binary payload that could not be decoded, shown by size and a short prefix.</summary>
    public JsonNode Raw(ReadOnlySpan<byte> bytes, string? reason = null)
    {
        var obj = new JsonObject
        {
            ["_bytes"] = bytes.Length,
            ["_base64"] = Convert.ToBase64String(bytes.Slice(0, Math.Min(bytes.Length, _maxBinary))),
        };
        if (reason != null) obj["_error"] = reason;
        return obj;
    }

    public bool IsRedacted(string name)
    {
        foreach (var r in _redact)
            if (r.IsMatch(name)) return true;
        return false;
    }

    public void Redact(JsonNode? node)
    {
        if (_redact.Length == 0 || node == null) return;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsRedacted(key)) obj[key] = "***";
                    else Redact(obj[key]);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    // Dictionaries with non-string keys are written as [{ "Key": …, "Value": … }].
                    if (item is JsonObject entry && entry.Count == 2
                        && entry.TryGetPropertyValue("Key", out var key) && key is JsonValue kv
                        && kv.TryGetValue<string>(out var keyText) && IsRedacted(keyText))
                    {
                        entry["Value"] = "***";
                        continue;
                    }
                    Redact(item);
                }
                break;
        }
    }

    private static JsonSerializerOptions CreateOptions(int maxBinary)
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            MaxDepth = 64,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new BinaryConverter(maxBinary));
        options.Converters.Add(new BinaryMemoryConverter(maxBinary));
        options.Converters.Add(new ComplexKeyDictionaryConverterFactory());
        return options;
    }

    // ── converters ─────────────────────────────────────────────────────────────

    private sealed class BinaryConverter(int max) : JsonConverter<byte[]>
    {
        public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : reader.GetBytesFromBase64();

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
            => WriteBinary(writer, value, max);
    }

    private sealed class BinaryMemoryConverter(int max) : JsonConverter<ReadOnlyMemory<byte>>
    {
        public override ReadOnlyMemory<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? default : reader.GetBytesFromBase64();

        public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte> value, JsonSerializerOptions options)
            => WriteBinary(writer, value.Span, max);
    }

    private static void WriteBinary(Utf8JsonWriter writer, ReadOnlySpan<byte> value, int max)
    {
        if (value.Length <= max)
        {
            writer.WriteBase64StringValue(value);
            return;
        }
        writer.WriteStartObject();
        writer.WriteNumber("_bytes", value.Length);
        writer.WriteBase64String("_base64", value.Slice(0, max));
        writer.WriteEndObject();
    }

    /// <summary>
    /// Dictionaries whose key System.Text.Json cannot write as a property name (structs, records,
    /// tuples) become an array of key/value entries instead of failing the whole value.
    /// </summary>
    private sealed class ComplexKeyDictionaryConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            var keyType = DictionaryKeyType(typeToConvert);
            return keyType != null && !IsNativeKey(keyType);
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(EntriesConverter<>).MakeGenericType(typeToConvert))!;

        private static Type? DictionaryKeyType(Type type)
        {
            foreach (var iface in type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces())
            {
                if (!iface.IsGenericType) continue;
                var def = iface.GetGenericTypeDefinition();
                if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                    return iface.GetGenericArguments()[0];
            }
            return null;
        }

        private static bool IsNativeKey(Type key)
        {
            key = Nullable.GetUnderlyingType(key) ?? key;
            return key == typeof(string) || key.IsPrimitive || key.IsEnum || key == typeof(Guid)
                   || key == typeof(decimal) || key == typeof(DateTime) || key == typeof(DateTimeOffset)
                   || key == typeof(object);
        }
    }

    private sealed class EntriesConverter<TDictionary> : JsonConverter<TDictionary> where TDictionary : IEnumerable
    {
        public override TDictionary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException("Dictionaries with complex keys are write-only in the inspector.");

        public override void Write(Utf8JsonWriter writer, TDictionary value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                if (item == null) continue;
                var itemType = item.GetType();
                var key = itemType.GetProperty("Key")?.GetValue(item);
                var val = itemType.GetProperty("Value")?.GetValue(item);
                writer.WriteStartObject();
                writer.WritePropertyName("Key");
                JsonSerializer.Serialize(writer, key, key?.GetType() ?? typeof(object), options);
                writer.WritePropertyName("Value");
                JsonSerializer.Serialize(writer, val, val?.GetType() ?? typeof(object), options);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }
}
