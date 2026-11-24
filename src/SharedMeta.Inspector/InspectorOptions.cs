namespace SharedMeta.Inspector;

/// <summary>How much of a call an event carries. Each level includes the ones below it.</summary>
public enum InspectorLevel
{
    Off = 0,
    /// <summary>Who called what, when, with which outcome.</summary>
    Ids = 1,
    /// <summary>+ decoded arguments.</summary>
    Args = 2,
    /// <summary>+ decoded result.</summary>
    ArgsResult = 3,
    /// <summary>+ the entity state after the call (not for queries).</summary>
    Full = 4,
}

/// <summary>
/// What to capture. Changeable at runtime (MCP <c>set_capture_config</c>, <c>watch_entity</c>);
/// the host's options only seed it.
/// </summary>
public sealed class InspectorCaptureSettings
{
    /// <summary>Level for every method not matched by <see cref="MethodLevels"/>.</summary>
    public InspectorLevel Level { get; set; } = InspectorLevel.Off;

    /// <summary>
    /// Per-method overrides, keyed <c>"IService.Method"</c>, <c>"IService.*"</c> or <c>"*"</c>
    /// (method = alias). The most specific pattern wins.
    /// </summary>
    public Dictionary<string, InspectorLevel> MethodLevels { get; set; } = new();

    /// <summary>Entities to capture regardless of method level, by entity id; the highest level applies.</summary>
    public Dictionary<string, InspectorLevel> WatchedEntities { get; set; } = new();

    /// <summary>Players whose calls (and broadcasts to them) are captured regardless of method level.</summary>
    public Dictionary<string, InspectorLevel> WatchedPlayers { get; set; } = new();

    /// <summary>Fraction of calls kept at the method/global level, 0..1. Watches are never sampled.</summary>
    public double SampleRate { get; set; } = 1.0;

    /// <summary>One event per subscriber for broadcasts instead of one per operation.</summary>
    public bool BroadcastPerSubscriber { get; set; }

    public InspectorCaptureSettings Clone() => new()
    {
        Level = Level,
        MethodLevels = new Dictionary<string, InspectorLevel>(MethodLevels, StringComparer.Ordinal),
        WatchedEntities = new Dictionary<string, InspectorLevel>(WatchedEntities, StringComparer.Ordinal),
        WatchedPlayers = new Dictionary<string, InspectorLevel>(WatchedPlayers, StringComparer.Ordinal),
        SampleRate = SampleRate,
        BroadcastPerSubscriber = BroadcastPerSubscriber,
    };
}

/// <summary>Host configuration of the inspector.</summary>
public sealed class InspectorOptions
{
    /// <summary>Initial capture settings.</summary>
    public InspectorCaptureSettings Capture { get; set; } = new();

    /// <summary>Events kept in memory; the oldest go first.</summary>
    public int MaxEvents { get; set; } = 50_000;

    /// <summary>Approximate memory budget of the kept events' JSON.</summary>
    public long MaxBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Argument / result payloads above this are recorded by size only.</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>State snapshots above this are recorded by size only.</summary>
    public int MaxStateSnapshotBytes { get; set; } = 1024 * 1024;

    /// <summary>Events waiting for the background encoder; beyond it new events are dropped and counted.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>Entities remembered as "seen", independent of event eviction.</summary>
    public int SeenEntitiesCapacity { get; set; } = 10_000;

    /// <summary>Optional JSON-lines file every event is appended to.</summary>
    public string? JsonlPath { get; set; }

    /// <summary>Most events of one entity read back from a broker's history per query (the newest are kept).</summary>
    public int MaxHistoryEvents { get; set; } = 20_000;

    // ── Security: set from code / configuration only, never through MCP ────────

    /// <summary>Allow <c>invoke_method</c>. Off by default.</summary>
    public bool AllowInvoke { get; set; }

    /// <summary>Methods <c>invoke_method</c> may call, as <c>"IService.Method"</c> / <c>"IService.*"</c> / <c>"*"</c>.</summary>
    public List<string> InvokeMethods { get; set; } = new();

    /// <summary>
    /// Member-name patterns (<c>*</c> wildcard, case-insensitive) whose values are replaced by
    /// <c>"***"</c> in every captured or returned JSON — properties and dictionary keys alike.
    /// </summary>
    public List<string> RedactMembers { get; set; } = new() { "*password*", "*token*", "*secret*" };

    /// <summary>Required on every MCP request when set (header <c>X-Inspector-Key</c> or <c>Authorization: Bearer</c>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Accept MCP requests only from loopback with a localhost Host and no foreign Origin.</summary>
    public bool LocalhostOnly { get; set; } = true;
}
