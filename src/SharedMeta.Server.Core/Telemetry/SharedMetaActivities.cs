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
    /// Span tree of one client call: <c>server.*</c> on the transport handler (parent = the
    /// client's <c>traceparent</c> when it sent one) → <c>entity.*</c> on the entity grain →
    /// <c>cross_entity.call</c> per cross-entity hop. The session grain in between logs under the
    /// handler's span without one of its own. Parents travel in <c>RpcCall.TraceParent</c>
    /// (subscribe: <see cref="MetaCallContextPropagation"/>), so no Orleans activity propagation
    /// is needed. The trace id is also the <c>CallId</c> in every log scope of the call.
    /// </para>
    /// </summary>
    public static class SharedMetaActivities
    {
        /// <summary>OpenTelemetry source name. Host opt-in via <c>AddSource(SourceName)</c>.</summary>
        public const string SourceName = "SharedMeta";

        /// <summary>Source version (the SharedMeta assembly version) — drives the version dimension on exported spans.</summary>
        public static readonly string SourceVersion = SharedMetaMeters.MeterVersion;

        /// <summary>Shared <see cref="ActivitySource"/> instance — single allocation per process.</summary>
        public static readonly ActivitySource Source = new(SourceName, SourceVersion);

        // ── Span names (constants — used as Activity name argument) ─────────────────

        public const string SpanServerRpc            = "sharedmeta.server.rpc";
        public const string SpanServerQuery          = "sharedmeta.server.query";
        public const string SpanServerSignal         = "sharedmeta.server.signal";
        public const string SpanServerSubscribe      = "sharedmeta.server.subscribe";
        public const string SpanServerUnsubscribe    = "sharedmeta.server.unsubscribe";
        public const string SpanServerDesyncReport   = "sharedmeta.server.desync_report";
        public const string SpanSessionCall          = "sharedmeta.session.call";

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
        public const string TagOp                    = "sharedmeta.op";
        public const string TagRequestId             = "sharedmeta.request_id";
        public const string TagErrorKind             = "sharedmeta.error_kind";
    }
}
