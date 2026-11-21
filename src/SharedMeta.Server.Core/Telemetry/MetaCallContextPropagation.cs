using System;
using Orleans.Runtime;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Carries the trace across grain calls that have no <c>RpcCall</c> to put it in (subscribe /
    /// unsubscribe) through Orleans <see cref="RequestContext"/>. The RPC, query, signal and
    /// cross-entity paths carry it in <c>RpcCall.TraceParent</c> instead: every
    /// <see cref="RequestContext.Set"/> copies a dictionary and the context rides on every grain
    /// message sent from the flow afterwards, broadcasts included — too much for the hot path.
    /// </summary>
    public static class MetaCallContextPropagation
    {
        internal const string TraceParentKey = "sm.tp";

        /// <summary>Trace of the incoming grain call; invalid when the caller sent none.</summary>
        public static MetaTraceContext Read()
        {
            MetaTraceContext.TryParse(RequestContext.Get(TraceParentKey) as string, out var trace);
            return trace;
        }

        /// <summary>
        /// Puts <paramref name="trace"/> on the outgoing request context and returns a token that
        /// restores the previous value on dispose — removed, not nulled, when there was none.
        /// </summary>
        public static Scope Push(MetaTraceContext trace) => new Scope(trace);

        public readonly struct Scope : IDisposable
        {
            private readonly bool _active;
            private readonly object? _previous;

            internal Scope(MetaTraceContext trace)
            {
                _active = true;
                _previous = RequestContext.Get(TraceParentKey);
                var value = trace.ToTraceParent();
                if (value == null) RequestContext.Remove(TraceParentKey);
                else RequestContext.Set(TraceParentKey, value);
            }

            public void Dispose()
            {
                if (!_active) return;
                if (_previous == null) RequestContext.Remove(TraceParentKey);
                else RequestContext.Set(TraceParentKey, _previous);
            }
        }
    }
}
