using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;

namespace SharedMeta.Inspector;

/// <summary>
/// Outgoing grain-call filter: the inspector's view of what an entity sends — calls to other
/// entities, broadcasts to subscribers — and of subscriptions arriving at it. Runs on every grain
/// message of the silo, so the fast path is a flag check and a cached method lookup, with no
/// state machine.
/// </summary>
internal sealed class InspectorGrainCallFilter : IOutgoingGrainCallFilter
{
    private enum Role : byte { None, CallEntity, CallEntityOneWay, QueryEntity, Broadcast, Subscribe, Reclaim, Unsubscribe }

    private static readonly ConcurrentDictionary<MethodInfo, Role> s_roles = new();
    private static readonly ConcurrentDictionary<Type, string?> s_stateTypeOfGrain = new();

    private readonly InspectorRuntime _runtime;

    public InspectorGrainCallFilter(InspectorRuntime runtime) => _runtime = runtime;

    public Task Invoke(IOutgoingGrainCallContext context)
    {
        if (!_runtime.Rules.IsActive) return context.Invoke();
        var method = context.InterfaceMethod;
        if (method == null) return context.Invoke();
        var role = s_roles.GetOrAdd(method, Classify);
        return role switch
        {
            Role.None => context.Invoke(),
            Role.CallEntity or Role.CallEntityOneWay or Role.QueryEntity => CallEntityAsync(context, role),
            Role.Broadcast => Broadcast(context),
            _ => SubscriptionAsync(context, role),
        };
    }

    private static Role Classify(MethodInfo method)
    {
        var declaring = method.DeclaringType;
        if (declaring == typeof(IEntityGrainBase))
        {
            return method.Name switch
            {
                nameof(IEntityGrainBase.HandleCallFromEntityAsync) => Role.CallEntity,
                nameof(IEntityGrainBase.HandleCallFromEntityOneWayAsync) => Role.CallEntityOneWay,
                nameof(IEntityGrainBase.HandleQueryFromEntityAsync) => Role.QueryEntity,
                nameof(IEntityGrainBase.SubscribeAsync) => Role.Subscribe,
                nameof(IEntityGrainBase.ReclaimSubscriptionAsync) => Role.Reclaim,
                nameof(IEntityGrainBase.UnsubscribeAsync) => Role.Unsubscribe,
                _ => Role.None,
            };
        }
        if (declaring == typeof(ISessionManagerReference) && method.Name == nameof(ISessionManagerReference.ReceiveBroadcastAsync))
            return Role.Broadcast;
        return Role.None;
    }

    // ── entity → entity ────────────────────────────────────────────────────────

    private Task CallEntityAsync(IOutgoingGrainCallContext context, Role role)
    {
        if (context.Request.GetArgument(0) is not RpcCall call) return context.Invoke();
        var rules = _runtime.Rules;
        var source = SourceEntityKey(context);
        var targetStateType = StateTypeOfReference(context.Grain);
        var targetEntityId = context.TargetId.Key.ToString();
        var target = targetStateType != null ? MetaInspectorEvent.MakeEntityKey(targetStateType, targetEntityId) : targetEntityId;

        var methodLevel = rules.MethodLevel(call.MethodId);
        var targetLevel = rules.Resolve(methodLevel, targetEntityId, call.CallerId);
        var level = CaptureRules.Max(targetLevel, rules.EntityLevel(InspectorCallTap.EntityIdOf(source)));
        if (level == InspectorLevel.Off) return context.Invoke();

        // Tell the target who called: its tap needs it for its own events and to capture on behalf
        // of a watched sender. Removed right after the send so it does not ride on later messages.
        bool setCaller = source != null;
        var previous = setCaller ? RequestContext.Get(InspectorCallTap.CallerEntityKey) : null;
        if (setCaller) RequestContext.Set(InspectorCallTap.CallerEntityKey, source!);

        // Nothing to record on the sender's side when it is not an entity (ServerApi, timers):
        // the target's own event shows a call with no caller entity.
        if (source == null)
        {
            try { return context.Invoke(); }
            finally { if (setCaller) Restore(previous); }
        }

        var raw = new RawEvent
        {
            Kind = InspectorEventKinds.CallSent,
            Level = level,
            StateType = StateTypeOfKey(source),
            EntityId = InspectorCallTap.EntityIdOf(source),
            StartedAtUtc = DateTime.UtcNow,
            StartTimestamp = Stopwatch.GetTimestamp(),
            MethodId = call.MethodId,
            CallKind = role == Role.QueryEntity ? "query" : role == Role.CallEntityOneWay ? "notification" : "cross_entity",
            CallerId = call.CallerId,
            TargetEntity = target,
        };
        StampCausality(raw, call.TraceParent);
        var frame = InspectorCallTap.CurrentFrame;
        if (frame != null && frame.EntityKey == source)
        {
            raw.Sequence = frame.Sequence;
            raw.SubOrder = ++frame.State.SubOrder;
        }
        if (level >= InspectorLevel.Args && !call.Payload.IsEmpty)
            _runtime.CopyPayload(call.Payload.Span, out raw.Payload, out raw.PayloadSkipped);

        Task sent;
        try { sent = context.Invoke(); }
        finally { if (setCaller) Restore(previous); }
        return CompleteAsync(context, sent, raw);
    }

