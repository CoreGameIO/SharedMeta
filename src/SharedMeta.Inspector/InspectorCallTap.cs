using System.Diagnostics;
using Orleans.Runtime;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Server.Core.Diagnostics;

namespace SharedMeta.Inspector;

/// <summary>
/// What a capture point hands to the encoder: identity and raw bytes, copied on the grain turn.
/// Decoding and JSON happen later on the background encoder.
/// </summary>
internal sealed class RawEvent
{
    public string Kind = "";
    public InspectorLevel Level;
    public string? CallId;
    public ulong SpanId;
    public ulong ParentSpanId;
    public string? StateType;
    public string? EntityId;
    public long Sequence;
    public int SubOrder;
    public DateTime StartedAtUtc;
    public long StartTimestamp;
    public double DurationMs;
    public ushort? MethodId;
    public string? Service;
    public string? Method;
    public string? CallKind;
    public string? CallerId;
    public string? CallerEntity;
    public string? TargetEntity;
    public string? TargetPlayer;
    public string? Result;
    public string? ErrorKind;
    public string? Error;
    public byte[]? Payload;
    public int PayloadSkipped;
    public byte[]? ResultBytes;
    public int ResultSkipped;
    public byte[]? StateBytes;
    public int StateSkipped;
    public int? FromVersion;
    public int? ToVersion;
    public string? SessionId;
    public string? ClientVersion;
    public string? Reason;
}

/// <summary>
/// The inspector's <see cref="IMetaCallTap"/>. Runs on the grain turn: decides, copies the bytes it
/// keeps, stamps causality, and queues. Everything else is the encoder's job.
/// </summary>
internal sealed class InspectorCallTap : IMetaCallTap
{
    /// <summary>Orleans request-context key with the calling entity's key, set by the outgoing filter.</summary>
    internal const string CallerEntityKey = "sm.ie";

    private readonly InspectorRuntime _runtime;

    public InspectorCallTap(InspectorRuntime runtime) => _runtime = runtime;

    /// <summary>
    /// The dispatch in progress on this async flow, so the outgoing filter can place what the
    /// entity sends inside the operation that sent it (sequence, order).
    /// </summary>
    internal sealed class Frame(string entityKey, long sequence, EntityState state, Frame? previous)
    {
        public readonly string EntityKey = entityKey;
        public readonly long Sequence = sequence;
        public readonly EntityState State = state;
        public readonly Frame? Previous = previous;
        public volatile bool Ended;
    }

    private static readonly AsyncLocal<Frame?> s_frame = new();

    /// <summary>
    /// Innermost dispatch still running. Begin sets the frame on the caller's flow, but a call that
    /// completes asynchronously ends inside its own async method, whose restore of the AsyncLocal
    /// does not reach the caller — so ended frames are skipped, not trusted to be unset.
    /// </summary>
    internal static Frame? CurrentFrame
    {
        get
        {
            var f = s_frame.Value;
            while (f is { Ended: true }) f = f.Previous;
            return f;
        }
    }

    /// <summary>Per-entity bookkeeping kept in <see cref="IMetaCallTapTarget.TapState"/>.</summary>
    internal sealed class EntityState
    {
        public long Sequence = long.MinValue;
        public int SubOrder;
        public ulong RootSpanId;
        public string? RootCallId;
    }

    public bool IsEnabled => _runtime.Rules.IsActive;

    public bool ShouldCapture(MetaDispatchKind kind, ushort methodId, string entityId)
    {
        var rules = _runtime.Rules;
        if (!rules.IsActive) return false;
        // Player watches and watched calling entities are known only in Begin (which declines).
        return rules.HasPlayerWatches
               || (rules.HasEntityWatches && kind is MetaDispatchKind.CrossEntity or MetaDispatchKind.Query)
               || rules.EntityLevel(entityId) != InspectorLevel.Off
               || (kind == MetaDispatchKind.Init ? rules.AnyMethodLevel : rules.MethodLevel(methodId) != InspectorLevel.Off);
    }

    public object? Begin(in MetaTapCall call)
    {
        var rules = _runtime.Rules;
        var target = call.Target;
        var methodLevel = call.Kind == MetaDispatchKind.Init
            ? (rules.AnyMethodLevel ? rules.Settings.Level : InspectorLevel.Off)
            : rules.MethodLevel(call.MethodId);
        bool fromEntity = call.Kind is MetaDispatchKind.CrossEntity or MetaDispatchKind.Query;
        var callerEntity = fromEntity ? RequestContext.Get(CallerEntityKey) as string : null;
        var level = rules.Resolve(methodLevel, target.EntityId, call.CallerId, EntityIdOf(callerEntity));
        if (level == InspectorLevel.Off) return null;

        var es = target.TapState as EntityState;
        if (es == null) target.TapState = es = new EntityState();
        var sequence = target.CurrentEntitySequence;
        if (sequence != es.Sequence)
        {
            es.Sequence = sequence;
            es.SubOrder = 0;
        }

