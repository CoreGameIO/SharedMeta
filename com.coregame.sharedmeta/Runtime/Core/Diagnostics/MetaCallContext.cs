using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>Structured log field names carried by <see cref="MetaCallContext"/> scopes.</summary>
    public static class MetaLogFields
    {
        public const string CallId    = "CallId";
        public const string SpanId    = "SpanId";
        public const string PlayerId  = "PlayerId";
        public const string SessionId = "SessionId";
        // Not "RequestId": ASP.NET Core's hosting scope already uses that name for the HTTP request.
        public const string RequestId = "ClientRequestId";
        public const string EntityId  = "EntityId";
        public const string StateType = "StateType";
        public const string Service   = "Service";
        public const string Method    = "Method";
        public const string Op        = "Op";
    }

    /// <summary>Values of <see cref="MetaCallContext.Op"/>.</summary>
    public static class MetaCallOps
    {
        public const string Rpc            = "rpc";
        public const string Query          = "query";
        public const string Signal         = "signal";
        public const string Subscribe      = "subscribe";
        public const string Unsubscribe    = "unsubscribe";
        public const string SessionConnect = "session_connect";
        public const string CrossEntity    = "cross_entity";
        public const string Desync         = "desync_report";
        public const string ServerApi      = "server_api";
    }

    /// <summary>
    /// Identity of the meta call being executed: trace (whose trace id is the call id), caller,
    /// target and method. Immutable; a hop that changes any field makes a new instance via
    /// <see cref="With"/>.
    /// <para>
    /// Doubles as the state object of <c>ILogger.BeginScope</c>: it is the
    /// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> logging providers expand
    /// into structured properties, so opening a scope allocates nothing beyond the scope itself.
    /// Null fields are left out.
    /// </para>
    /// </summary>
    public sealed class MetaCallContext : IReadOnlyList<KeyValuePair<string, object?>>
    {
        // AsyncLocal, not a MetaContext field: one MetaContext serves every call of its grain,
        // and calls of one grain can overlap in time (interleaving, reentrant grains).
        private static readonly AsyncLocal<MetaCallContext?> _current = new();

        /// <summary>Call context of the current async flow, or null outside a meta call.</summary>
        public static MetaCallContext? Current
        {
            get => _current.Value;
            set => _current.Value = value;
        }

        private string? _callId;
        private KeyValuePair<string, object?>[]? _fields;

        public MetaCallContext(
            MetaTraceContext trace,
            string? op = null,
            string? playerId = null,
            string? entityId = null,
            string? stateType = null,
            string? service = null,
            string? method = null,
            string? sessionId = null,
            long requestId = 0)
        {
            Trace = trace;
            Op = op;
            PlayerId = playerId;
            EntityId = entityId;
            StateType = stateType;
            Service = service;
            Method = method;
            SessionId = sessionId;
            RequestId = requestId;
        }

        public MetaTraceContext Trace { get; }

        /// <summary>Correlation id of the call: the W3C trace id as 32 hex chars, empty without a trace.</summary>
        public string CallId => _callId ??= Trace.IsValid ? Trace.TraceIdHex : "";

        public string? Op { get; }
        public string? PlayerId { get; }
        public string? EntityId { get; }
        public string? StateType { get; }
        public string? Service { get; }
        public string? Method { get; }
        public string? SessionId { get; }

        /// <summary>Client's per-session request id, 0 when the call has none.</summary>
        public long RequestId { get; }

        /// <summary>Copy with the given fields replaced; null keeps the current value.</summary>
        public MetaCallContext With(
            MetaTraceContext? trace = null,
            string? op = null,
            string? playerId = null,
            string? entityId = null,
            string? stateType = null,
            string? service = null,
            string? method = null,
            string? sessionId = null,
            long? requestId = null)
            => new MetaCallContext(
                trace ?? Trace,
                op ?? Op,
                playerId ?? PlayerId,
                entityId ?? EntityId,
                stateType ?? StateType,
                service ?? Service,
                method ?? Method,
                sessionId ?? SessionId,
                requestId ?? RequestId);

        /// <summary>
        /// Sets <see cref="Current"/> and returns a token that restores the previous value on
        /// dispose. Use with <c>using</c> so the flow never leaks into the next call.
        /// </summary>
        public CurrentScope Enter() => new CurrentScope(this);

        // ── IReadOnlyList — structured log scope ────────────────────────────────

        public int Count => Fields.Length;

        public KeyValuePair<string, object?> this[int index] => Fields[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => ((IEnumerable<KeyValuePair<string, object?>>)Fields).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private KeyValuePair<string, object?>[] Fields => _fields ??= BuildFields();

        private KeyValuePair<string, object?>[] BuildFields()
        {
            var list = new List<KeyValuePair<string, object?>>(10);
            if (Trace.IsValid)
            {
                list.Add(new KeyValuePair<string, object?>(MetaLogFields.CallId, CallId));
                list.Add(new KeyValuePair<string, object?>(MetaLogFields.SpanId, Trace.SpanIdHex));
            }
            Add(list, MetaLogFields.Op, Op);
            Add(list, MetaLogFields.PlayerId, PlayerId);
            Add(list, MetaLogFields.EntityId, EntityId);
            Add(list, MetaLogFields.StateType, StateType);
            Add(list, MetaLogFields.Service, Service);
            Add(list, MetaLogFields.Method, Method);
            Add(list, MetaLogFields.SessionId, SessionId);
            if (RequestId != 0)
                list.Add(new KeyValuePair<string, object?>(MetaLogFields.RequestId, RequestId));
            return list.ToArray();
        }

        private static void Add(List<KeyValuePair<string, object?>> list, string key, string? value)
        {
            if (value != null)
                list.Add(new KeyValuePair<string, object?>(key, value));
        }

        /// <summary>Compact text form for plain-text log sinks: <c>call=… player=… entity=… Svc.Method</c>.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder(96);
            if (Trace.IsValid) sb.Append("call=").Append(CallId);
            if (PlayerId != null) Sep(sb).Append("player=").Append(PlayerId);
            if (EntityId != null) Sep(sb).Append("entity=").Append(EntityId);
            if (Service != null || Method != null)
                Sep(sb).Append(Service ?? "?").Append('.').Append(Method ?? "?");
            else if (Op != null)
                Sep(sb).Append(Op);
            return sb.ToString();
        }

        private static StringBuilder Sep(StringBuilder sb) => sb.Length > 0 ? sb.Append(' ') : sb;

        /// <summary>Restores the previous <see cref="Current"/> on dispose.</summary>
        public readonly struct CurrentScope : IDisposable
        {
            private readonly MetaCallContext? _previous;

            internal CurrentScope(MetaCallContext context)
            {
                _previous = _current.Value;
                _current.Value = context;
            }

            public void Dispose() => _current.Value = _previous;
        }
    }
}
