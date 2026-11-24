using System.Text.Json;

namespace SharedMeta.Inspector;

/// <summary>Destination of encoded events (store, file, broker). Called from one background writer.</summary>
public interface IMetaEventSink
{
    ValueTask WriteAsync(IReadOnlyList<MetaInspectorEvent> events);
}

/// <summary>
/// Bounded in-memory store with indexes by entity, call, player and method. The oldest events are
/// evicted first, by count and by approximate size; eviction always cleans every index.
/// Thread-safe: one writer (the encoder), any number of readers.
/// </summary>
public sealed class InMemoryEventStore : IMetaEventSink
{
    private readonly int _maxEvents;
    private readonly long _maxBytes;
    private readonly object _lock = new();
    private readonly LinkedList<MetaInspectorEvent> _events = new();
    private readonly Dictionary<string, LinkedList<MetaInspectorEvent>> _byEntity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<MetaInspectorEvent>> _byCall = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<MetaInspectorEvent>> _byPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedList<MetaInspectorEvent>> _byMethod = new(StringComparer.Ordinal);
    private long _bytes;
    private long _nextId;
    private long _evicted;

    public InMemoryEventStore(int maxEvents, long maxBytes)
    {
        _maxEvents = Math.Max(1, maxEvents);
        _maxBytes = Math.Max(1, maxBytes);
    }

    public int Count { get { lock (_lock) return _events.Count; } }
    public long Bytes { get { lock (_lock) return _bytes; } }
    public long Evicted { get { lock (_lock) return _evicted; } }

    public ValueTask WriteAsync(IReadOnlyList<MetaInspectorEvent> events)
    {
        lock (_lock)
        {
            foreach (var e in events) Add(e);
        }
        return default;
    }

    private void Add(MetaInspectorEvent e)
    {
        e.Id = ++_nextId;
        _events.AddLast(e);
        _bytes += e.ApproxBytes;
        Index(_byEntity, e.EntityKey, e);
        if (e.TargetEntity != null && e.TargetEntity != e.EntityKey) Index(_byEntity, e.TargetEntity, e);
        Index(_byCall, e.CallId, e);
        foreach (var player in Players(e)) Index(_byPlayer, player, e);
        Index(_byMethod, MethodKey(e), e);

        while (_events.Count > _maxEvents || (_bytes > _maxBytes && _events.Count > 1))
            EvictOldest();
    }

    private void EvictOldest()
    {
        var e = _events.First!.Value;
        _events.RemoveFirst();
        _bytes -= e.ApproxBytes;
        _evicted++;
        Unindex(_byEntity, e.EntityKey, e);
        if (e.TargetEntity != null && e.TargetEntity != e.EntityKey) Unindex(_byEntity, e.TargetEntity, e);
        Unindex(_byCall, e.CallId, e);
        foreach (var player in Players(e)) Unindex(_byPlayer, player, e);
        Unindex(_byMethod, MethodKey(e), e);
    }

    private static string? MethodKey(MetaInspectorEvent e) => e.Service != null && e.Method != null ? e.Service + "." + e.Method : null;

    /// <summary>Caller and recipients, each once (a folded broadcast lists its recipients).</summary>
    private static IEnumerable<string> Players(MetaInspectorEvent e)
    {
        if (e.CallerId != null) yield return e.CallerId;
        if (e.TargetPlayer != null && e.TargetPlayer != e.CallerId) yield return e.TargetPlayer;
        if (e.TargetPlayers == null) yield break;
        foreach (var p in e.TargetPlayers.Distinct(StringComparer.Ordinal))
            if (p != e.CallerId && p != e.TargetPlayer) yield return p;
    }

    private static bool InvolvesPlayer(MetaInspectorEvent e, string player)
        => e.CallerId == player || e.TargetPlayer == player || (e.TargetPlayers?.Contains(player) ?? false);

    private static void Index(Dictionary<string, LinkedList<MetaInspectorEvent>> index, string? key, MetaInspectorEvent e)
    {
        if (key == null) return;
        if (!index.TryGetValue(key, out var list)) index[key] = list = new LinkedList<MetaInspectorEvent>();
        list.AddLast(e);
    }

