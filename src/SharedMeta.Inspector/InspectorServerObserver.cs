using SharedMeta.Core.Diagnostics;
using SharedMeta.Server.Core.Telemetry;

namespace SharedMeta.Inspector;

/// <summary>
/// Transport-side view: the answer a client got for its call, and session lifetimes. Gated, so an
/// idle inspector does not make every client call build a <see cref="MetaCallInfo"/>.
/// </summary>
internal sealed class InspectorServerObserver : IMetaServerObserver, IMetaCallObservationGate
{
    private readonly InspectorRuntime _runtime;

    public InspectorServerObserver(InspectorRuntime runtime) => _runtime = runtime;

    public bool ObservesCalls => _runtime.Rules.IsActive;

    public void OnCallCompleted(in MetaCallInfo call)
    {
        var rules = _runtime.Rules;
        var level = rules.Resolve(rules.MethodLevel(call.Service, call.Method), call.EntityId, call.PlayerId);
        if (level == InspectorLevel.Off) return;

        _runtime.Enqueue(new RawEvent
        {
            Kind = InspectorEventKinds.ClientResponded,
            Level = InspectorLevel.Ids,
            CallId = string.IsNullOrEmpty(call.CallId) ? null : call.CallId,
            StateType = _runtime.ResolveStateTypeName(call.StateType),
            EntityId = call.EntityId,
            StartedAtUtc = DateTime.UtcNow.AddMilliseconds(-call.DurationMs),
            DurationMs = call.DurationMs,
            Service = call.Service,
            Method = call.Method,
            CallKind = call.Op,
            CallerId = call.PlayerId,
            TargetPlayer = call.PlayerId,
            Result = call.Result,
            ErrorKind = call.ErrorKind,
            SessionId = call.SessionId,
            ClientVersion = call.ClientVersion,
        });
    }

    public void OnSessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed)
    {
        _runtime.Sessions.Started(playerId, sessionId, clientVersion, resumed);
        if (!Wants(playerId)) return;
        _runtime.Enqueue(new RawEvent
        {
            Kind = InspectorEventKinds.SessionStarted,
            Level = InspectorLevel.Ids,
            StartedAtUtc = DateTime.UtcNow,
            CallerId = playerId,
            SessionId = sessionId,
            ClientVersion = clientVersion,
            Reason = resumed ? "resumed" : "new",
            Result = "success",
        });
    }

    public void OnSessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs)
    {
        _runtime.Sessions.Ended(sessionId);
        if (!Wants(playerId)) return;
        _runtime.Enqueue(new RawEvent
        {
            Kind = InspectorEventKinds.SessionEnded,
            Level = InspectorLevel.Ids,
            StartedAtUtc = DateTime.UtcNow.AddMilliseconds(-durationMs),
            DurationMs = durationMs,
            CallerId = playerId,
            SessionId = sessionId,
            Reason = reason.ToString(),
            Result = "success",
        });
    }

    private bool Wants(string playerId)
    {
        var rules = _runtime.Rules;
        return rules.AnyMethodLevel || rules.PlayerLevel(playerId) != InspectorLevel.Off;
    }
}

/// <summary>Sessions open on this host, keyed by session id (a player can briefly have two).</summary>
public sealed class SessionTracker
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);

    public sealed record SessionInfo(string PlayerId, string SessionId, string? ClientVersion, DateTime StartedAtUtc, bool Resumed);

    internal void Started(string playerId, string sessionId, string? clientVersion, bool resumed)
        => _sessions[sessionId] = new SessionInfo(playerId, sessionId, clientVersion, DateTime.UtcNow, resumed);

    internal void Ended(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public IReadOnlyList<SessionInfo> Snapshot() => _sessions.Values.OrderBy(s => s.StartedAtUtc).ToList();
}
