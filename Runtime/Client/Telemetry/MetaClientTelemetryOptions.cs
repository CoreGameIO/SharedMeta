using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Client.Telemetry
{
    /// <summary>Client-side observability settings (<c>MetaClientOptions.Telemetry</c>).</summary>
    public sealed class MetaClientTelemetryOptions
    {
        /// <summary>
        /// Told about every finished RPC / Signal — wire time, result, call id (queries: pass it to the generated QueryApi constructor). Works on
        /// Unity without the DiagnosticSource package; the place to feed client analytics.
        /// </summary>
        public IMetaClientCallObserver? CallObserver { get; set; }

        /// <summary>
        /// Tag client metrics (<c>SharedMeta.Client</c> meter, when compiled with
        /// <c>SHAREDMETA_CLIENT_TELEMETRY</c>) with the method name. Off: every method of a
        /// service is <c>method="_other"</c>. Default true — client meters are per process, so
        /// cardinality is bounded by the schema.
        /// </summary>
        public bool MethodMetrics { get; set; } = true;
    }
}