    // Events are evicted oldest-first, so an evicted event is the first entry of every list it is in.
    private static void Unindex(Dictionary<string, LinkedList<MetaInspectorEvent>> index, string? key, MetaInspectorEvent e)
    {
        if (key == null || !index.TryGetValue(key, out var list)) return;
        if (list.First != null && ReferenceEquals(list.First.Value, e)) list.RemoveFirst();
        else list.Remove(e);
        if (list.Count == 0) index.Remove(key);
    }

    /// <summary>Snapshot of matching events, oldest first.</summary>
    public List<MetaInspectorEvent> Query(InspectorQuery query)
    {
        lock (_lock)
        {
            IEnumerable<MetaInspectorEvent> source =
                query.CallId != null ? Lookup(_byCall, query.CallId)
                : query.EntityKey != null ? Lookup(_byEntity, query.EntityKey)
                : query.PlayerId != null ? Lookup(_byPlayer, query.PlayerId)
                : query.Method != null && query.Method.Contains('.') ? Lookup(_byMethod, query.Method)
                : _events;

            var result = new List<MetaInspectorEvent>();
            foreach (var e in source)
            {
                if (query.SinceId.HasValue && e.Id <= query.SinceId.Value) continue;
                // Every filter is checked, whichever index chose the candidates.
                if (query.CallId != null && e.CallId != query.CallId) continue;
                if (query.EntityKey != null && e.EntityKey != query.EntityKey && e.TargetEntity != query.EntityKey) continue;
                if (query.PlayerId != null && !InvolvesPlayer(e, query.PlayerId)) continue;
                if (query.StateType != null && e.StateType != query.StateType
                    && !(e.TargetEntity?.StartsWith(query.StateType + ":", StringComparison.Ordinal) ?? false)) continue;
                if (query.EntityId != null && e.EntityId != query.EntityId && !(e.TargetEntity?.EndsWith(":" + query.EntityId) ?? false)) continue;
                if (query.Kind != null && e.Kind != query.Kind) continue;
                if (query.CallKind != null && e.CallKind != query.CallKind) continue;
                if (query.Method != null && !MatchesMethod(e, query.Method)) continue;
                if (query.ErrorsOnly && (e.Result == null || e.Result == "success")) continue;
                result.Add(e);
            }
            if (query.Limit > 0 && result.Count > query.Limit)
                result = result.GetRange(result.Count - query.Limit, query.Limit);
            return result;
        }
    }

    private static bool MatchesMethod(MetaInspectorEvent e, string method)
        => method.Contains('.')
            ? e.Service + "." + e.Method == method
            : string.Equals(e.Method, method, StringComparison.Ordinal);

    private static IEnumerable<MetaInspectorEvent> Lookup(Dictionary<string, LinkedList<MetaInspectorEvent>> index, string key)
        => index.TryGetValue(key, out var list) ? list : Enumerable.Empty<MetaInspectorEvent>();

    /// <summary>Every entity key that still has events, with its event count.</summary>
    public List<(string EntityKey, int Events)> Entities()
    {
        lock (_lock) return _byEntity.Select(p => (p.Key, p.Value.Count)).ToList();
    }
}

/// <summary>Filter for <see cref="InMemoryEventStore.Query"/>.</summary>
public sealed class InspectorQuery
{
    public string? EntityKey { get; set; }
    public string? EntityId { get; set; }
    /// <summary>Full state type name: events of any entity of that type (or calls sent to one).</summary>
    public string? StateType { get; set; }
    public string? CallId { get; set; }
    public string? PlayerId { get; set; }
    /// <summary><c>"IService.Method"</c> or just the method alias.</summary>
    public string? Method { get; set; }
    public string? Kind { get; set; }
    public string? CallKind { get; set; }
    public bool ErrorsOnly { get; set; }
    public long? SinceId { get; set; }
    /// <summary>Keep only the newest N; 0 = all.</summary>
    public int Limit { get; set; }
}

/// <summary>Appends every event as one JSON line — for offline analysis.</summary>
public sealed class JsonlFileSink : IMetaEventSink, IDisposable
{
    private readonly StreamWriter _writer;

    public JsonlFileSink(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = false };
    }

    public async ValueTask WriteAsync(IReadOnlyList<MetaInspectorEvent> events)
    {
        foreach (var e in events)
            await _writer.WriteLineAsync(JsonSerializer.Serialize(e, InspectorService.EventJson));
        await _writer.FlushAsync();
    }

    public void Dispose() => _writer.Dispose();
}
