using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SharedMeta.Inspector.Mcp;

/// <summary>MCP tools over <see cref="InspectorService"/>. Every tool answers with compact JSON.</summary>
[McpServerToolType]
public sealed class InspectorTools
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly InspectorService _inspector;

    public InspectorTools(InspectorService inspector) => _inspector = inspector;

    private InspectorRuntime Runtime => _inspector.Runtime;

    // ── overview ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "inspector_status", ReadOnly = true, Idempotent = true)]
    [Description("Inspector state of this server process: whether call capture is available, the current capture level, watches, stored/dropped event counts, open sessions. Start here.")]
    public string Status() => Write(_inspector.Status());

    [McpServerTool(Name = "list_methods", ReadOnly = true, Idempotent = true)]
    [Description("All meta methods the server dispatches: service, method alias, version, execution mode, parameters (name, declared type, wire type) and result type; plus the entity state types.")]
    public string ListMethods() => Write(_inspector.Schema());

    // ── capture settings ───────────────────────────────────────────────────────

    [McpServerTool(Name = "get_capture_config", ReadOnly = true, Idempotent = true)]
    [Description("Current capture settings: global level, per-method levels, watched entities and players, sample rate. Levels: Off, Ids, Args, ArgsResult, Full (Full adds the entity state after each call).")]
    public string GetCaptureConfig() => Write(Runtime.GetCaptureSettings());

    [McpServerTool(Name = "set_capture_config", Idempotent = true)]
    [Description("Changes what is captured, effective immediately — on every silo when the inspector shares settings through a broker (see inspector_status), else on this server. Omitted arguments keep their value. methodLevels maps 'IService.Method', 'IService.*' or '*' to a level; pass an empty object to clear. Prefer watch_entity / watch_player for focused capture.")]
    public async Task<string> SetCaptureConfig(
        [Description("Global level for methods without a specific rule: Off, Ids, Args, ArgsResult, Full.")] InspectorLevel? level = null,
        [Description("Per-method levels, e.g. {\"IShopService.Buy\": \"Full\", \"IChatService.*\": \"Off\"}.")] Dictionary<string, InspectorLevel>? methodLevels = null,
        [Description("Fraction (0..1) of calls kept at method/global level. Watches are never sampled.")] double? sampleRate = null,
        [Description("One broadcast event per subscriber instead of one per operation.")] bool? broadcastPerSubscriber = null,
        [Description("Auto-revert a non-Off global level back to Off after this many minutes (0 clears the timer; watches stay).")] int? globalTtlMinutes = null)
        => await Explain(async () => Write(await Runtime.UpdateCaptureSettingsAsync(s =>
        {
            if (level.HasValue) s.Level = level.Value;
            if (methodLevels != null) s.MethodLevels = new Dictionary<string, InspectorLevel>(methodLevels, StringComparer.Ordinal);
            if (sampleRate.HasValue) s.SampleRate = sampleRate.Value;
            if (broadcastPerSubscriber.HasValue) s.BroadcastPerSubscriber = broadcastPerSubscriber.Value;
            if (globalTtlMinutes.HasValue)
            {
                s.GlobalLevelExpiresAtUtc = globalTtlMinutes.Value > 0 && s.Level != InspectorLevel.Off
                    ? DateTime.UtcNow.AddMinutes(globalTtlMinutes.Value)
                    : null;
            }
            if (s.Level == InspectorLevel.Off) s.GlobalLevelExpiresAtUtc = null;
        })));

    [McpServerTool(Name = "watch_entity", Idempotent = true)]
    [Description("Captures everything about one entity from now on — calls into it, its triggers, calls it sends, broadcasts — at the given level (default Full, which records the state after every call; needed for entity_state_at).")]
    public async Task<string> WatchEntity(
        [Description("Entity id (the grain key).")] string entityId,
        [Description("Level: Ids, Args, ArgsResult or Full.")] InspectorLevel level = InspectorLevel.Full)
        => await Explain(async () => Write(await Runtime.UpdateCaptureSettingsAsync(s => SetWatch(s.WatchedEntities, entityId, level))));

    [McpServerTool(Name = "unwatch_entity", Idempotent = true)]
    [Description("Stops the entity watch; already captured events stay.")]
    public async Task<string> UnwatchEntity([Description("Entity id.")] string entityId)
        => await Explain(async () => Write(await Runtime.UpdateCaptureSettingsAsync(s => s.WatchedEntities.Remove(entityId))));

    [McpServerTool(Name = "watch_player", Idempotent = true)]
    [Description("Captures every call made by a player and every broadcast sent to them, at the given level.")]
    public async Task<string> WatchPlayer(
        [Description("Player id.")] string playerId,
        [Description("Level: Ids, Args, ArgsResult or Full.")] InspectorLevel level = InspectorLevel.ArgsResult)
        => await Explain(async () => Write(await Runtime.UpdateCaptureSettingsAsync(s => SetWatch(s.WatchedPlayers, playerId, level))));

    private static void SetWatch(Dictionary<string, InspectorLevel> watches, string id, InspectorLevel level)
    {
        if (level == InspectorLevel.Off) watches.Remove(id);
        else watches[id] = level;
    }

    [McpServerTool(Name = "unwatch_player", Idempotent = true)]
    [Description("Stops the player watch.")]
    public async Task<string> UnwatchPlayer([Description("Player id.")] string playerId)
        => await Explain(async () => Write(await Runtime.UpdateCaptureSettingsAsync(s => s.WatchedPlayers.Remove(playerId))));

    // ── events ─────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "recent_calls", ReadOnly = true)]
    [Description("Newest captured events, newest first, optionally filtered. Kinds: call_completed, call_sent, broadcast_sent, client_responded, subscribed, reclaimed, unsubscribed, session_started, session_ended. callKind: rpc, cross_entity, trigger, nested, query, signal, init. Use the returned id as sinceId to poll for newer events.")]
    public string RecentCalls(
        [Description("Entity id filter.")] string? entityId = null,
        [Description("State type (short or full name) — with entityId narrows to one entity.")] string? stateType = null,
        [Description("Player id filter (caller or broadcast recipient).")] string? playerId = null,
        [Description("'IService.Method' or a method alias.")] string? method = null,
        [Description("Event kind filter.")] string? kind = null,
        [Description("Dispatch kind filter.")] string? callKind = null,
        [Description("Only failed / rejected events.")] bool errorsOnly = false,
        [Description("Only events with an id greater than this.")] long? sinceId = null,
        [Description("Maximum events returned.")] int limit = 50,
        [Description("Include decoded args / result / state.")] bool includePayloads = false)
    {
        var query = new InspectorQuery
        {
            EntityKey = stateType != null && entityId != null ? _inspector.EntityKey(stateType, entityId) : null,
            EntityId = stateType == null ? entityId : null,
            StateType = stateType != null && entityId == null ? Runtime.ResolveStateTypeName(stateType) : null,
            PlayerId = playerId,
            Method = method,
            Kind = kind,
            CallKind = callKind,
            ErrorsOnly = errorsOnly,
            SinceId = sinceId,
            Limit = Math.Clamp(limit, 1, 1000),
        };
        var events = _inspector.Recent(query, includePayloads);
        return Write(new JsonObject { ["count"] = events.Count, ["events"] = InspectorService.ToJson(events, includePayloads) });
    }

    [McpServerTool(Name = "get_call", ReadOnly = true)]
    [Description("Everything one call caused, as a tree by span: the entity dispatch, its triggers and nested calls, calls it sent to other entities and what those did, broadcasts, and the reply to the client. callId is the 32-hex trace id shown on events and in server logs (CallId).")]
    public string GetCall(
        [Description("Call id (trace id).")] string callId,
        [Description("Include decoded args / result / state.")] bool includePayloads = true)
        => Write(_inspector.CallTree(callId, includePayloads));

    // ── TimeMachine ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "entity_timeline", ReadOnly = true)]
    [Description("TimeMachine: the entity's events in entity order (sequence, then order within the operation) — calls that reached it and from whom, its triggers, calls it sent, broadcasts, subscriptions — with a Mermaid sequence diagram. With a broker, events older than the in-memory window are read from its history.")]
    public async Task<string> EntityTimeline(
        [Description("State type (short or full name).")] string stateType,
        [Description("Entity id.")] string entityId,
        [Description("From entity sequence (inclusive).")] long? fromSeq = null,
        [Description("To entity sequence (inclusive).")] long? toSeq = null,
        [Description("Newest N events at most.")] int limit = 200,
        [Description("Include decoded args / result / state.")] bool includePayloads = false)
        => await Explain(async () =>
        {
            var timeline = await _inspector.TimelineAsync(_inspector.EntityKey(stateType, entityId), fromSeq, toSeq, Math.Clamp(limit, 1, 2000), includePayloads);
            return Write(new JsonObject
            {
                ["entity"] = timeline.EntityKey,
                ["count"] = timeline.Events.Count,
                ["history"] = timeline.History,
                ["events"] = InspectorService.ToJson(timeline.Events, includePayloads),
                ["mermaid"] = _inspector.TimelineMermaid(timeline.EntityKey, timeline.Events),
            });
        });

    [McpServerTool(Name = "entity_graph", ReadOnly = true)]
    [Description("TimeMachine: entities and players connected to this entity through calls, cross-entity hops and broadcasts, up to `depth` hops, from the newest `window` events; with a Mermaid flowchart.")]
    public string EntityGraph(
        [Description("State type (short or full name).")] string stateType,
        [Description("Entity id.")] string entityId,
        [Description("Hops from the entity.")] int depth = 2,
        [Description("Newest events to scan.")] int window = 5000)
        => Write(_inspector.Graph(_inspector.EntityKey(stateType, entityId), Math.Clamp(depth, 1, 6), Math.Clamp(window, 1, 100_000)));

    [McpServerTool(Name = "entity_state_at", ReadOnly = true)]
    [Description("TimeMachine: the entity's state after operation `seq`, from events captured at level Full. Reports the sequence actually used and whether it is exact.")]
    public async Task<string> EntityStateAt(
        [Description("State type (short or full name).")] string stateType,
        [Description("Entity id.")] string entityId,
        [Description("Entity sequence number.")] long seq)
        => await Explain(async () => Write(await _inspector.StateAtAsync(_inspector.EntityKey(stateType, entityId), seq)));

    // ── live ───────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_entity_state", ReadOnly = true)]
    [Description("Current state of a live entity, optionally a sub-path like 'Inventory.Items[0]'. Without activate=true only entities the inspector has seen are read; reading may activate a deactivated grain.")]
    public async Task<string> GetEntityState(
        [Description("State type (short or full name).")] string stateType,
        [Description("Entity id.")] string entityId,
        [Description("Optional dot/index path into the state.")] string? path = null,
        [Description("Read even if never seen (activates the grain; an unknown id yields a default state).")] bool activate = false)
        => await Explain(async () => Write(await _inspector.LiveStateAsync(stateType, entityId, path, activate)));

    [McpServerTool(Name = "list_entities", ReadOnly = true)]
    [Description("Entities that produced events recently (most recent first) with their last sequence.")]
    public string ListEntities(
        [Description("State type filter (short or full name).")] string? stateType = null,
        [Description("Maximum entities.")] int limit = 100)
    {
        var full = stateType != null ? Runtime.ResolveStateTypeName(stateType) : null;
        var entities = Runtime.SeenEntitiesSnapshot()
            .Where(e => full == null || e.StateType == full)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToList();
        return Write(new { count = entities.Count, entities });
    }

    [McpServerTool(Name = "list_sessions", ReadOnly = true)]
    [Description("Client sessions currently open on this server.")]
    public string ListSessions() => Write(Runtime.Sessions.Snapshot());

    [McpServerTool(Name = "invoke_method", Destructive = true)]
    [Description("Calls a meta method on an entity as server code (like a generated ServerApi): state changes are broadcast and persisted. Disabled unless the host enabled it and allow-listed the method. args: JSON object by parameter name or array in order, in wire form (see list_methods).")]
    public async Task<string> InvokeMethod(
        [Description("State type (short or full name).")] string stateType,
        [Description("Entity id.")] string entityId,
        [Description("Service interface name, e.g. IShopService.")] string service,
        [Description("Method alias (or C# name).")] string method,
        [Description("Arguments: {\"name\": value} or [values].")] JsonElement? args = null,
        [Description("Method version; latest when omitted.")] int? version = null,
        [Description("Caller id seen by the method (Context.CallerId).")] string? callerId = null,
        [Description("Client version to run under (selects config branch); needed for a cold entity with service configs.")] string? callerClientVersion = null)
        => await Explain(async () => Write(await _inspector.InvokeAsync(stateType, entityId, service, method, args, version, callerId, callerClientVersion)));

    private static string Write<T>(T value) => JsonSerializer.Serialize(value, s_json);

    // The SDK replaces the message of any exception but McpException with a generic one; the
    // inspector's refusals say what to do instead (activate=true, use a silo, allow-list …).
    private static async Task<string> Explain(Func<Task<string>> body)
    {
        try { return await body(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