    private async Task CompleteAsync(IOutgoingGrainCallContext context, Task sent, RawEvent raw)
    {
        try
        {
            await sent;
            var (error, resultBytes) = context.Result switch
            {
                CrossEntityCallReturn r => (r.Error, r.ResultBytes),
                QueryCallResponse q => (q.Success ? null : q.Error ?? "query failed", q.ResultBytes),
                _ => ((string?)null, ReadOnlyMemory<byte>.Empty),
            };
            raw.Result = error == null ? "success" : "error";
            raw.Error = error;
            if (raw.Level >= InspectorLevel.ArgsResult && error == null && !resultBytes.IsEmpty)
                _runtime.CopyPayload(resultBytes.Span, out raw.ResultBytes, out raw.ResultSkipped);
        }
        catch (Exception ex)
        {
            raw.Result = "error";
            raw.ErrorKind = ex.GetType().Name;
            raw.Error = ex.Message;
            throw;
        }
        finally
        {
            raw.DurationMs = Stopwatch.GetElapsedTime(raw.StartTimestamp).TotalMilliseconds;
            _runtime.Enqueue(raw);
        }
    }

    // ── broadcasts ─────────────────────────────────────────────────────────────

    private Task Broadcast(IOutgoingGrainCallContext context)
    {
        // ReceiveBroadcastAsync(entityId, stateTypeName, broadcast, entitySequenceNumber)
        var entityId = context.Request.GetArgument(0) as string;
        var stateType = context.Request.GetArgument(1) as string;
        var broadcast = context.Request.GetArgument(2) as EntityBroadcast;
        var sequence = context.Request.GetArgument(3) is long seq ? seq : 0;
        var player = context.TargetId.Key.ToString();
        if (entityId == null || broadcast == null) return context.Invoke();

        var rules = _runtime.Rules;
        var level = rules.Resolve(rules.MethodLevel(broadcast.MethodId), entityId, player);
        if (level == InspectorLevel.Off) return context.Invoke();

        var raw = new RawEvent
        {
            Kind = InspectorEventKinds.BroadcastSent,
            Level = InspectorLevel.Ids,   // a broadcast never carries state; the op bytes are not decoded
            StateType = stateType,
            EntityId = entityId,
            Sequence = sequence,
            // Sent once the operation and its triggers are done: last within the operation.
            SubOrder = BroadcastSubOrder,
            StartedAtUtc = DateTime.UtcNow,
            MethodId = broadcast.MethodId,
            TargetPlayer = player,
            Result = "success",
        };
        StampCausality(raw, null);
        // Per-subscriber detail only for a watched player; the encoder folds the rest per operation.
        raw.Reason = rules.PlayerLevel(player) != InspectorLevel.Off ? "watched" : null;
        _runtime.Enqueue(raw);
        return context.Invoke();
    }

    // ── subscriptions ──────────────────────────────────────────────────────────

    internal const int BroadcastSubOrder = int.MaxValue - 1;

