using System.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Static <see cref="ActivitySource"/> for distributed tracing of server-side
    /// SharedMeta operations. Hosts subscribe via OpenTelemetry:
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(b =&gt; b
    ///         .AddSource(SharedMetaActivities.SourceName)
    ///         .AddJaegerExporter());
    /// </code>
    /// <para>
    /// Activities form a parent/child tree through in-process <c>Activity.Current</c>
    /// propagation: a top-level <c>rpc</c> span on <see cref="MetaConnectionHandler"/>
    /// nests <c>entity.rpc</c> on the grain, which nests <c>cross_entity.call</c> spans
    /// for each <c>GetIService(id).Method(...)</c> hop. Trace context survives cross-grain
    /// hops automatically because Orleans preserves <see cref="System.Threading.ExecutionContext"/>.
    /// </para>
    /// <para>
    /// Wire-level trace propagation from client to server (W3C <c>traceparent</c>) is NOT yet
    /// implemented — adding it requires extending the RPC envelope shape, planned as a separate
    /// follow-up. Client-side and server-side traces are independent for now.
    /// </para>
    /// </summary>
    public static class SharedMetaActivities
    {
        /// <summary>OpenTelemetry source name. Host opt-in via <c>AddSource(SourceName)</c>.</summary>
        public const string SourceName = "SharedMeta";

        /// <summary>Source version — drives the version dimension on exported spans.</summary>
        public const string SourceVersion = "0.23.0";

        /// <summary>Shared <see cref="ActivitySource"/> instance — single allocation per process.</summary>
        public static readonly ActivitySource Source = new(SourceName, SourceVersion);

        // ── Span names (constants — used as Activity name argument) ─────────────────

        public const string SpanSessionConnect       = "sharedmeta.session.connect";
        public const string SpanEntitySubscribe      = "sharedmeta.entity.subscribe";
        public const string SpanEntityRpc            = "sharedmeta.entity.rpc";
        public const string SpanEntityQuery          = "sharedmeta.entity.query";
        public const string SpanEntitySignal         = "sharedmeta.entity.signal";
        public const string SpanCrossEntityCall      = "sharedmeta.cross_entity.call";
        public const string SpanBroadcastSend        = "sharedmeta.broadcast.send";
        public const string SpanPersistenceWrite     = "sharedmeta.persistence.write";

        // ── Tag keys (constants — keep consistent across call sites) ────────────────

        public const string TagService               = "sharedmeta.service";
        public const string TagMethod                = "sharedmeta.method";
        public const string TagStateType             = "sharedmeta.state_type";
        public const string TagEntityId              = "sharedmeta.entity_id";
        public const string TagPlayerId              = "sharedmeta.player_id";
        public const string TagMode                  = "sharedmeta.mode";
        public const string TagKind                  = "sharedmeta.kind";
        public const string TagResult                = "sharedmeta.result";
        public const string TagReason                = "sharedmeta.reason";
        public const string TagColdStart             = "sharedmeta.cold_start";
        public const string TagFanOutSize            = "sharedmeta.fan_out_size";
        public const string TagPayloadBytes          = "sharedmeta.payload_bytes";
        public const string TagForcePatchServices    = "sharedmeta.force_patch_services_count";
    }
}
