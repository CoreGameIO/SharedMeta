using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Server.Core.Diagnostics;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Inspector;

/// <summary>
/// Questions asked of the captured events and of live entities: call trees, entity timelines and
/// graphs (TimeMachine), state at a sequence, current state, and opt-in invocation. Transport
/// agnostic — the MCP tools are a thin layer over it.
/// </summary>
public sealed class InspectorService
{
    private readonly InspectorRuntime _runtime;

    public InspectorService(InspectorRuntime runtime) => _runtime = runtime;

    public InspectorRuntime Runtime => _runtime;

    // ── identity ───────────────────────────────────────────────────────────────

    /// <summary><c>StateTypeFullName:entityId</c> from a short or full state type name.</summary>
    public string EntityKey(string stateType, string entityId)
        => MetaInspectorEvent.MakeEntityKey(_runtime.ResolveStateTypeName(stateType) ?? stateType, entityId);

    // ── status / schema ────────────────────────────────────────────────────────

    public JsonObject Status()
    {
        var settings = _runtime.GetCaptureSettings();
        return new JsonObject
        {
            ["silo"] = _runtime.Silo,
            ["environment"] = _runtime.EnvironmentName,
            ["captureAvailable"] = _runtime.CaptureAvailable,
            ["captureNote"] = _runtime.CaptureAvailable ? null
                : _runtime.GrainFactory == null
                    ? "Standalone inspector: no silo here, so it captures nothing itself and shows the events the silos publish to the broker."
                    : "No method table registered: the server project was built without a [MetaCallTapProvider] reference (or in legacy shared-project mode), so calls are not reported.",
            ["level"] = settings.Level.ToString(),
            ["globalLevelExpiresAtUtc"] = settings.GlobalLevelExpiresAtUtc,
            ["maxGlobalCaptureMinutes"] = _runtime.Options.MaxGlobalCaptureDuration is { } cap && cap > TimeSpan.Zero
                ? Math.Round(cap.TotalMinutes, 2)
                : null,
            ["watchedEntities"] = settings.WatchedEntities.Count,
            ["watchedPlayers"] = settings.WatchedPlayers.Count,
            ["storedEvents"] = _runtime.Store.Count,
            ["storedBytes"] = _runtime.Store.Bytes,
            ["evictedEvents"] = _runtime.Store.Evicted,
            ["droppedEvents"] = _runtime.Dropped,
            ["queuedEvents"] = _runtime.Queued,
            ["openSessions"] = _runtime.Sessions.Snapshot().Count,
            ["invokeAllowed"] = _runtime.Options.AllowInvoke,
            ["liveAccess"] = _runtime.GrainFactory != null,
            ["distribution"] = _runtime.Distribution ?? "none: events and capture settings of this process only",
            ["sharedCaptureSettings"] = _runtime.Control != null,
            ["history"] = _runtime.History != null,
        };
    }

    public JsonObject Schema()
    {
        if (_runtime.BuildLocalSchema() is { } local) return local;
        if (_runtime.RemoteSchema is { } remote)
        {
            var copy = (JsonObject)remote.DeepClone();
            copy["note"] = "Method table published by a silo over the broker (standalone inspector: read-only, invoke_method unavailable). Arguments are in wire form.";
            return copy;
        }
        return new JsonObject
        {
            ["stateTypes"] = new JsonArray(),
            ["methods"] = new JsonArray(),
            ["note"] = "No method table in this process (standalone inspector before a silo has published one, or a server built without a call tap): service and method names still appear on events.",
        };
    }

    // ── events ─────────────────────────────────────────────────────────────────

    public List<MetaInspectorEvent> Recent(InspectorQuery query, bool includePayloads)
    {
        var events = _runtime.Store.Query(query);
        events.Reverse(); // newest first
        return includePayloads ? events : events.Select(e => e.Summary()).ToList();
    }