        var context = call.CallContext;
        ulong spanId, parentSpanId;
        string? callId = context?.CallId;
        switch (call.Kind)
        {
            case MetaDispatchKind.Trigger:
            case MetaDispatchKind.Nested:
                // Runs inside the operation of the call that caused it: a child span of that call.
                spanId = NewSpanId();
                parentSpanId = es.RootSpanId;
                callId ??= es.RootCallId;
                break;
            case MetaDispatchKind.Init:
                spanId = NewSpanId();
                parentSpanId = context?.Trace.SpanId ?? 0;
                break;
            default:
                spanId = context?.Trace.IsValid == true ? context.Trace.SpanId : NewSpanId();
                parentSpanId = context?.ParentSpanId ?? 0;
                es.RootSpanId = spanId;
                es.RootCallId = callId;
                break;
        }

        var raw = new RawEvent
        {
            Kind = InspectorEventKinds.CallCompleted,
            Level = level,
            CallId = string.IsNullOrEmpty(callId) ? null : callId,
            SpanId = spanId,
            ParentSpanId = parentSpanId,
            StateType = target.StateTypeName,
            EntityId = target.EntityId,
            Sequence = sequence,
            SubOrder = ++es.SubOrder,
            StartedAtUtc = DateTime.UtcNow,
            StartTimestamp = Stopwatch.GetTimestamp(),
            MethodId = call.Kind == MetaDispatchKind.Init ? null : call.MethodId,
            CallKind = KindName(call.Kind),
            CallerId = call.CallerId,
            CallerEntity = callerEntity,
        };
        if (level >= InspectorLevel.Args && !call.Payload.IsEmpty)
            _runtime.CopyPayload(call.Payload.Span, out raw.Payload, out raw.PayloadSkipped);

        var frame = new Frame(MetaInspectorEvent.MakeEntityKey(target.StateTypeName, target.EntityId), sequence, es, CurrentFrame);
        s_frame.Value = frame;
        return new Scope(raw, target, frame);
    }

    private sealed class Scope(RawEvent raw, IMetaCallTapTarget target, Frame frame)
    {
        public readonly RawEvent Raw = raw;
        public readonly IMetaCallTapTarget Target = target;
        public readonly Frame Frame = frame;
    }

    private static void Leave(Scope s)
    {
        s.Frame.Ended = true;
        s_frame.Value = s.Frame.Previous;
    }

    public void EndCall(object scope, ReadOnlyMemory<byte> resultBytes, Exception? error)
    {
        var s = (Scope)scope;
        Leave(s);
        var raw = s.Raw;
        Finish(raw, error);
        if (raw.Level >= InspectorLevel.ArgsResult && error == null && !resultBytes.IsEmpty)
            _runtime.CopyPayload(resultBytes.Span, out raw.ResultBytes, out raw.ResultSkipped);
        CaptureState(s, raw);
        _runtime.Enqueue(raw);
    }

    public void EndSignal(object scope, Exception? error)
    {
        var s = (Scope)scope;
        Leave(s);
        Finish(s.Raw, error);
        CaptureState(s, s.Raw);
        _runtime.Enqueue(s.Raw);
    }

    public void EndInit(object scope, int fromVersion, int toVersion, Exception? error)
    {
        var s = (Scope)scope;
        Leave(s);
        // The init check runs on every call; only a run that moved the schema (or failed) is news.
        if (error == null && fromVersion == toVersion) return;
        Finish(s.Raw, error);
        s.Raw.FromVersion = fromVersion;
        s.Raw.ToVersion = toVersion;
        CaptureState(s, s.Raw);
        _runtime.Enqueue(s.Raw);
    }

    private static void Finish(RawEvent raw, Exception? error)
    {
        raw.DurationMs = Stopwatch.GetElapsedTime(raw.StartTimestamp).TotalMilliseconds;
        if (error == null)
        {
            raw.Result = "success";
            return;
        }
        raw.Result = "error";
        raw.ErrorKind = error.GetType().Name;
        raw.Error = error.Message;
    }

    // Packed on the grain turn, so the snapshot is exactly the state at the end of the dispatch.
    private void CaptureState(Scope s, RawEvent raw)
    {
        if (raw.Level < InspectorLevel.Full || raw.CallKind == "query") return;
        var bytes = s.Target.GetStateBytes();
        if (bytes.Length <= _runtime.Options.MaxStateSnapshotBytes) raw.StateBytes = bytes;
        else raw.StateSkipped = bytes.Length;
    }

    internal static string KindName(MetaDispatchKind kind) => kind switch
    {
        MetaDispatchKind.Rpc => "rpc",
        MetaDispatchKind.CrossEntity => "cross_entity",
        MetaDispatchKind.Trigger => "trigger",
        MetaDispatchKind.Nested => "nested",
        MetaDispatchKind.Query => "query",
        MetaDispatchKind.Signal => "signal",
        MetaDispatchKind.Init => "init",
        _ => kind.ToString().ToLowerInvariant(),
    };

    internal static string? EntityIdOf(string? entityKey)
    {
        if (entityKey == null) return null;
        var i = entityKey.IndexOf(':');
        return i < 0 ? entityKey : entityKey.Substring(i + 1);
    }

    private static ulong NewSpanId() => MetaTraceContext.CreateRandom().SpanId;
}
