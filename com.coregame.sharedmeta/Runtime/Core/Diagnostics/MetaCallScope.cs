using System;
using Microsoft.Extensions.Logging;
using SharedMeta.Core.Logging;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Client-side start of a meta call, opened by the generated API client before the local
    /// (optimistic) execution: a fresh trace whose id is the call id, set as
    /// <see cref="MetaCallContext.Current"/> and as a <see cref="MetaLog"/> logger scope. The
    /// dispatcher reads <see cref="MetaCallContext.Current"/> when it builds the wire request,
    /// so the same id reaches the server as <c>TraceParent</c>; continuations of the call
    /// (send completion, desync report) see it through the captured execution context.
    /// <para>
    /// Not deterministic state: the trace id never feeds meta logic, and it is generated apart
    /// from <c>Context.Random</c>.
    /// </para>
    /// </summary>
    public readonly struct MetaCallScope : IDisposable
    {
        /// <summary>
        /// Optional source of the ambient trace — a .NET client with OpenTelemetry sets it to read
        /// <c>Activity.Current</c>, so client calls join the host's trace. Null: every call
        /// starts its own trace.
        /// </summary>
        public static Func<MetaTraceContext?>? AmbientTrace { get; set; }

        /// <summary>Open an <c>ILogger</c> scope on <see cref="MetaLog.Logger"/> per call. Default true.</summary>
        public static bool LogScopes { get; set; } = true;

        private readonly MetaCallContext.CurrentScope _current;
        private readonly IDisposable? _logScope;

        private MetaCallScope(MetaCallContext context)
        {
            Context = context;
            _current = context.Enter();
            _logScope = LogScopes ? MetaLog.Logger.BeginScope(context) : null;
        }

        /// <summary>Context of this call.</summary>
        public MetaCallContext Context { get; }

        /// <summary>
        /// Starts a call. A call started inside another one (an API call made from a handler of
        /// the first) continues the outer trace with a new span instead of starting a new one.
        /// </summary>
        public static MetaCallScope Begin(string service, string method, string op, string? entityId)
        {
            var outer = MetaCallContext.Current;
            MetaTraceContext trace;
            if (outer != null && outer.Trace.IsValid)
                trace = outer.Trace.WithNewSpan();
            else
            {
                var ambient = AmbientTrace?.Invoke();
                trace = ambient is { IsValid: true } a ? a.WithNewSpan() : MetaTraceContext.CreateRandom();
            }
            return new MetaCallScope(new MetaCallContext(trace, op, entityId: entityId, service: service, method: method));
        }

        /// <summary><c>traceparent</c> of the current call, or null outside one — for wire requests.</summary>
        public static string? CurrentTraceParent() => MetaCallContext.Current?.Trace.ToTraceParent();

        /// <summary>
        /// Hands a finished call to <paramref name="observer"/> (no-op when null). Observer
        /// failures are logged and swallowed — analytics must never fail a call.
        /// </summary>
        public static void Report(IMetaClientCallObserver? observer, MetaCallContext context, string op,
            long startTimestamp, string? errorKind)
        {
            if (observer == null) return;
            try
            {
                observer.OnCallCompleted(new MetaCallInfo
                {
                    CallId = context.CallId,
                    Op = op,
                    Service = context.Service,
                    Method = context.Method,
                    EntityId = context.EntityId,
                    Result = errorKind == null ? MetaCallResults.Success : MetaCallResults.Error,
                    ErrorKind = errorKind,
                    DurationMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp) * 1000.0
                                 / System.Diagnostics.Stopwatch.Frequency,
                });
            }
            catch (Exception ex)
            {
                MetaLog.Warning($"[Telemetry] Call observer failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _logScope?.Dispose();
            _current.Dispose();
        }
    }
}