    private Task SubscriptionAsync(IOutgoingGrainCallContext context, Role role)
    {
        var player = context.Request.GetArgument(0) as string;
        var entityId = context.TargetId.Key.ToString();
        var rules = _runtime.Rules;
        var level = rules.Resolve(rules.AnyMethodLevel ? InspectorLevel.Ids : InspectorLevel.Off, entityId, player);
        if (level == InspectorLevel.Off) return context.Invoke();
        return RecordSubscriptionAsync(context, role, player, StateTypeOfReference(context.Grain), entityId);
    }

    private async Task RecordSubscriptionAsync(IOutgoingGrainCallContext context, Role role, string? player, string? stateType, string entityId)
    {
        var raw = new RawEvent
        {
            Kind = role switch
            {
                Role.Subscribe => InspectorEventKinds.Subscribed,
                Role.Reclaim => InspectorEventKinds.Reclaimed,
                _ => InspectorEventKinds.Unsubscribed,
            },
            Level = InspectorLevel.Ids,
            StateType = stateType,
            EntityId = entityId,
            StartedAtUtc = DateTime.UtcNow,
            StartTimestamp = Stopwatch.GetTimestamp(),
            TargetPlayer = player,
        };
        StampCausality(raw, null);
        try
        {
            await context.Invoke();
            switch (context.Result)
            {
                case EntitySnapshot snapshot:
                    raw.Sequence = snapshot.CurrentSequenceNumber;
                    break;
                case SubscriptionResult result:
                    raw.Sequence = result.EntitySequenceNumber;
                    raw.Error = result.FailureReason;
                    raw.Reason = result.Status.ToString();
                    break;
            }
            raw.Result = raw.Error == null ? "success" : "error";
        }
        catch (Exception ex)
        {
            raw.Result = "error";
            raw.Error = ex.Message;
            throw;
        }
        finally
        {
            raw.DurationMs = Stopwatch.GetElapsedTime(raw.StartTimestamp).TotalMilliseconds;
            _runtime.Enqueue(raw);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The new hop's span is the sender's span: the target's hop names it as parent, which is the
    /// edge. Falls back to the RpcCall's trace for senders outside a meta call.
    /// </summary>
    private static void StampCausality(RawEvent raw, string? traceParent)
    {
        var current = MetaCallContext.Current;
        if (current != null && current.Trace.IsValid)
        {
            raw.CallId = current.CallId;
            raw.SpanId = current.Trace.SpanId;
            raw.ParentSpanId = current.ParentSpanId;
            return;
        }
        if (MetaTraceContext.TryParse(traceParent, out var trace))
        {
            raw.CallId = trace.TraceIdHex;
            raw.SpanId = trace.SpanId;
        }
    }

    private static string? SourceEntityKey(IOutgoingGrainCallContext context)
    {
        var grain = context.SourceContext?.GrainInstance;
        if (grain is not IEntityGrainBase) return null;
        var stateType = s_stateTypeOfGrain.GetOrAdd(grain.GetType(), StateTypeOfGrainType);
        var id = context.SourceId?.Key.ToString();
        return stateType == null || id == null ? null : MetaInspectorEvent.MakeEntityKey(stateType, id);
    }

    private static string? StateTypeOfGrainType(Type grainType)
    {
        for (var t = grainType; t != null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(EntityGrain<>))
                return t.GetGenericArguments()[0].FullName;
        }
        return null;
    }

    // The grain reference is a generated proxy implementing IEntityGrain<TState>.
    private static string? StateTypeOfReference(object? reference)
        => reference == null ? null : s_stateTypeOfReference.GetOrAdd(reference.GetType(), static type =>
        {
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEntityGrain<>))
                    return iface.GetGenericArguments()[0].FullName;
            }
            return null;
        });

    private static readonly ConcurrentDictionary<Type, string?> s_stateTypeOfReference = new();

    private static string? StateTypeOfKey(string entityKey)
    {
        var i = entityKey.IndexOf(':');
        return i < 0 ? null : entityKey.Substring(0, i);
    }

    private static void Restore(object? previous)
    {
        if (previous == null) RequestContext.Remove(InspectorCallTap.CallerEntityKey);
        else RequestContext.Set(InspectorCallTap.CallerEntityKey, previous);
    }
}
