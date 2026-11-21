using System.Collections.Generic;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>Which shared methods get their own metric series.</summary>
    public enum MethodMetricsMode
    {
        /// <summary>No method has its own series; everything is <c>method="_other"</c> of its service.</summary>
        Off = 0,
        /// <summary>Only methods (or services) marked <c>[MetaMethod(Metrics = MetaMetrics.On)]</c>.</summary>
        Annotated = 1,
        /// <summary>Every method except those marked <c>MetaMetrics.Off</c>.</summary>
        All = 2,
    }

    /// <summary>
    /// Observability settings of the SharedMeta server: per-method metric series, transport
    /// metrics, log scopes, client trace context, analytics log. Register with
    /// <c>services.AddSharedMetaTelemetry(o =&gt; …)</c> in every container that hosts SharedMeta
    /// parts (web host and silo); read through <c>IOptionsMonitor</c>, so everything except
    /// <see cref="TransportMetrics"/> follows configuration reloads.
    /// </summary>
    public sealed class MetaTelemetryOptions
    {
        /// <summary>Default <see cref="MethodMetricsMode.Annotated"/>.</summary>
        public MethodMetricsMode MethodMetrics { get; set; } = MethodMetricsMode.Annotated;

        /// <summary>
        /// Methods that always get their own series, whatever the attribute says. Patterns:
        /// <c>"IShopService.Buy"</c>, <c>"IShopService.*"</c>, <c>"*"</c>. Method part is the
        /// alias; matching is ordinal.
        /// </summary>
        public List<string> IncludeMethods { get; set; } = new();

        /// <summary>Methods always folded into <c>_other</c>. Wins over <see cref="IncludeMethods"/>.</summary>
        public List<string> ExcludeMethods { get; set; } = new();

        /// <summary>
        /// Per-request metrics on the SignalR hub and HTTP endpoints (polling, auth, config
        /// download). Read once when the filters are registered — no reload: off means the
        /// filters are not in the pipeline at all.
        /// </summary>
        public bool TransportMetrics { get; set; }

        /// <summary>
        /// Open an <c>ILogger</c> scope carrying call id, player, entity and method around every
        /// call, so every log line of the call — including <c>Context.Log</c> in service code —
        /// carries them. Default true.
        /// </summary>
        public bool LogScopes { get; set; } = true;

        /// <summary>
        /// Continue the client's W3C trace (<c>TraceParent</c> on call requests). When false the
        /// server starts its own trace for every call and ignores what the client sent.
        /// Default true.
        /// </summary>
        public bool AcceptClientTraceContext { get; set; } = true;

        /// <summary>
        /// Write <c>session_started</c> / <c>session_ended</c> / <c>call_completed</c> events to the
        /// <c>SharedMeta.Analytics</c> log category at Information. One record per client call —
        /// at 50K RPS that is 50K records/s; route the category to a sink sized for it.
        /// Default false.
        /// </summary>
        public bool AnalyticsLog { get; set; }
    }
}
