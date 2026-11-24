using System.Text;

namespace SharedMeta.Inspector.Nats;

/// <summary>
/// Subject layout: <c>{prefix}.ev.{stateType}.{entityId}</c>, so one entity's events can be read
/// back by a filter subject. Names are encoded into single tokens: subjects cannot carry '.', '*',
/// '>' or whitespace, and state type names are dotted.
/// </summary>
internal sealed class NatsSubjects
{
    // An encoded token never starts with this (the '~' of a name is encoded as ~7E).
    private const string NoEntity = "~none";

    private readonly string _events;

    public NatsSubjects(string prefix)
    {
        _events = prefix.TrimEnd('.') + ".ev";
        All = _events + ".>";
    }

    /// <summary>Every event subject.</summary>
    public string All { get; }

    public string ForEntity(string stateType, string entityId) => _events + "." + Token(stateType) + "." + Token(entityId);

    /// <summary>The entity id under every state type (an encoded type is always one token).</summary>
    public string ForAnyStateType(string entityId) => _events + ".*." + Token(entityId);

    /// <summary>Subject of an event: its entity's, or one per player for session events.</summary>
    public string For(MetaInspectorEvent e)
        => e.StateType != null && e.EntityId != null
            ? ForEntity(e.StateType, e.EntityId)
            : _events + "." + NoEntity + "." + Token(e.CallerId ?? e.TargetPlayer ?? "");

    /// <summary>
    /// Letters, digits, '_' and '-' stay; every other UTF-8 byte becomes <c>~XX</c>. Injective, so
    /// two names never share a subject.
    /// </summary>
    internal static string Token(string value)
    {
        if (value.Length == 0) return "~";
        var sb = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-')
                sb.Append((char)b);
            else
                sb.Append('~').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
