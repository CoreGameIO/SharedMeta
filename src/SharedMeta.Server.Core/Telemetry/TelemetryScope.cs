using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Everything one server hop does to make a call observable, as one <c>using</c>:
    /// optionally starts a span (child of the incoming trace), sets <see cref="MetaCallContext.Current"/>,
    /// opens the <c>ILogger</c> scope and, for paths without an <c>RpcCall</c>, puts the trace on the
    /// Orleans request context. Dispose undoes all of it in reverse order.
    /// <para>
    /// Mutable struct, stack-only: never copy, never pass, only <c>using var</c>.
    /// </para>
    /// </summary>
    public struct TelemetryScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly IDisposable? _logScope;
        private readonly MetaCallContext.CurrentScope _current;
        private readonly MetaCallContextPropagation.Scope _propagation;
        private readonly bool _propagates;
        private readonly Activity? _ambient;
        private readonly bool _restoreAmbient;

        private TelemetryScope(Activity? activity, IDisposable? logScope, MetaCallContext context, bool propagate,
            Activity? ambient, bool restoreAmbient)
        {
            _activity = activity;
            _logScope = logScope;
            _ambient = ambient;
            _restoreAmbient = restoreAmbient;
            Context = context;
            _current = context.Enter();
            _propagates = propagate;
            _propagation = propagate ? MetaCallContextPropagation.Push(context.Trace) : default;
        }

        /// <summary>The call context of this hop; its trace carries this hop's span id when a span was started.</summary>
        public MetaCallContext Context { get; }

        /// <summary>Span of this hop; null when no span was requested or nobody listens.</summary>
        public Activity? Activity => _activity;

        /// <summary>
        /// Starts the hop. <paramref name="parent"/> is the caller's trace (invalid → a new trace).
        /// With <paramref name="startSpan"/> false the hop logs under the parent's span — used for
        /// pass-through hops where a span of their own would only add cost.
        /// </summary>
        /// <param name="propagate">Write the trace onto Orleans <c>RequestContext</c> for outgoing grain calls
        /// that carry no <c>RpcCall</c> (subscribe / unsubscribe).</param>
        public static TelemetryScope Start(
            string spanName,
            ActivityKind kind,
            MetaTraceContext parent,
            ILogger? logger,
            bool logScopes,
            string? op = null,
            string? playerId = null,
            string? entityId = null,
            string? stateType = null,
            string? service = null,
            string? method = null,
            string? sessionId = null,
            long requestId = 0,
            bool startSpan = true,
            bool propagate = false)
        {
            Activity? activity = null;
            Activity? ambient = null;
            bool restoreAmbient = false;
            if (startSpan && SharedMetaActivities.Source.HasListeners())
            {
                if (parent.IsValid)
                    activity = SharedMetaActivities.Source.StartActivity(spanName, kind, ToActivityContext(parent));
                else
                {
                    // No parent means "new trace" — never the ambient Activity.Current: on the
                    // transport that is the WebSocket connection's / HTTP request's activity
                    // (every call on a connection would share one call id, and an HTTP
                    // traceparent header would bypass AcceptClientTraceContext = false).
                    ambient = Activity.Current;
                    if (ambient != null) Activity.Current = null;
                    activity = SharedMetaActivities.Source.StartActivity(spanName, kind);
                    if (ambient != null)
                    {
                        if (activity == null) Activity.Current = ambient;
                        else restoreAmbient = true;
                    }
                }
            }

            MetaTraceContext own;
            if (activity != null && activity.IdFormat == ActivityIdFormat.W3C
                && MetaTraceContext.TryParse(activity.Id, out var fromActivity))
                own = fromActivity;
            else if (!parent.IsValid)
                own = MetaTraceContext.CreateRandom();
            else
                own = startSpan ? parent.WithNewSpan() : parent;

            var context = new MetaCallContext(own, op, playerId, entityId, stateType, service, method, sessionId, requestId);
            if (activity != null) Tag(activity, context);

            var logScope = logScopes && logger != null ? logger.BeginScope(context) : null;
            return new TelemetryScope(activity, logScope, context, propagate, ambient, restoreAmbient);
        }

        public void Dispose()
        {
            if (_propagates) _propagation.Dispose();
            _logScope?.Dispose();
            _current.Dispose();
            _activity?.Dispose();
            // A root span stops back to its (null) parent; hand the flow its own activity back.
            if (_restoreAmbient) Activity.Current = _ambient;
        }

        /// <summary>
        /// Trace a grain hop continues: the one on its <c>RpcCall</c>, else the Orleans request
        /// context (subscribe path), else the ambient <see cref="System.Diagnostics.Activity.Current"/>
        /// (host tracing a background job). Invalid when none — the hop then starts a fresh trace,
        /// so direct <c>*ServerApi</c> calls and timers still get a call id.
        /// </summary>
        public static MetaTraceContext ResolveIncoming(string? traceParent)
        {
            if (MetaTraceContext.TryParse(traceParent, out var fromCall)) return fromCall;

            var fromContext = MetaCallContextPropagation.Read();
            if (fromContext.IsValid) return fromContext;

            var ambient = System.Diagnostics.Activity.Current;
            if (ambient != null && ambient.IdFormat == ActivityIdFormat.W3C
                && MetaTraceContext.TryParse(ambient.Id, out var fromAmbient))
                return fromAmbient;

            return default;
        }

        private static ActivityContext ToActivityContext(MetaTraceContext trace)
        {
            var traceParent = trace.ToTraceParent()!;
            return ActivityContext.TryParse(traceParent, null, out var parsed) ? parsed : default;
        }

        private static void Tag(Activity activity, MetaCallContext c)
        {
            if (c.PlayerId != null) activity.SetTag(SharedMetaActivities.TagPlayerId, c.PlayerId);
            if (c.EntityId != null) activity.SetTag(SharedMetaActivities.TagEntityId, c.EntityId);
            if (c.StateType != null) activity.SetTag(SharedMetaActivities.TagStateType, c.StateType);
            if (c.Service != null) activity.SetTag(SharedMetaActivities.TagService, c.Service);
            if (c.Method != null) activity.SetTag(SharedMetaActivities.TagMethod, c.Method);
            if (c.Op != null) activity.SetTag(SharedMetaActivities.TagOp, c.Op);
            if (c.RequestId != 0) activity.SetTag(SharedMetaActivities.TagRequestId, c.RequestId);
        }
    }
}
