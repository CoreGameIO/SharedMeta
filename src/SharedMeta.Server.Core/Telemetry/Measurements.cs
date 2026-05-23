using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Disposable measurement helpers that bundle Activity span + duration histogram +
    /// outcome counter recording into a single <c>using</c>-friendly value type.
    /// <para>
    /// Pattern:
    /// <code>
    /// using var __m = SubscribeMeasurement.Start(typeof(TState).Name, _entityId, playerId);
    /// try { /* business logic */ }
    /// catch { __m.MarkError(); throw; }
    /// </code>
    /// On <see cref="IDisposable.Dispose"/>: records duration into the matching histogram,
    /// increments the count/active-gauge counters with the resolved outcome tag, and stamps
    /// the activity's result tag before disposing the activity.
    /// </para>
    /// <para>
    /// Mutable struct used only on the stack inside one method scope. Do NOT capture across
    /// async boundaries — the wrapping method body is always synchronous-prologue → single
    /// await chain → return, so scope is stable.
    /// </para>
    /// </summary>
    public struct SubscribeMeasurement : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private readonly string _stateType;
        private string _result;

        private SubscribeMeasurement(Activity? activity, long startTimestamp, string stateType)
        {
            _activity = activity;
            _startTimestamp = startTimestamp;
            _stateType = stateType;
            _result = "success";
        }

        public static SubscribeMeasurement Start(string stateType, string entityId, string playerId)
        {
            var activity = SharedMetaActivities.Source.StartActivity(SharedMetaActivities.SpanEntitySubscribe);
            if (activity != null)
            {
                activity.SetTag(SharedMetaActivities.TagStateType, stateType);
                activity.SetTag(SharedMetaActivities.TagEntityId, entityId);
                activity.SetTag(SharedMetaActivities.TagPlayerId, playerId);
            }
            return new SubscribeMeasurement(activity, Stopwatch.GetTimestamp(), stateType);
        }

        public void MarkError() => _result = "error";

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            SharedMetaMeters.SubscribeDuration.Record(elapsed,
                new KeyValuePair<string, object?>("state_type", _stateType),
                new KeyValuePair<string, object?>("result", _result));
            SharedMetaMeters.SubscribeCount.Add(1,
                new KeyValuePair<string, object?>("state_type", _stateType));
            if (_result == "success")
            {
                SharedMetaMeters.SubscribersActive.Add(1,
                    new KeyValuePair<string, object?>("state_type", _stateType));
            }
            _activity?.SetTag(SharedMetaActivities.TagResult, _result);
            _activity?.Dispose();
        }
    }

    /// <summary>
    /// Measurement wrapper for <c>EntityGrain.HandleCallAsync</c> — entity-rpc span, RPC
    /// duration histogram (service/method/result), and request-bytes histogram (service/method).
    /// </summary>
    public struct RpcMeasurement : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private readonly string _serviceName;
        private readonly string _methodName;
        private readonly int _payloadLength;
        private string _result;

        private RpcMeasurement(Activity? activity, long startTimestamp, string serviceName, string methodName, int payloadLength)
        {
            _activity = activity;
            _startTimestamp = startTimestamp;
            _serviceName = serviceName;
            _methodName = methodName;
            _payloadLength = payloadLength;
            _result = "success";
        }

        public static RpcMeasurement Start(string spanName, string serviceName, string methodName, string entityId, string? callerId, int payloadLength)
        {
            var activity = SharedMetaActivities.Source.StartActivity(spanName);
            if (activity != null)
            {
                activity.SetTag(SharedMetaActivities.TagService, serviceName);
                activity.SetTag(SharedMetaActivities.TagMethod, methodName);
                activity.SetTag(SharedMetaActivities.TagEntityId, entityId);
                if (callerId != null)
                    activity.SetTag(SharedMetaActivities.TagPlayerId, callerId);
            }
            return new RpcMeasurement(activity, Stopwatch.GetTimestamp(), serviceName, methodName, payloadLength);
        }

        public void MarkError() => _result = "error";

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            SharedMetaMeters.RpcDuration.Record(elapsed,
                new KeyValuePair<string, object?>("service", _serviceName),
                new KeyValuePair<string, object?>("method", _methodName),
                new KeyValuePair<string, object?>("result", _result));
            if (_payloadLength > 0)
            {
                SharedMetaMeters.RpcRequestBytes.Record(_payloadLength,
                    new KeyValuePair<string, object?>("service", _serviceName),
                    new KeyValuePair<string, object?>("method", _methodName));
            }
            _activity?.SetTag(SharedMetaActivities.TagResult, _result);
            _activity?.Dispose();
        }
    }

    /// <summary>
    /// Measurement wrapper for cross-entity dispatch (both awaited and OneWay paths). Lands
    /// in <c>CrossEntityCallDuration</c> / <c>CrossEntityCallCount</c> with a "kind" tag
    /// ("normal" vs "notification") for the dual-mode breakdown.
    /// </summary>
    public struct CrossEntityCallMeasurement : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private readonly string _serviceName;
        private readonly string _kind;
        private string _result;

        private CrossEntityCallMeasurement(Activity? activity, long startTimestamp, string serviceName, string kind)
        {
            _activity = activity;
            _startTimestamp = startTimestamp;
            _serviceName = serviceName;
            _kind = kind;
            _result = "success";
        }

        public static CrossEntityCallMeasurement Start(string serviceName, string methodName, string entityId, string kind)
        {
            var activity = SharedMetaActivities.Source.StartActivity(SharedMetaActivities.SpanCrossEntityCall);
            if (activity != null)
            {
                activity.SetTag(SharedMetaActivities.TagService, serviceName);
                activity.SetTag(SharedMetaActivities.TagMethod, methodName);
                activity.SetTag(SharedMetaActivities.TagKind, kind);
                activity.SetTag(SharedMetaActivities.TagEntityId, entityId);
            }
            return new CrossEntityCallMeasurement(activity, Stopwatch.GetTimestamp(), serviceName, kind);
        }

        public void MarkError() => _result = "error";

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            SharedMetaMeters.CrossEntityCallDuration.Record(elapsed,
                new KeyValuePair<string, object?>("to_service", _serviceName),
                new KeyValuePair<string, object?>("kind", _kind),
                new KeyValuePair<string, object?>("result", _result));
            SharedMetaMeters.CrossEntityCallCount.Add(1,
                new KeyValuePair<string, object?>("to_service", _serviceName),
                new KeyValuePair<string, object?>("kind", _kind));
            _activity?.SetTag(SharedMetaActivities.TagResult, _result);
            _activity?.Dispose();
        }
    }

    /// <summary>
    /// Measurement wrapper for <c>MetaConnectionHandler.RpcCallAsync</c> — end-to-end
    /// server-side RPC duration histogram (transport entry → SessionManager → grain →
    /// response). Diff against <c>RpcDuration</c> surfaces queue / hop overhead.
    /// <para>
    /// 0.24.0+ Keyed by client-local <c>ushort MethodId</c> only — no service/method name
    /// resolution on the hot path. Result tag is set via <see cref="MarkRejected"/> /
    /// <see cref="MarkError"/>; defaults to "success".
    /// </para>
    /// </summary>
    public struct ServerRpcTotalMeasurement : IDisposable
    {
        private readonly long _startTimestamp;
        private readonly ushort _methodId;
        private string _result;

        private ServerRpcTotalMeasurement(long startTimestamp, ushort methodId)
        {
            _startTimestamp = startTimestamp;
            _methodId = methodId;
            _result = "success";
        }

        public static ServerRpcTotalMeasurement Start(ushort methodId)
            => new ServerRpcTotalMeasurement(Stopwatch.GetTimestamp(), methodId);

        public void MarkError() => _result = "error";
        public void MarkRejected() => _result = "rejected";
        public void MarkBadRequest() => _result = "bad_request";

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            SharedMetaMeters.ServerRpcTotalDuration.Record(elapsed,
                new KeyValuePair<string, object?>("method_id", _methodId),
                new KeyValuePair<string, object?>("result", _result));
        }
    }

    /// <summary>
    /// Measurement wrapper for <c>MetaConnectionHandler.SessionConnectAsync</c>: starts the
    /// <c>SpanSessionConnect</c> activity, records the connect-handshake duration histogram,
    /// and bumps the active-session gauge when the outcome is success.
    /// <para>
    /// Outcome tag is set via <see cref="MarkResult"/> with one of
    /// <c>success | needs_signature_registration | rejected | error</c>; defaults to
    /// <c>rejected</c> so an early-return without an explicit mark surfaces as a rejected
    /// session (which is what the original inline tracking inferred from a null/Success=false
    /// response).
    /// </para>
    /// </summary>
    public struct SessionConnectMeasurement : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private string _result;

        private SessionConnectMeasurement(Activity? activity, long startTimestamp)
        {
            _activity = activity;
            _startTimestamp = startTimestamp;
            _result = "rejected";
        }

        public static SessionConnectMeasurement Start(string playerId)
        {
            var activity = SharedMetaActivities.Source.StartActivity(SharedMetaActivities.SpanSessionConnect);
            activity?.SetTag(SharedMetaActivities.TagPlayerId, playerId);
            return new SessionConnectMeasurement(activity, Stopwatch.GetTimestamp());
        }

        /// <summary>Set the outcome tag — one of <c>success | needs_signature_registration | rejected | error</c>.</summary>
        public void MarkResult(string result) => _result = result;

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            SharedMetaMeters.SessionConnectDuration.Record(elapsed,
                new KeyValuePair<string, object?>("result", _result));
            if (_result == "success")
                MetricEvents.Session.Started();
            _activity?.SetTag(SharedMetaActivities.TagResult, _result);
            _activity?.Dispose();
        }
    }

    /// <summary>
    /// Measurement wrapper for <c>EntityGrain.PersistIfNeededImpl</c> — duration histogram
    /// keyed by state-type plus the span. No success/error split since write failures
    /// already escalate via the IPersistentState
    /// pipeline. Single-tag, single-sink, but bundled into a disposable for consistency
    /// with the other grain measurements.
    /// </summary>
    public struct PersistenceWriteMeasurement : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private readonly string _stateType;

        private PersistenceWriteMeasurement(Activity? activity, long startTimestamp, string stateType)
        {
            _activity = activity;
            _startTimestamp = startTimestamp;
            _stateType = stateType;
        }

        public static PersistenceWriteMeasurement Start(string stateType)
        {
            var activity = SharedMetaActivities.Source.StartActivity(SharedMetaActivities.SpanPersistenceWrite);
            activity?.SetTag(SharedMetaActivities.TagStateType, stateType);
            return new PersistenceWriteMeasurement(activity, Stopwatch.GetTimestamp(), stateType);
        }

        public void Dispose()
        {
            SharedMetaMeters.PersistenceWriteDuration.Record(
                Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds,
                new KeyValuePair<string, object?>("state_type", _stateType));
            _activity?.Dispose();
        }
    }
}