    /// <summary>Every event of one call, as a tree by span (roots first).</summary>
    public JsonObject CallTree(string callId, bool includePayloads)
    {
        var events = _runtime.Store.Query(new InspectorQuery { CallId = callId });
        var bySpan = new Dictionary<string, List<MetaInspectorEvent>>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.SpanId == null) continue;
            if (!bySpan.TryGetValue(e.SpanId, out var list)) bySpan[e.SpanId] = list = new();
            list.Add(e);
        }

        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var span in bySpan.Keys)
        {
            var parent = bySpan[span].Select(e => e.ParentSpanId).FirstOrDefault(p => p != null);
            if (parent != null && bySpan.ContainsKey(parent))
            {
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new();
                list.Add(span);
            }
            else roots.Add(span);
        }

        JsonNode Node(string span)
        {
            var spanEvents = bySpan[span].OrderBy(e => e.Id).ToList();
            return new JsonObject
            {
                ["spanId"] = span,
                ["parentSpanId"] = spanEvents.Select(e => e.ParentSpanId).FirstOrDefault(p => p != null),
                ["events"] = ToJson(spanEvents, includePayloads),
                ["children"] = new JsonArray((children.TryGetValue(span, out var c) ? c : new List<string>())
                    .OrderBy(s => bySpan[s].Min(e => e.Id)).Select(Node).ToArray()),
            };
        }

        return new JsonObject
        {
            ["callId"] = callId,
            ["events"] = events.Count,
            ["roots"] = new JsonArray(roots.OrderBy(s => bySpan[s].Min(e => e.Id)).Select(Node).ToArray()),
            ["unspanned"] = ToJson(events.Where(e => e.SpanId == null).ToList(), includePayloads),
            ["note"] = "A root whose parentSpanId is set but absent here is the transport hop of a client call (see client_responded) or an uncaptured caller.",
        };
    }

    /// <summary>The entity's own events in entity order, from the in-memory store only.</summary>
    public List<MetaInspectorEvent> Timeline(string entityKey, long? fromSequence, long? toSequence, int limit, bool includePayloads)
        => Shape(OwnEvents(StoreEvents(entityKey), entityKey), fromSequence, toSequence, limit, includePayloads);

    /// <summary>
    /// The entity's own events in entity order: what reached it, what it sent, who heard. Older
    /// events come from the broker's history when one is attached.
    /// </summary>
    public async Task<TimelineResult> TimelineAsync(string entityKey, long? fromSequence, long? toSequence, int limit,
        bool includePayloads, CancellationToken cancellationToken = default)
    {
        var (key, events, history) = await EntityEventsAsync(entityKey, cancellationToken).ConfigureAwait(false);
        return new TimelineResult(key, Shape(events, fromSequence, toSequence, limit, includePayloads), history);
    }

    /// <summary>
    /// Events of a timeline plus where they came from. <see cref="EntityKey"/> is the one the
    /// events belong to — the asked key, or its full form when the history resolved a short name.
    /// </summary>
    public sealed record TimelineResult(string EntityKey, List<MetaInspectorEvent> Events, JsonObject? History);

    /// <summary>
    /// State after <paramref name="sequence"/>: from the last event of that operation that carries
    /// state (after its triggers), else from the nearest earlier one — reported as not exact.
    /// From the in-memory store only.
    /// </summary>
    public JsonObject StateAt(string entityKey, long sequence)
        => StateAtOf(entityKey, sequence, OwnEvents(StoreEvents(entityKey), entityKey), null);

    /// <summary>Same as <see cref="StateAt"/>, reaching into the broker's history when one is attached.</summary>
    public async Task<JsonObject> StateAtAsync(string entityKey, long sequence, CancellationToken cancellationToken = default)
    {
        var (key, events, history) = await EntityEventsAsync(entityKey, cancellationToken).ConfigureAwait(false);
        return StateAtOf(key, sequence, events, history);
    }

    private static JsonObject StateAtOf(string entityKey, long sequence, List<MetaInspectorEvent> events, JsonObject? history)
    {
        var withState = InEntityOrder(events.Where(e => e.State != null && e.EntitySequence <= sequence)).LastOrDefault();
        return new JsonObject
        {
            ["entity"] = entityKey,
            ["requestedSeq"] = sequence,
            ["actualSeq"] = withState?.EntitySequence,
            ["isExact"] = withState != null && withState.EntitySequence == sequence,
            ["eventId"] = withState?.Id,
            ["state"] = withState?.State?.DeepClone(),
            ["history"] = history,
            ["note"] = withState == null ? "No captured state at or before this sequence — capture at level Full (watch_entity) to record state." : null,
        };
    }

    private List<MetaInspectorEvent> StoreEvents(string entityKey)
        => _runtime.Store.Query(new InspectorQuery { EntityKey = entityKey });

    private static List<MetaInspectorEvent> OwnEvents(IEnumerable<MetaInspectorEvent> events, string entityKey)
        => PlaceReplies(events.Where(e => e.EntityKey == entityKey).ToList());

    /// <summary>
    /// Store events of the entity, merged with the broker's history. The same event can be in
    /// both: matched by its stream position, or by content when one side never went through the
    /// broker (store ids are per process).
    /// </summary>
    private async Task<(string EntityKey, List<MetaInspectorEvent> Events, JsonObject? History)> EntityEventsAsync(
        string entityKey, CancellationToken cancellationToken)
    {
        var own = StoreEvents(entityKey).Where(e => e.EntityKey == entityKey).ToList();
        var history = _runtime.History;
        if (history == null || !MetaInspectorEvent.TrySplitEntityKey(entityKey, out var stateType, out var entityId))
            return (entityKey, PlaceReplies(own), null);

        JsonObject info;
        try
        {
            var (stored, truncated) = await history.ReadEntityAsync(stateType, entityId, _runtime.Options.MaxHistoryEvents, cancellationToken).ConfigureAwait(false);

            // A short name the store could not resolve: the history matched it by entity id.
            if (own.Count == 0 && stored.Count > 0 && stored[0].EntityKey is { } found && found != entityKey)
            {
                if (stored.Any(e => e.EntityKey != found))
                    return (entityKey, own, new JsonObject { ["error"] = $"'{stateType}' matches several state types for entity {entityId}; use the full name." });
                entityKey = found;
                _runtime.LearnStateType(stored[0].StateType!);
                own = StoreEvents(entityKey).Where(e => e.EntityKey == entityKey).ToList();
            }

            var seenSequences = new HashSet<ulong>(own.Where(e => e.BrokerSequence.HasValue).Select(e => e.BrokerSequence!.Value));
            var seenContent = new HashSet<string>(own.Where(e => !e.BrokerSequence.HasValue).Select(ContentKey), StringComparer.Ordinal);
            int added = 0;
            foreach (var e in stored)
            {
                if (e.EntityKey != entityKey) continue;
                var known = (e.BrokerSequence is { } seq && seenSequences.Contains(seq)) || seenContent.Contains(ContentKey(e));
                if (known) continue;
                own.Add(e);
                added++;
            }
            info = new JsonObject { ["read"] = stored.Count, ["olderThanStore"] = added, ["truncated"] = truncated };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // History is best effort: the store's events still answer.
            info = new JsonObject { ["error"] = ex.GetType().Name + ": " + ex.Message };
        }
        return (entityKey, PlaceReplies(own), info);
    }

    private static string ContentKey(MetaInspectorEvent e)
        => string.Join("|", e.Kind, e.EntitySequence, e.SubOrder, e.SpanId, e.StartedAtUtc.Ticks, e.Silo, e.CallId, e.TargetEntity, e.TargetPlayer,
            e.TargetPlayers == null ? "" : string.Join(",", e.TargetPlayers));

    /// <summary>
    /// A client reply is reported where the client is connected, not where the entity ran it, so it
    /// may reach the store before (or without) the dispatch it answers. Placed at query time: it
    /// takes the sequence of its dispatch and goes last in that operation.
    /// </summary>
    private static List<MetaInspectorEvent> PlaceReplies(List<MetaInspectorEvent> events)
    {
        Dictionary<string, long>? dispatches = null;
        foreach (var e in events)
        {
            if (e.Kind == InspectorEventKinds.CallCompleted && e.CallId != null && e.CallKind is "rpc" or "query" or "signal")
                (dispatches ??= new(StringComparer.Ordinal))[e.CallId] = e.EntitySequence;
        }
        if (dispatches == null) return events;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Kind != InspectorEventKinds.ClientResponded || e.CallId == null || !dispatches.TryGetValue(e.CallId, out var seq)) continue;
            if (e.EntitySequence == seq && e.SubOrder == InspectorRuntime.ReplySubOrder) continue;
            var placed = e.Clone();
            placed.EntitySequence = seq;
            placed.SubOrder = InspectorRuntime.ReplySubOrder;
            events[i] = placed;
        }
        return events;
    }

    private static IEnumerable<MetaInspectorEvent> InEntityOrder(IEnumerable<MetaInspectorEvent> events)
        => events.OrderBy(e => e.EntitySequence).ThenBy(e => e.SubOrder).ThenBy(e => e.StartedAtUtc).ThenBy(e => e.Id);

    private static List<MetaInspectorEvent> Shape(List<MetaInspectorEvent> events, long? fromSequence, long? toSequence, int limit, bool includePayloads)
    {
        var shaped = InEntityOrder(events
                .Where(e => fromSequence == null || e.EntitySequence >= fromSequence)
                .Where(e => toSequence == null || e.EntitySequence <= toSequence))
            .ToList();
        if (limit > 0 && shaped.Count > limit) shaped = shaped.GetRange(shaped.Count - limit, limit);
        return includePayloads ? shaped : shaped.Select(e => e.Summary()).ToList();
    }

    /// <summary>
    /// Entities and players around <paramref name="entityKey"/>, from calls, cross-entity hops and
    /// broadcasts in the newest <paramref name="window"/> events, up to <paramref name="depth"/> hops.
    /// </summary>
    public JsonObject Graph(string entityKey, int depth, int window)
    {
        var events = _runtime.Store.Query(new InspectorQuery { Limit = window > 0 ? window : 0 });
        var edges = new Dictionary<(string From, string To), (int Count, HashSet<string> Methods)>();

        void Edge(string? from, string? to, string? method)
        {
            if (from == null || to == null || from == to) return;
            var key = (from, to);
            if (!edges.TryGetValue(key, out var v)) v = (0, new HashSet<string>(StringComparer.Ordinal));
            if (method != null) v.Methods.Add(method);
            edges[key] = (v.Count + 1, v.Methods);
        }

        // A hop captured at both ends (call_sent on the sender, call_completed with CallerEntity on
        // the target) is one call: the sender's record counts, the target's only when it is alone.
        var sentHops = new HashSet<(string Call, string From, string To)>();
        foreach (var e in events)
        {
            if (e.Kind == InspectorEventKinds.CallSent && e.EntityKey != null && e.TargetEntity != null)
                sentHops.Add((e.CallId ?? "", e.EntityKey, e.TargetEntity));
        }

        foreach (var e in events)
        {
            var method = e.Service != null ? e.Service + "." + e.Method : e.Method;
            switch (e.Kind)
            {
                case InspectorEventKinds.CallSent:
                    Edge(e.EntityKey, e.TargetEntity, method);
                    break;
                case InspectorEventKinds.CallCompleted when e.CallKind == "rpc":
                    Edge(Player(e.CallerId), e.EntityKey, method);
                    break;
                case InspectorEventKinds.CallCompleted when e.CallerEntity != null && e.EntityKey != null:
                    if (!sentHops.Contains((e.CallId ?? "", e.CallerEntity, e.EntityKey)))
                        Edge(e.CallerEntity, e.EntityKey, method);
                    break;
                case InspectorEventKinds.BroadcastSent:
                    foreach (var p in e.TargetPlayers ?? (e.TargetPlayer != null ? new List<string> { e.TargetPlayer } : new()))
                        Edge(e.EntityKey, Player(p), "broadcast " + method);
                    break;
            }
        }

        // Breadth-first from the entity, following edges both ways.
        var reached = new HashSet<string>(StringComparer.Ordinal) { entityKey };
        var frontier = new List<string> { entityKey };
        for (int d = 0; d < Math.Max(1, depth) && frontier.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var (from, to) in edges.Keys)
            {
                if (frontier.Contains(from) && reached.Add(to)) next.Add(to);
                if (frontier.Contains(to) && reached.Add(from)) next.Add(from);
            }
            frontier = next;
        }

        var kept = edges.Where(p => reached.Contains(p.Key.From) && reached.Contains(p.Key.To)).ToList();
        var mermaid = new StringBuilder("flowchart LR\n");
        var ids = reached.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => "n" + x.i, StringComparer.Ordinal);
        foreach (var node in reached)
            mermaid.Append("  ").Append(ids[node]).Append(node.StartsWith("player:") ? "([\"" : "[\"").Append(Escape(Short(node)))
                .Append(node.StartsWith("player:") ? "\"])" : "\"]").Append('\n');
        foreach (var (key, value) in kept)
            mermaid.Append("  ").Append(ids[key.From]).Append(" -->|\"").Append(Escape(string.Join(", ", value.Methods.Take(3))))
                .Append(value.Count > 1 ? " ×" + value.Count : "").Append("\"| ").Append(ids[key.To]).Append('\n');

        return new JsonObject
        {
            ["center"] = entityKey,
            ["nodes"] = new JsonArray(reached.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["edges"] = new JsonArray(kept.Select(p => (JsonNode)new JsonObject
            {
                ["from"] = p.Key.From,
                ["to"] = p.Key.To,
                ["count"] = p.Value.Count,
                ["methods"] = new JsonArray(p.Value.Methods.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            }).ToArray()),
            ["mermaid"] = mermaid.ToString(),
            ["eventsScanned"] = events.Count,
        };
    }

    /// <summary>Mermaid sequence diagram of an entity's timeline.</summary>
    public string TimelineMermaid(string entityKey, IReadOnlyList<MetaInspectorEvent> events)
    {
        // Participants get short ids declared up front: names with quotes, colons or spaces are
        // not valid in message lines for every Mermaid renderer.
        var self = "E";
        var actors = new Dictionary<string, string>(StringComparer.Ordinal);
        var declarations = new StringBuilder();
        declarations.Append("  participant ").Append(self).Append(" as ").Append(Escape(Short(entityKey))).Append('\n');
        string Actor(string name)
        {
            if (actors.TryGetValue(name, out var id)) return id;
            id = "p" + (actors.Count + 1);
            actors[name] = id;
            declarations.Append("  participant ").Append(id).Append(" as ").Append(Escape(name)).Append('\n');
            return id;
        }

        var sb = new StringBuilder();
        foreach (var e in events)
        {
            var method = e.Method ?? (e.CallKind == "init" ? $"[MetaInit] v{e.FromVersion}→v{e.ToVersion}" : e.Kind);
            var note = $"#{e.EntitySequence}.{e.SubOrder} {method}" + (e.Result is null or "success" ? "" : " ✗ " + e.Result);
            switch (e.Kind)
            {
                case InspectorEventKinds.CallCompleted:
                    var from = e.CallKind == "rpc" ? "player " + e.CallerId
                        : e.CallerEntity != null ? Short(e.CallerEntity)
                        : e.CallKind is "trigger" or "nested" or "init" ? null
                        : "server";
                    if (from == null) sb.Append("  Note over ").Append(self).Append(": ").Append(Escape(note)).Append('\n');
                    else sb.Append("  ").Append(Actor(from)).Append("->>").Append(self).Append(": ").Append(Escape(note)).Append('\n');
                    break;
                case InspectorEventKinds.CallSent:
                    sb.Append("  ").Append(self).Append("->>").Append(Actor(Short(e.TargetEntity ?? "?"))).Append(": ").Append(Escape(note)).Append('\n');
                    break;
                case InspectorEventKinds.BroadcastSent:
                    sb.Append("  ").Append(self).Append("-->>").Append(Actor("subscribers")).Append(": ")
                        .Append(Escape($"#{e.EntitySequence} broadcast {method} ×{e.SubscriberCount ?? 1}")).Append('\n');
                    break;
                case InspectorEventKinds.ClientResponded:
                    sb.Append("  ").Append(self).Append("-->>").Append(Actor("player " + e.CallerId)).Append(": ")
                        .Append(Escape($"reply {method} {e.Result} {e.DurationMs:0.#}ms")).Append('\n');
                    break;
                default:
                    sb.Append("  Note over ").Append(self).Append(": ").Append(Escape(e.Kind + " " + (e.TargetPlayer ?? e.CallerId))).Append('\n');
                    break;
            }
        }
        return "sequenceDiagram\n" + declarations + sb;
    }

    private static string? Player(string? id) => id == null ? null : "player:" + id;

    private static string Short(string key)
    {
        if (key.StartsWith("player:", StringComparison.Ordinal)) return key;
        var colon = key.IndexOf(':');
        if (colon < 0) return key;
        var type = key.Substring(0, colon);
        var dot = type.LastIndexOf('.');
        return (dot >= 0 ? type.Substring(dot + 1) : type) + ":" + key.Substring(colon + 1);
    }

    private static string Escape(string s) => s.Replace("\"", "'").Replace("\n", " ");

    /// <summary>
    /// Event fields in camelCase, nulls left out. Payloads keep the game's own member names and
    /// parameter names exactly.
    /// </summary>
    public static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonArray ToJson(IEnumerable<MetaInspectorEvent> events, bool includePayloads)
        => new(events.Select(e => JsonSerializer.SerializeToNode(includePayloads ? e : e.Summary(), EventJson)).ToArray());

    // ── live entities ──────────────────────────────────────────────────────────

    /// <summary>
    /// Current state of an entity. Without <paramref name="activate"/> only entities seen by the
    /// inspector are read; reading still activates a deactivated one, and a never-persisted id
    /// with <paramref name="activate"/> yields a fresh default state.
    /// </summary>
    public async Task<JsonObject> LiveStateAsync(string stateType, string entityId, string? path, bool activate)
    {
        var key = EntityKey(stateType, entityId);
        if (!activate && !_runtime.WasSeen(key))
            throw new InvalidOperationException($"Entity {key} has not been seen by the inspector; pass activate=true to read it anyway (this activates the grain).");
        var grain = Grain(stateType, entityId);
        var bytes = await grain.GetEntityStateAsync();
        var state = bytes == null ? null : _runtime.DecodeState(_runtime.ResolveStateTypeName(stateType), bytes);
        return new JsonObject
        {
            ["entity"] = key,
            ["path"] = path,
            ["state"] = string.IsNullOrEmpty(path) ? state : Select(state, path!),
        };
    }

    /// <summary>Invokes a meta method on an entity as server code. Opt-in and allow-listed.</summary>
    public async Task<JsonObject> InvokeAsync(string stateType, string entityId, string service, string method,
        JsonElement? args, int? version, string? callerId, string? callerClientVersion)
    {
        var options = _runtime.Options;
        if (!options.AllowInvoke)
            throw new InvalidOperationException("invoke_method is disabled (InspectorOptions.AllowInvoke).");

        var descriptors = _runtime.Descriptors ?? throw new InvalidOperationException("No method table: the server was built without a call tap.");
        var serializer = _runtime.Serializer ?? throw new InvalidOperationException("No IMetaSerializer bound.");
        var candidates = descriptors.Methods
            .Where(d => d.ServiceName == service && (d.Alias == method || d.MethodName == method))
            .Where(d => version == null || d.Version == version)
            .OrderByDescending(d => d.Version)
            .ToList();
        var descriptor = candidates.FirstOrDefault()
            ?? throw new InvalidOperationException($"Unknown method {service}.{method}" + (version != null ? " v" + version : "") + ".");

        // Checked on the resolved method, so an allow-list entry by alias also covers a request by
        // C# name, and the other way round.
        if (!CaptureRules.MatchesAny(options.InvokeMethods, descriptor.ServiceName, descriptor.Alias)
            && !CaptureRules.MatchesAny(options.InvokeMethods, descriptor.ServiceName, descriptor.MethodName))
            throw new InvalidOperationException($"{descriptor.ServiceName}.{descriptor.Alias} is not in InspectorOptions.InvokeMethods.");

        var stateTypeName = descriptor.StateType.FullName!;
        if (!string.IsNullOrEmpty(stateType) && _runtime.ResolveStateTypeName(stateType) != stateTypeName)
            throw new InvalidOperationException($"{descriptor.ServiceName}.{descriptor.Alias} runs on {stateTypeName}, not on '{stateType}'.");

        var mode = _runtime.Signature?.GetMethodEntry(descriptor.MethodId)?.Mode;
        if (mode == ExecutionMode.LocalQuery)
            throw new InvalidOperationException($"{service}.{method} is a LocalQuery: it runs on the client only and has no server dispatch.");

        var values = ParseArgs(descriptor, args);
        var payload = descriptors.EncodeArgs(descriptor.MethodId, values, serializer);
        var trace = MetaTraceContext.CreateRandom();
        var call = new RpcCall
        {
            MethodId = descriptor.MethodId,
            Payload = payload,
            CallerId = callerId ?? "inspector",
            ServerTimeTicks = DateTime.UtcNow.Ticks,
            CallerClientVersion = callerClientVersion,
            TraceParent = trace.ToTraceParent(),
        };

        var grain = Grain(stateTypeName, entityId);
        string? error = null;
        ReadOnlyMemory<byte> resultBytes = default;
        switch (mode)
        {
            case ExecutionMode.Query:
                var q = await grain.HandleQueryFromEntityAsync(call);
                error = q.Success ? null : q.Error ?? "query failed";
                resultBytes = q.ResultBytes;
                break;
            case ExecutionMode.Signal:
                await grain.HandleSignalAsync(call);
                break;
            case ExecutionMode.Notification:
                await grain.HandleCallFromEntityOneWayAsync(call);
                break;
            default:
                var r = await grain.HandleCallFromEntityAsync(call);
                error = r.Error;
                resultBytes = r.ResultBytes;
                break;
        }

        JsonNode? result = null;
        if (error == null && !resultBytes.IsEmpty)
            result = _runtime.Json.ToNode(descriptors.DecodeResult(descriptor.MethodId, resultBytes, serializer), descriptor.ResultType);
        return new JsonObject
        {
            ["callId"] = trace.TraceIdHex,
            ["method"] = $"{descriptor.ServiceName}.{descriptor.Alias} v{descriptor.Version}",
            ["mode"] = mode?.ToString(),
            ["success"] = error == null,
            ["error"] = error,
            ["result"] = result,
            ["note"] = mode is ExecutionMode.Signal or ExecutionMode.Notification ? "Fire-and-forget: returned on dispatch, not completion." : null,
        };
    }

    private object?[] ParseArgs(MetaMethodDescriptor descriptor, JsonElement? args)
    {
        var parameters = descriptor.Parameters;
        var values = new object?[parameters.Count];
        if (parameters.Count == 0) return values;
        if (args is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new ArgumentException($"{descriptor.ServiceName}.{descriptor.Alias} needs arguments: {string.Join(", ", parameters.Select(p => p.Name))}.");

        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            JsonElement value;
            if (element.ValueKind == JsonValueKind.Array)
            {
                if (i >= element.GetArrayLength()) throw new ArgumentException($"Missing argument '{p.Name}'.");
                value = element[i];
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                if (!element.TryGetProperty(p.Name, out value)) throw new ArgumentException($"Missing argument '{p.Name}'.");
            }
            else throw new ArgumentException("args must be a JSON object keyed by parameter name, or an array in parameter order.");

            try { values[i] = value.Deserialize(p.WireType, _runtime.Json.Options); }
            catch (Exception ex) { throw new ArgumentException($"Argument '{p.Name}' is not a valid {p.WireType.Name}: {ex.Message}"); }
        }
        return values;
    }

    private IEntityGrainBase Grain(string stateType, string entityId)
    {
        var factory = _runtime.GrainFactory ?? throw new InvalidOperationException(
            "Live entity access needs a silo: this inspector has no grain factory (standalone, events only). Use the inspector of a silo for live state and invoke.");
        var resolver = _runtime.EntityGrainResolver ?? throw new InvalidOperationException("No entity grain resolver bound.");
        return resolver.GetEntityGrain(factory, _runtime.ResolveStateTypeName(stateType) ?? stateType, entityId)
               ?? throw new InvalidOperationException($"Unknown state type '{stateType}'.");
    }

    /// <summary>Dot / index path into a JSON value: <c>Inventory.Items[0].Count</c>.</summary>
    public static JsonNode? Select(JsonNode? node, string path)
    {
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw;
            while (part.Length > 0)
            {
                var bracket = part.IndexOf('[');
                var name = bracket < 0 ? part : part.Substring(0, bracket);
                if (name.Length > 0)
                {
                    if (node is not JsonObject obj) return null;
                    node = obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
                }
                if (bracket < 0) break;
                var close = part.IndexOf(']', bracket);
                if (close < 0 || node is not JsonArray arr || !int.TryParse(part.AsSpan(bracket + 1, close - bracket - 1), out var index)
                    || index < 0 || index >= arr.Count) return null;
                node = arr[index];
                part = part.Substring(close + 1);
            }
        }
        return node?.DeepClone();
    }
}
