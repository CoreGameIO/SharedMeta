using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharedMeta.Inspector;

/// <summary>Kinds of <see cref="MetaInspectorEvent"/>.</summary>
public static class InspectorEventKinds
{
    /// <summary>The entity executed a dispatch (any <see cref="MetaInspectorEvent.CallKind"/>).</summary>
    public const string CallCompleted = "call_completed";
    /// <summary>The entity called another entity (recorded on the sender).</summary>
    public const string CallSent = "call_sent";
    /// <summary>The entity's operation was broadcast to subscribers.</summary>
    public const string BroadcastSent = "broadcast_sent";
    /// <summary>The transport answered the client that made the call.</summary>
    public const string ClientResponded = "client_responded";
    public const string Subscribed = "subscribed";
    public const string Reclaimed = "reclaimed";
    public const string Unsubscribed = "unsubscribed";
    public const string SessionStarted = "session_started";
    public const string SessionEnded = "session_ended";
}

/// <summary>
/// One captured fact. Flat and JSON-shaped so it can go to any sink as is. Causality comes from
/// <see cref="CallId"/> / <see cref="SpanId"/> / <see cref="ParentSpanId"/>; order within an entity
/// from (<see cref="EntitySequence"/>, <see cref="SubOrder"/>) — never from clocks across silos.
/// </summary>
public sealed class MetaInspectorEvent
{
    /// <summary>Position in this process's store (monotonic).</summary>
    public long Id { get; set; }
    public string Kind { get; set; } = "";

    public string? CallId { get; set; }
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }

    /// <summary><c>StateTypeFullName:entityId</c> of the entity the event belongs to.</summary>
    public string? EntityKey { get; set; }
    public string? StateType { get; set; }
    public string? EntityId { get; set; }
    public long EntitySequence { get; set; }
    public int SubOrder { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public double DurationMs { get; set; }
    public string? Silo { get; set; }

    public int? MethodId { get; set; }
    public string? Service { get; set; }
    public string? Method { get; set; }
    /// <summary>rpc / cross_entity / trigger / nested / query / signal / init.</summary>
    public string? CallKind { get; set; }

    /// <summary>Player id the call ran for (client calls), or the server caller id.</summary>
    public string? CallerId { get; set; }
    /// <summary>Entity that made a cross-entity call, when known.</summary>
    public string? CallerEntity { get; set; }
    public string? TargetEntity { get; set; }
    public string? TargetPlayer { get; set; }
    public int? SubscriberCount { get; set; }
    public List<string>? TargetPlayers { get; set; }

    /// <summary>success / error / rejected / bad_request.</summary>
    public string? Result { get; set; }
    public string? ErrorKind { get; set; }
    public string? Error { get; set; }

    public JsonNode? Args { get; set; }
    public JsonNode? ResultValue { get; set; }
    public JsonNode? State { get; set; }

    public int? FromVersion { get; set; }
    public int? ToVersion { get; set; }
    public string? SessionId { get; set; }
    public string? ClientVersion { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Position in the broker's stream when the event came through one: the same event read again
    /// (store vs. history) has the same position.
    /// </summary>
    [JsonIgnore]
    public ulong? BrokerSequence { get; set; }

    /// <summary>Approximate in-memory size, for the store budget.</summary>
    [JsonIgnore]
    public int ApproxBytes { get; set; }

    /// <summary>Copy without the payload fields — for listings.</summary>
    public MetaInspectorEvent Summary()
    {
        var copy = (MetaInspectorEvent)MemberwiseClone();
        copy.Args = null;
        copy.ResultValue = null;
        copy.State = null;
        return copy;
    }

    internal MetaInspectorEvent Clone() => (MetaInspectorEvent)MemberwiseClone();

    public static string MakeEntityKey(string stateType, string entityId) => stateType + ":" + entityId;

    /// <summary>Splits an entity key at its first colon (state type names have none; ids may).</summary>
    public static bool TrySplitEntityKey(string entityKey, out string stateType, out string entityId)
    {
        var i = entityKey.IndexOf(':');
        stateType = i > 0 ? entityKey.Substring(0, i) : "";
        entityId = i > 0 ? entityKey.Substring(i + 1) : "";
        return i > 0;
    }
}
