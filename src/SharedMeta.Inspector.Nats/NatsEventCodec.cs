using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharedMeta.Inspector.Nats;

/// <summary>Events travel as the same camelCase JSON the tools return.</summary>
internal static class NatsEventCodec
{
    // A decoded JSON tree takes a few times its text.
    private const int TreeBytesPerJsonByte = 3;

    public static byte[] Encode(MetaInspectorEvent e) => JsonSerializer.SerializeToUtf8Bytes(e, InspectorService.EventJson);

    public static MetaInspectorEvent? Decode(ReadOnlySpan<byte> json)
    {
        var e = JsonSerializer.Deserialize<MetaInspectorEvent>(json, InspectorService.EventJson);
        if (e != null) e.ApproxBytes = 400 + json.Length * TreeBytesPerJsonByte;
        return e;
    }

    /// <summary>Same event with its payloads replaced by their size — for messages over the server's limit.</summary>
    public static MetaInspectorEvent WithoutPayloads(MetaInspectorEvent e)
    {
        var copy = e.Summary();
        if (e.Args != null) copy.Args = Skipped(e.Args);
        if (e.ResultValue != null) copy.ResultValue = Skipped(e.ResultValue);
        if (e.State != null) copy.State = Skipped(e.State);
        return copy;
    }

    private static JsonNode Skipped(JsonNode node)
        => new JsonObject { ["_skipped_json_bytes"] = node.ToJsonString().Length, ["_reason"] = "over the broker's max message size" };
}
