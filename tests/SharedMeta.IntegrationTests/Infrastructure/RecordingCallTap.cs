using System.Collections.Concurrent;
using SharedMeta.Server.Core.Diagnostics;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Call tap registered on the test silo. Captures nothing until a test arms it for an entity id
/// prefix, so the rest of the suite runs through the tap wrappers with the tap idle.
/// </summary>
public sealed class RecordingCallTap : IMetaCallTap
{
    public static RecordingCallTap Instance { get; } = new();

    public sealed record Record(
        MetaDispatchKind Kind,
        ushort MethodId,
        string EntityId,
        string StateType,
        string? CallerId,
        long Sequence,
        byte[] Payload,
        byte[]? Result,
        string? Error,
        string? CallId,
        ulong SpanId,
        ulong ParentSpanId,
        byte[] StateAfter,
        int FromVersion,
        int ToVersion);

    private sealed class Pending
    {
        public MetaDispatchKind Kind;
        public ushort MethodId;
        public string EntityId = "";
        public string StateType = "";
        public string? CallerId;
        public byte[] Payload = Array.Empty<byte>();
        public string? CallId;
        public ulong SpanId;
        public ulong ParentSpanId;
        public IMetaCallTapTarget Target = null!;
    }

    private readonly ConcurrentQueue<Record> _records = new();
    private volatile string? _prefix;

    /// <summary>Failure injection — each must leave the call itself untouched.</summary>
    public volatile bool ThrowOnBegin;
    public volatile bool ThrowOnShouldCapture;
    public volatile bool ThrowOnEnd;

    /// <summary>Begin returns null: the provider must then skip End.</summary>
    public volatile bool DeclineInBegin;

    private int _endCalls;
    public int EndCalls => Volatile.Read(ref _endCalls);

    public IDisposable Arm(string entityIdPrefix)
    {
        Reset();
        _prefix = entityIdPrefix;
        return new Disarm(this);
    }

    public IReadOnlyList<Record> Records => _records.ToArray();

    public bool IsEnabled => _prefix != null;

    public bool ShouldCapture(MetaDispatchKind kind, ushort methodId, string entityId)
    {
        var prefix = _prefix;
        if (prefix == null || !entityId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (ThrowOnShouldCapture) throw new InvalidOperationException("tap failure injected by test");
        return true;
    }

    public object? Begin(in MetaTapCall call)
    {
        if (ThrowOnBegin) throw new InvalidOperationException("tap failure injected by test");
        if (DeclineInBegin) return null;
        var context = call.CallContext;
        return new Pending
        {
            Kind = call.Kind,
            MethodId = call.MethodId,
            EntityId = call.Target.EntityId,
            StateType = call.Target.StateTypeName,
            CallerId = call.CallerId,
            Payload = call.Payload.ToArray(),
            CallId = context?.CallId,
            SpanId = context?.Trace.SpanId ?? 0,
            ParentSpanId = context?.ParentSpanId ?? 0,
            Target = call.Target,
        };
    }

    public void EndCall(object scope, ReadOnlyMemory<byte> resultBytes, Exception? error)
        => Complete(scope, resultBytes.ToArray(), error, 0, 0);

    public void EndSignal(object scope, Exception? error) => Complete(scope, null, error, 0, 0);

    public void EndInit(object scope, int fromVersion, int toVersion, Exception? error)
        => Complete(scope, null, error, fromVersion, toVersion);

    private void Complete(object scope, byte[]? result, Exception? error, int fromVersion, int toVersion)
    {
        Interlocked.Increment(ref _endCalls);
        if (ThrowOnEnd) throw new InvalidOperationException("tap failure injected by test");
        if (scope is not Pending p) throw new InvalidOperationException("End reached with a foreign scope");
        _records.Enqueue(new Record(p.Kind, p.MethodId, p.EntityId, p.StateType, p.CallerId,
            p.Target.CurrentEntitySequence, p.Payload, result, error?.Message, p.CallId, p.SpanId, p.ParentSpanId,
            p.Target.GetStateBytes(), fromVersion, toVersion));
    }

    private void Reset()
    {
        ThrowOnBegin = ThrowOnShouldCapture = ThrowOnEnd = DeclineInBegin = false;
        Volatile.Write(ref _endCalls, 0);
        _records.Clear();
    }

    private sealed class Disarm(RecordingCallTap owner) : IDisposable
    {
        public void Dispose()
        {
            owner._prefix = null;
            owner.Reset();
        }
    }
}
