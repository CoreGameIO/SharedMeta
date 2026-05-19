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
