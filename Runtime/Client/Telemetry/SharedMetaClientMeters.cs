// 0.26.1+: Telemetry types live in the System.Diagnostics.DiagnosticSource NuGet, which
// isn't part of Unity's bundled netstandard reference assemblies. Hosts that need client
// telemetry (typically .NET-side wrappers feeding OpenTelemetry) opt in by defining
// SHAREDMETA_CLIENT_TELEMETRY *and* providing the NuGet (e.g. via NuGetForUnity). Unity
// clients without OpenTelemetry pipelines pay zero compile-cost; all use-sites in
// MetaClient are guarded by the same symbol.
#if SHAREDMETA_CLIENT_TELEMETRY
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SharedMeta.Client.Telemetry
{
    /// <summary>
    /// 0.23.0+: Client-side <see cref="Meter"/> exposing latency and error counters as seen from
    /// the client (wire RTT, replay cost, desync detection). Symmetric to the server-side
    /// <c>SharedMeta.Server.Core.Telemetry.SharedMetaMeters</c>:
    /// <list type="bullet">
    /// <item>Server-side meter name = <c>"SharedMeta"</c>.</item>
    /// <item>Client-side meter name = <c>"SharedMeta.Client"</c>.</item>
    /// </list>
    /// Subscribe in the host (a .NET client wrapping <c>MetaClient</c>) via OpenTelemetry:
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(b =&gt; b
    ///         .AddMeter(SharedMetaClientMeters.MeterName)
    ///         .AddRuntimeInstrumentation()
    ///         .AddOtlpExporter());
    /// </code>
    /// On Unity hosts the same <see cref="Meter"/> is reachable via <c>MeterListener</c> directly
    /// for custom telemetry sinks — OpenTelemetry SDK is not strictly required, only the
    /// built-in <c>System.Diagnostics.Metrics</c> APIs.
    /// <para>
    /// When no listener is attached every <c>Counter</c> / <c>Histogram</c> call is a single
    /// volatile-flag check — no heap allocation.
    /// </para>
    /// </summary>
    public static class SharedMetaClientMeters
    {
        public const string MeterName = "SharedMeta.Client";
        public const string MeterVersion = "0.23.0";

        public static readonly Meter Meter = new(MeterName, MeterVersion);

        /// <summary>Client-side RPC duration (full wire round-trip: serialize → send → server →
        /// receive → deserialize). Tags: <c>service</c>, <c>method</c>, <c>mode</c>, <c>result</c>.</summary>
        public static readonly Histogram<double> RpcDuration =
            Meter.CreateHistogram<double>("sharedmeta.client.rpc.duration", "ms",
                "RPC round-trip as observed on the client");

        /// <summary>Local replay duration — how long the client takes to apply a server broadcast
        /// (state mutation + change-tracker fire). Tag: <c>service</c>, <c>method</c>.</summary>
        public static readonly Histogram<double> ReplayDuration =
            Meter.CreateHistogram<double>("sharedmeta.client.replay.duration", "ms",
                "Time to apply a server broadcast on the client side");

        /// <summary>Connection state transitions. Tags: <c>from</c>, <c>to</c> — values from
        /// <c>disconnected | connecting | connected | reconnecting | failed</c>.</summary>
        public static readonly Counter<long> ConnectionStateTransition =
            Meter.CreateCounter<long>("sharedmeta.client.connection.state.transition", "{transition}",
                "Connection state transitions observed by MetaClient");

        /// <summary>Desyncs detected by client-side validation. Tags: <c>service</c>, <c>method</c>,
        /// <c>kind</c> (<c>result|patch_crc|optimistic_random|named_random</c>).</summary>
        public static readonly Counter<long> DesyncDetected =
            Meter.CreateCounter<long>("sharedmeta.client.desync.detected", "{desync}",
                "Client-side desync detection events");

        /// <summary>Config-provider cache lookups. Tags: <c>config_type</c>,
        /// <c>result</c> (<c>hit|miss</c>).</summary>
        public static readonly Counter<long> ConfigCacheLookup =
            Meter.CreateCounter<long>("sharedmeta.client.config_cache.lookup", "{lookup}",
                "Per-version config cache hits / misses on the client");
    }

    /// <summary>
    /// 0.23.0+: Client-side <see cref="ActivitySource"/>. Client spans are independent from
    /// server spans for now (no wire-level W3C trace propagation yet — planned follow-up). Use
    /// alongside the server-side trace source <c>"SharedMeta"</c> in your OpenTelemetry pipeline:
    /// <code>
    /// .AddSource(SharedMetaClientActivities.SourceName)
    /// </code>
    /// </summary>
    public static class SharedMetaClientActivities
    {
        public const string SourceName = "SharedMeta.Client";
        public const string SourceVersion = "0.23.0";

        public static readonly ActivitySource Source = new(SourceName, SourceVersion);

        public const string SpanClientRpc       = "sharedmeta.client.rpc";
        public const string SpanClientReplay    = "sharedmeta.client.replay";
        public const string SpanClientConnect   = "sharedmeta.client.connect";
    }
}
#endif
