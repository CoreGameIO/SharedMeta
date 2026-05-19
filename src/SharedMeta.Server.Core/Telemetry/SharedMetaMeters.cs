using System.Diagnostics.Metrics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Static <see cref="Meter"/> exposing every server-side instrument SharedMeta records.
    /// Hosts subscribe via OpenTelemetry (or any <see cref="MeterListener"/>):
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(b =&gt; b
    ///         .AddMeter(SharedMetaMeters.MeterName)
    ///         .AddRuntimeInstrumentation()
    ///         .AddPrometheusExporter());
    /// </code>
    /// <para>
    /// When no listener is attached every <c>Counter</c>/<c>Histogram</c> call is a single
    /// volatile-flag check plus a few primitive writes — no heap allocation. <see cref="TagList"/>
    /// is used at instrumentation sites to keep tag construction stack-only.
    /// </para>
    /// <para>
    /// Cardinality budget: <c>service</c>, <c>method</c>, <c>state_type</c>, <c>mode</c>,
    /// <c>kind</c>, <c>result</c>, <c>reason</c> tags are all bounded by the application's
    /// schema (typically 5–600 distinct series total across the process). Per-entity ids are
    /// NOT used as metric tags — they go on <see cref="System.Diagnostics.Activity"/> tags
    /// instead (no aggregation cardinality cost there).
    /// </para>
    /// </summary>
    public static class SharedMetaMeters
    {
        /// <summary>OpenTelemetry meter name. Host opt-in via <c>AddMeter(MeterName)</c>.</summary>
        public const string MeterName = "SharedMeta";

        /// <summary>Meter version — drives the version dimension on exported metrics.</summary>
        public const string MeterVersion = "0.23.0";

        /// <summary>Shared <see cref="Meter"/> instance — single allocation per process.</summary>
        public static readonly Meter Meter = new(MeterName, MeterVersion);

        // ── Session lifecycle ───────────────────────────────────────────────────────

        /// <summary>Session-connect handshake duration. Tags: <c>result</c>
        /// (<c>success|rejected|error</c>), <c>phase</c> (<c>1|2</c> for full-signature follow-up).</summary>
        public static readonly Histogram<double> SessionConnectDuration =
            Meter.CreateHistogram<double>("sharedmeta.session.connect.duration", "ms",
                "Session-connect handshake duration including version-gate + signature lookup");

        /// <summary>Currently active sessions (delta-counted). Drops on graceful + ungraceful disconnect.</summary>
        public static readonly UpDownCounter<long> SessionsActive =
            Meter.CreateUpDownCounter<long>("sharedmeta.session.active", "{session}",
                "Currently active sessions");

        /// <summary>Session terminations. Tag: <c>reason</c>
        /// (<c>graceful|transport_drop|superseded|server_close|version_rejected</c>).</summary>
        public static readonly Counter<long> SessionTerminated =
            Meter.CreateCounter<long>("sharedmeta.session.terminated.count", "{session}",
                "Session terminations grouped by reason");

        // ── Entity subscribe ────────────────────────────────────────────────────────

        /// <summary>Subscribe duration. Tags: <c>state_type</c>, <c>result</c> (<c>success|error</c>),
        /// <c>cold_start</c> (<c>true|false</c> — true means grain activation happened during this call).</summary>
        public static readonly Histogram<double> SubscribeDuration =
            Meter.CreateHistogram<double>("sharedmeta.entity.subscribe.duration", "ms",
                "Entity subscribe duration including grain activation and capability compute");

        /// <summary>Subscribe count. Tag: <c>state_type</c>, <c>force_patch_services_count</c>
        /// (0 = clean, &gt;0 = boundary force-patch triggered for this subscriber).</summary>
        public static readonly Counter<long> SubscribeCount =
            Meter.CreateCounter<long>("sharedmeta.entity.subscribe.count", "{subscribe}",
                "Subscribe count, tagged by force-patch overlay size");

        /// <summary>Currently subscribed entity-player pairs across the silo (observable — read on poll).</summary>
        public static readonly UpDownCounter<long> SubscribersActive =
            Meter.CreateUpDownCounter<long>("sharedmeta.entity.subscribers.active", "{subscriber}",
                "Currently active subscriber bindings");

        // ── RPC dispatch ────────────────────────────────────────────────────────────

        /// <summary>RPC duration on the server side (between grain entry and response).
        /// Tags: <c>service</c>, <c>method</c>, <c>mode</c>
        /// (<c>optimistic|server|local|crossopt|server_patch|server_replace</c>), <c>result</c>.</summary>
        public static readonly Histogram<double> RpcDuration =
            Meter.CreateHistogram<double>("sharedmeta.entity.rpc.duration", "ms",
                "Entity RPC processing time, server side");

        /// <summary>End-to-end RPC time as seen at the server's transport entry point
        /// (top of <c>MetaConnectionHandler.RpcCallAsync</c>): includes SessionManagerGrain
        /// queueing, grain-hop to EntityGrain, the actual method body
        /// (<see cref="RpcDuration"/>), broadcast scheduling, and response serialization.
        /// Diff with <see cref="RpcDuration"/> reveals queue-time / grain-hop overhead.
        /// Tags: <c>service</c>, <c>method</c>, <c>result</c>.</summary>
        public static readonly Histogram<double> ServerRpcTotalDuration =
            Meter.CreateHistogram<double>("sharedmeta.server.rpc.total_duration", "ms",
                "Full server-side RPC time at transport entry (queue + grain hop + method + response build)");

        /// <summary>RPC request payload size in bytes. Tags: <c>service</c>, <c>method</c>.</summary>
        public static readonly Histogram<long> RpcRequestBytes =
            Meter.CreateHistogram<long>("sharedmeta.entity.rpc.request_bytes", "By",
                "RPC request payload size");

        /// <summary>RPC response payload size in bytes. Tags: <c>service</c>, <c>method</c>.</summary>
        public static readonly Histogram<long> RpcResponseBytes =
            Meter.CreateHistogram<long>("sharedmeta.entity.rpc.response_bytes", "By",
                "RPC response payload size (replay payload + state diff combined)");

        // ── Query / Signal ──────────────────────────────────────────────────────────

        /// <summary>Query call duration. Tags: <c>service</c>, <c>method</c>, <c>result</c>.</summary>
        public static readonly Histogram<double> QueryDuration =
            Meter.CreateHistogram<double>("sharedmeta.entity.query.duration", "ms",
                "Query method duration (read-only, no state sync)");

        /// <summary>Signal call count. Tags: <c>service</c>, <c>method</c>.</summary>
        public static readonly Counter<long> SignalCount =
            Meter.CreateCounter<long>("sharedmeta.entity.signal.count", "{signal}",
                "Signal method fire count (client → server fire-and-forget)");

        // ── Cross-entity ────────────────────────────────────────────────────────────

        /// <summary>Cross-entity call duration. Tags: <c>from_service</c>, <c>to_service</c>,
        /// <c>kind</c> (<c>normal|sibling|notification|self_nested</c>), <c>result</c>.</summary>
        public static readonly Histogram<double> CrossEntityCallDuration =
            Meter.CreateHistogram<double>("sharedmeta.cross_entity.call.duration", "ms",
                "Server-side cross-entity call latency (caller-grain perspective)");

        /// <summary>Cross-entity call count by kind. Tags: <c>from_service</c>, <c>to_service</c>, <c>kind</c>.</summary>
        public static readonly Counter<long> CrossEntityCallCount =
            Meter.CreateCounter<long>("sharedmeta.cross_entity.call.count", "{call}",
                "Cross-entity call count, includes Notification fire-and-forget");

        // ── Broadcast fan-out ───────────────────────────────────────────────────────

        /// <summary>Subscribers receiving a broadcast. Tag: <c>state_type</c>.</summary>
        public static readonly Histogram<long> BroadcastFanOutSize =
            Meter.CreateHistogram<long>("sharedmeta.broadcast.fan_out_size", "{subscriber}",
                "Number of subscribers a broadcast was delivered to");

        /// <summary>Broadcast payload size in bytes. Tags: <c>state_type</c>, <c>kind</c>
        /// (<c>replay|patch|state</c>) — single broadcast may emit multiple kinds when 0.22+
        /// per-subscriber tailoring fires.</summary>
        public static readonly Histogram<long> BroadcastPayloadBytes =
            Meter.CreateHistogram<long>("sharedmeta.broadcast.payload_bytes", "By",
                "Broadcast payload size split by payload kind");

        /// <summary>Tailored broadcasts. Tags: <c>state_type</c>, <c>path</c> (<c>patch|replay</c>)
        /// — counts how many subscribers got force-patched vs native on each broadcast.</summary>
        public static readonly Counter<long> BroadcastTailored =
            Meter.CreateCounter<long>("sharedmeta.broadcast.tailored.count", "{subscriber}",
                "Per-subscriber broadcast routing splits (0.22+ boundary force-patch fan-out)");

        // ── Persistence ─────────────────────────────────────────────────────────────

        /// <summary>State persistence write duration. Tags: <c>state_type</c>, <c>storage_provider</c>.</summary>
        public static readonly Histogram<double> PersistenceWriteDuration =
            Meter.CreateHistogram<double>("sharedmeta.persistence.write.duration", "ms",
                "WriteStateAsync duration");

        /// <summary>State persistence payload size. Tag: <c>state_type</c>.</summary>
        public static readonly Histogram<long> PersistenceWriteBytes =
            Meter.CreateHistogram<long>("sharedmeta.persistence.write.payload_bytes", "By",
                "Persisted state size in bytes");

        // ── Compatibility negotiation ───────────────────────────────────────────────

        /// <summary>Client-signature cache hit / miss. Tag: <c>result</c> (<c>hit|miss</c>).</summary>
        public static readonly Counter<long> SignatureCacheLookup =
            Meter.CreateCounter<long>("sharedmeta.compat.signature_cache.lookup", "{lookup}",
                "Client-signature registry lookup, tagged by hit/miss");

        /// <summary>Force-patch dispatch — number of RPCs that got downgraded. Tags:
        /// <c>service</c>, <c>method</c>, <c>kind</c> (<c>method|service</c>),
        /// <c>reason</c> (<c>min_version|boundary</c>).</summary>
        public static readonly Counter<long> ForcePatchApplied =
            Meter.CreateCounter<long>("sharedmeta.compat.force_patch.applied", "{call}",
                "RPC dispatched through ServerPatch downgrade by compat-negotiation");

        /// <summary>Method explicitly rejected by capabilities. Tags: <c>service</c>, <c>method</c>,
        /// <c>reason</c> (<c>missing|server_only|arg_drift</c>).</summary>
        public static readonly Counter<long> MethodRejected =
            Meter.CreateCounter<long>("sharedmeta.compat.method_rejected.count", "{call}",
                "RPC rejected by capability gate");

        // ── RPC ordering (server-side stash) ────────────────────────────────────────

        /// <summary>Out-of-order RPCs received per session: how many requests had to wait for
        /// a lower RequestId to arrive before draining. Counter, no tags.</summary>
        public static readonly Counter<long> RpcOutOfOrderStashed =
            Meter.CreateCounter<long>("sharedmeta.rpc.out_of_order.stashed", "{request}",
                "RPCs received before their predecessor, parked in stash");

        /// <summary>Time an out-of-order RPC waited in stash before being drained.</summary>
        public static readonly Histogram<double> RpcOutOfOrderDrainWait =
            Meter.CreateHistogram<double>("sharedmeta.rpc.out_of_order.drain_wait", "ms",
                "Wait time for stashed RPCs before dispatch");

        // ── Desync diagnostics ──────────────────────────────────────────────────────

        /// <summary>Server-observed desync (when client reports via DesyncReport API). Tags:
        /// <c>service</c>, <c>method</c>, <c>kind</c>
        /// (<c>result|patch_crc|optimistic_random|named_random</c>).</summary>
        public static readonly Counter<long> DesyncDetected =
            Meter.CreateCounter<long>("sharedmeta.desync.detected", "{desync}",
                "Desync occurrences grouped by category");

        // ── Grain lifecycle ─────────────────────────────────────────────────────────

        /// <summary>Grain activation count. Tag: <c>state_type</c>.</summary>
        public static readonly Counter<long> GrainActivation =
            Meter.CreateCounter<long>("sharedmeta.grain.activation.count", "{grain}",
                "EntityGrain activations (cold-starts) per state type");

        /// <summary>Grain deactivation count. Tag: <c>state_type</c>, <c>reason</c>
        /// (<c>idle_ttl|shutdown</c>).</summary>
        public static readonly Counter<long> GrainDeactivation =
            Meter.CreateCounter<long>("sharedmeta.grain.deactivation.count", "{grain}",
                "EntityGrain deactivations");

        /// <summary>Currently active grain count (UpDown — increments on activate, decrements on deactivate).</summary>
        public static readonly UpDownCounter<long> GrainsActive =
            Meter.CreateUpDownCounter<long>("sharedmeta.grain.active", "{grain}",
                "Currently active EntityGrains across this silo");
    }
}
