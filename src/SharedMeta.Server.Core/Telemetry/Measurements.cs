using System;
using System.Collections.Generic;
using System.Diagnostics;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Disposable measurement helpers that bundle duration histogram + outcome counter
    /// recording into a single <c>using</c>-friendly value type.
    /// <para>
    /// Pattern:
    /// <code>
    /// using var __t = TelemetryScope.Start(...);           // span + log scope + call context
    /// using var __m = RpcMeasurement.Start(tags, len, __t.Activity);
    /// try { /* business logic */ }
    /// catch { __m.MarkError(MetaCallErrorKinds.Exception); throw; }
    /// </code>
    /// The span belongs to <see cref="TelemetryScope"/>; a measurement only stamps the result on it.
    /// Per-method tags come from <see cref="MethodTags"/>, so a method disabled for its own series
    /// is recorded as <c>method="_other"</c> everywhere.
    /// </para>
    /// <para>
    /// Mutable struct used only on the stack inside one method scope — never copied or passed.
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
            _result = MetaCallResults.Success;
        }

        public static SubscribeMeasurement Start(string stateType, Activity? activity)
            => new SubscribeMeasurement(activity, Stopwatch.GetTimestamp(), stateType);

        public void MarkError() => _result = MetaCallResults.Error;

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            var st = new KeyValuePair<string, object?>("state_type", _stateType);
            SharedMetaMeters.SubscribeDuration.Record(elapsed, st,
                new KeyValuePair<string, object?>("result", _result));
            SharedMetaMeters.SubscribeCount.Add(1, st);
            if (_result == MetaCallResults.Success)
                SharedMetaMeters.SubscribersActive.Add(1, st);
            _activity?.SetTag(SharedMetaActivities.TagResult, _result);
        }
    }

    /// <summary>
    /// Entity-grain method body measurement for RPC (<c>RpcDuration</c>, <c>RpcRequestBytes</c>,
    /// <c>RpcResponseBytes</c>), Query (<c>QueryDuration</c>) and Signal (<c>SignalCount</c>).
    /// </summary>
    public struct RpcMeasurement : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private readonly MethodTags _tags;
        private readonly Kind _kind;
        private readonly int _payloadLength;
        private int _responseLength;
        private string _result;
        private string? _errorKind;

        private enum Kind : byte { Rpc, Query, Signal }

        private RpcMeasurement(Activity? activity, MethodTags tags, Kind kind, int payloadLength)
        {
            _activity = activity;
            _startTimestamp = Stopwatch.GetTimestamp();
            _tags = tags;
            _kind = kind;
            _payloadLength = payloadLength;
            _responseLength = 0;
            _result = MetaCallResults.Success;
            _errorKind = null;
        }

        public static RpcMeasurement Start(MethodTags tags, int payloadLength, Activity? activity)
            => new RpcMeasurement(activity, tags, Kind.Rpc, payloadLength);

        public static RpcMeasurement StartQuery(MethodTags tags, Activity? activity)
            => new RpcMeasurement(activity, tags, Kind.Query, 0);

        public static RpcMeasurement StartSignal(MethodTags tags, Activity? activity)
            => new RpcMeasurement(activity, tags, Kind.Signal, 0);

        /// <summary>Flip the result to error; <paramref name="errorKind"/> is a <see cref="MetaCallErrorKinds"/> value.</summary>
        public void MarkError(string errorKind = MetaCallErrorKinds.Exception)
        {
            _result = MetaCallResults.Error;
            _errorKind = errorKind;
        }

        /// <summary>Response payload size, recorded into <c>RpcResponseBytes</c> on dispose (RPC only).</summary>
        public void SetResponseBytes(int length) => _responseLength = length;

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            var tags = new TagList { _tags.ServiceTag, _tags.MethodTag };

            switch (_kind)
            {
                case Kind.Rpc:
                {
                    var outcome = tags;
                    outcome.Add(_tags.ModeTag);
                    outcome.Add("result", _result);
                    if (_errorKind != null) outcome.Add("error_kind", _errorKind);
                    SharedMetaMeters.RpcDuration.Record(elapsed, outcome);
                    if (_payloadLength > 0) SharedMetaMeters.RpcRequestBytes.Record(_payloadLength, tags);
                    if (_responseLength > 0) SharedMetaMeters.RpcResponseBytes.Record(_responseLength, tags);
                    break;
                }
                case Kind.Query:
                {
                    var outcome = tags;
                    outcome.Add("result", _result);
                    if (_errorKind != null) outcome.Add("error_kind", _errorKind);
                    SharedMetaMeters.QueryDuration.Record(elapsed, outcome);
                    break;
                }
                case Kind.Signal:
                {
                    var outcome = tags;
                    outcome.Add("result", _result);
                    SharedMetaMeters.SignalCount.Add(1, outcome);
                    break;
                }
            }

            if (_activity != null)
            {
                _activity.SetTag(SharedMetaActivities.TagResult, _result);
                if (_errorKind != null)
                {
                    _activity.SetTag(SharedMetaActivities.TagErrorKind, _errorKind);
                    _activity.SetStatus(ActivityStatusCode.Error);
                }
            }
        }
    }

    /// <summary>
    /// Measurement wrapper for cross-entity dispatch on the target grain. Lands in
    /// <c>CrossEntityCallDuration</c> / <c>CrossEntityCallCount</c> with a "kind" tag
    /// ("normal" vs "notification") for the dual-mode breakdown.
    /// </summary>
    public struct CrossEntityCallMeasurement : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _startTimestamp;
        private readonly MethodTags _tags;
        private readonly string _kind;
        private string _result;

        private CrossEntityCallMeasurement(Activity? activity, MethodTags tags, string kind)
        {
            _activity = activity;
            _startTimestamp = Stopwatch.GetTimestamp();
            _tags = tags;
            _kind = kind;
            _result = MetaCallResults.Success;
        }

        public static CrossEntityCallMeasurement Start(MethodTags tags, string kind, Activity? activity)
        {
            activity?.SetTag(SharedMetaActivities.TagKind, kind);
            return new CrossEntityCallMeasurement(activity, tags, kind);
        }

        public void MarkError() => _result = MetaCallResults.Error;

        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            var toService = new KeyValuePair<string, object?>("to_service", _tags.ServiceName);
            var kind = new KeyValuePair<string, object?>("kind", _kind);
            SharedMetaMeters.CrossEntityCallDuration.Record(elapsed, toService, kind,
                new KeyValuePair<string, object?>("result", _result));
            SharedMetaMeters.CrossEntityCallCount.Add(1, toService, kind);
            if (_activity != null)
            {
                _activity.SetTag(SharedMetaActivities.TagResult, _result);
                if (_result != MetaCallResults.Success) _activity.SetStatus(ActivityStatusCode.Error);
            }
        }
    }

    /// <summary>
    /// End-to-end server-side RPC duration at the transport handler (transport entry →
    /// SessionManager → grain → response). Diff against <c>RpcDuration</c> surfaces queue /
    /// hop overhead. Tagged by server-resolved <c>service</c> / <c>method</c> (stable across
    /// client builds); set via <see cref="SetMethod"/> once the client id is translated —
    /// a call rejected before that is tagged <c>_unknown</c>.
    /// </summary>
    public struct ServerRpcTotalMeasurement : IDisposable
    {
        private readonly long _startTimestamp;
        private MethodTags _tags;
        private string _result;
        private string? _errorKind;

        private ServerRpcTotalMeasurement(long startTimestamp)
        {
            _startTimestamp = startTimestamp;
            _tags = MethodTags.UnknownMethod;
            _result = MetaCallResults.Success;
            _errorKind = null;
        }

        public static ServerRpcTotalMeasurement Start()
            => new ServerRpcTotalMeasurement(Stopwatch.GetTimestamp());

        public void SetMethod(MethodTags tags) => _tags = tags;

        public string Result => _result;
        public string? ErrorKind => _errorKind;
        public double ElapsedMs => Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

        public void MarkError(string errorKind = MetaCallErrorKinds.Exception)
        {
            _result = MetaCallResults.Error;
            _errorKind = errorKind;
        }

        public void MarkRejected(string errorKind = MetaCallErrorKinds.Rejected)
        {
            _result = MetaCallResults.Rejected;
            _errorKind = errorKind;
        }

        public void MarkBadRequest()
        {
            _result = MetaCallResults.BadRequest;
            _errorKind = MetaCallErrorKinds.BadRequest;
        }

        public void Dispose()
        {
            var tags = new TagList { _tags.ServiceTag, _tags.MethodTag, { "result", _result } };
            if (_errorKind != null) tags.Add("error_kind", _errorKind);
            SharedMetaMeters.ServerRpcTotalDuration.Record(ElapsedMs, tags);
        }
    }

    /// <summary>
    /// Measurement wrapper for <c>MetaConnectionHandler.SessionConnectAsync</c>: records the
    /// connect-handshake duration histogram and bumps the active-session gauge when the outcome
    /// is success.
    /// <para>
    /// Outcome tag is set via <see cref="MarkResult"/> with one of
    /// <c>success | needs_signature_registration | rejected | error</c>; defaults to
    /// <c>rejected</c> so an early-return without an explicit mark surfaces as a rejected
    /// session.
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

        public static SessionConnectMeasurement Start(Activity? activity)
            => new SessionConnectMeasurement(activity, Stopwatch.GetTimestamp());

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
        }
    }

    /// <summary>
    /// Measurement wrapper for <c>EntityGrain.PersistIfNeededImpl</c> — duration histogram
    /// keyed by state-type plus the span. No success/error split since write failures
    /// already escalate via the IPersistentState pipeline.
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
