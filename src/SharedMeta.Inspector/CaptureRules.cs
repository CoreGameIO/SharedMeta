using SharedMeta.Core.Transport;

namespace SharedMeta.Inspector;

/// <summary>
/// Immutable snapshot of <see cref="InspectorCaptureSettings"/> compiled for the hot path: a level
/// per server method id, plus the watch tables. Replaced whole when the settings change, so a
/// reader never sees half an update.
/// </summary>
internal sealed class CaptureRules
{
    private readonly InspectorLevel[] _byMethodId;
    private readonly InspectorLevel _unknownMethodLevel;
    private readonly Dictionary<string, InspectorLevel> _byMethodName;
    private readonly Dictionary<string, InspectorLevel> _entities;
    private readonly Dictionary<string, InspectorLevel> _players;
    private readonly uint _sampleThreshold;

    public CaptureRules(InspectorCaptureSettings settings, MetaServerSignature? signature)
    {
        Settings = settings;
        // An Off watch captures nothing; keeping it would hold IsActive (and every filter) on.
        _entities = Watches(settings.WatchedEntities);
        _players = Watches(settings.WatchedPlayers);
        _unknownMethodLevel = LevelFor(settings, null, null);

        var methods = signature?.Methods ?? Array.Empty<ServerMethodEntry>();
        int max = methods.Count == 0 ? -1 : methods.Max(m => (int)m.GlobalIndex);
        _byMethodId = new InspectorLevel[max + 1];
        for (int i = 0; i < _byMethodId.Length; i++) _byMethodId[i] = _unknownMethodLevel;
        _byMethodName = new Dictionary<string, InspectorLevel>(StringComparer.Ordinal);
        foreach (var m in methods)
        {
            var level = LevelFor(settings, m.ServiceName, m.Alias);
            _byMethodId[m.GlobalIndex] = level;
            _byMethodName[m.ServiceName + "." + m.Alias] = level;
        }

        var rate = Math.Clamp(settings.SampleRate, 0, 1);
        _sampleThreshold = rate >= 1 ? uint.MaxValue : (uint)(rate * uint.MaxValue);

        AnyMethodLevel = _unknownMethodLevel != InspectorLevel.Off || _byMethodId.Any(l => l != InspectorLevel.Off);
        IsActive = AnyMethodLevel || _entities.Count > 0 || _players.Count > 0;
        HasPlayerWatches = _players.Count > 0;
        HasEntityWatches = _entities.Count > 0;
    }

    /// <summary>A call from another entity may be captured for its watched sender.</summary>
    public bool HasEntityWatches { get; }

    public InspectorCaptureSettings Settings { get; }

    /// <summary>Anything at all may be captured.</summary>
    public bool IsActive { get; }

    public bool AnyMethodLevel { get; }

    /// <summary>Player watches can only be resolved once the caller is known (in Begin).</summary>
    public bool HasPlayerWatches { get; }

    public InspectorLevel MethodLevel(ushort methodId)
        => methodId < _byMethodId.Length ? _byMethodId[methodId] : _unknownMethodLevel;

    public InspectorLevel MethodLevel(string? service, string? method)
    {
        if (service != null && method != null && _byMethodName.TryGetValue(service + "." + method, out var level))
            return level;
        return _unknownMethodLevel;
    }

    public InspectorLevel EntityLevel(string? entityId)
        => entityId != null && _entities.TryGetValue(entityId, out var level) ? level : InspectorLevel.Off;

    public InspectorLevel PlayerLevel(string? playerId)
        => playerId != null && _players.TryGetValue(playerId, out var level) ? level : InspectorLevel.Off;

    /// <summary>
    /// Level of one event: the method level (sampled), raised by entity and player watches, which
    /// are never sampled.
    /// </summary>
    public InspectorLevel Resolve(InspectorLevel methodLevel, string? entityId, string? playerId, string? otherEntityId = null)
    {
        var level = methodLevel != InspectorLevel.Off && Sampled() ? methodLevel : InspectorLevel.Off;
        level = Max(level, EntityLevel(entityId));
        level = Max(level, EntityLevel(otherEntityId));
        level = Max(level, PlayerLevel(playerId));
        return level;
    }

    private bool Sampled() => _sampleThreshold == uint.MaxValue || (uint)Random.Shared.NextInt64(uint.MaxValue) < _sampleThreshold;

    public static InspectorLevel Max(InspectorLevel a, InspectorLevel b) => a >= b ? a : b;

    private static Dictionary<string, InspectorLevel> Watches(Dictionary<string, InspectorLevel> source)
    {
        var result = new Dictionary<string, InspectorLevel>(StringComparer.Ordinal);
        foreach (var (key, level) in source)
            if (level != InspectorLevel.Off) result[key] = level;
        return result;
    }

    /// <summary>Most specific of <c>"Svc.Method"</c>, <c>"Svc.*"</c>, <c>"*"</c>; otherwise the global level.</summary>
    private static InspectorLevel LevelFor(InspectorCaptureSettings settings, string? service, string? method)
    {
        var rules = settings.MethodLevels;
        if (service != null && method != null && rules.TryGetValue(service + "." + method, out var exact)) return exact;
        if (service != null && rules.TryGetValue(service + ".*", out var svc)) return svc;
        if (rules.TryGetValue("*", out var all)) return all;
        return settings.Level;
    }

    /// <summary><c>"Svc.Method"</c> / <c>"Svc.*"</c> / <c>"*"</c> match for the invoke allow-list.</summary>
    public static bool MatchesAny(IEnumerable<string> patterns, string service, string method)
    {
        foreach (var p in patterns)
        {
            if (p == "*" || p == service + ".*" || p == service + "." + method) return true;
        }
        return false;
    }
}
