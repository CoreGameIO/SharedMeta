using System;
using System.Collections.Generic;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// One-shot metric recording helpers, grouped by domain. Each method bundles the tag
    /// construction + counter add into a single call so business code does not repeat
    /// <c>new KeyValuePair&lt;string, object?&gt;(...)</c> boilerplate or know which raw
    /// <c>Counter</c>/<c>Histogram</c> backs the event.
    /// <para>
    /// Use disposable measurement structs in <c>Measurements.cs</c> when a duration scope
    /// is involved. Use this facade for fire-and-forget instantaneous events (one-shot
    /// counters, gauge ±1 increments, fan-out histograms recorded at a single moment).
    /// </para>
    /// </summary>
    public static class MetricEvents
    {
        public static class Grain
        {
            public static void Activated(string stateType)
            {
                var tag = new KeyValuePair<string, object?>("state_type", stateType);
                SharedMetaMeters.GrainActivation.Add(1, tag);
                SharedMetaMeters.GrainsActive.Add(1, tag);
            }

            public static void Deactivated(string stateType, string reason)
            {
                SharedMetaMeters.GrainDeactivation.Add(1,
                    new KeyValuePair<string, object?>("state_type", stateType),
                    new KeyValuePair<string, object?>("reason", reason));
                SharedMetaMeters.GrainsActive.Add(-1,
                    new KeyValuePair<string, object?>("state_type", stateType));
            }
        }

        public static class Subscriber
        {
            public static void Removed(string stateType)
                => SharedMetaMeters.SubscribersActive.Add(-1,
                    new KeyValuePair<string, object?>("state_type", stateType));
        }

        public static class Session
        {
            /// <summary>Bumps the active-session gauge — call on a successful SessionConnect.</summary>
            public static void Started()
                => SharedMetaMeters.SessionsActive.Add(1);

            /// <summary>
            /// Decrements active-session gauge AND increments the terminated counter tagged
            /// by <paramref name="reason"/> — a bounded enum, never free text.
            /// </summary>
            public static void Terminated(MetaSessionEndReason reason)
            {
                SharedMetaMeters.SessionsActive.Add(-1);
                SharedMetaMeters.SessionTerminated.Add(1,
                    new KeyValuePair<string, object?>("reason", ReasonTag(reason)));
            }

            public static string ReasonTag(MetaSessionEndReason reason) => reason switch
            {
                MetaSessionEndReason.Graceful => "graceful",
                MetaSessionEndReason.TransportDrop => "transport_drop",
                MetaSessionEndReason.Superseded => "superseded",
                MetaSessionEndReason.StashOverflow => "stash_overflow",
                MetaSessionEndReason.Stall => "stall",
                MetaSessionEndReason.InactivityTimeout => "inactivity_timeout",
                MetaSessionEndReason.ServerClose => "server_close",
                MetaSessionEndReason.VersionRejected => "version_rejected",
                _ => "other",
            };
        }

        public static class Compat
        {
            public static void ForcePatchApplied(MethodTags tags, string kind)
                => SharedMetaMeters.ForcePatchApplied.Add(1, tags.ServiceTag, tags.MethodTag,
                    new KeyValuePair<string, object?>("kind", kind));

            /// <summary>
            /// RPC refused by the handler's capability gate. <paramref name="reason"/>:
            /// <c>out_of_range | not_callable | rejected | not_negotiated</c>.
            /// </summary>
            public static void MethodRejected(MethodTags tags, string reason)
                => SharedMetaMeters.MethodRejected.Add(1, tags.ServiceTag, tags.MethodTag,
                    new KeyValuePair<string, object?>("reason", reason));
        }

        public static class Desync
        {
            /// <summary>
            /// One client desync report per set mismatch bit. Names come from the server table,
            /// never from the client's strings.
            /// </summary>
            public static void Reported(MethodTags tags, DesyncMismatchKind kind)
            {
                if (kind == DesyncMismatchKind.None) kind = DesyncMismatchKind.Patch;
                if ((kind & DesyncMismatchKind.Patch) != 0) Add(tags, "patch_crc");
                if ((kind & DesyncMismatchKind.Result) != 0) Add(tags, "result");
                if ((kind & DesyncMismatchKind.Random) != 0) Add(tags, "optimistic_random");
            }

            private static void Add(MethodTags tags, string kind)
                => SharedMetaMeters.DesyncDetected.Add(1, tags.ServiceTag, tags.MethodTag,
                    new KeyValuePair<string, object?>("kind", kind));
        }

        public static class RpcOrder
        {
            public static void Stashed() => SharedMetaMeters.RpcOutOfOrderStashed.Add(1);

            public static void Drained(double waitedMs) => SharedMetaMeters.RpcOutOfOrderDrainWait.Record(waitedMs);
        }

        public static class CrossEntity
        {
            /// <summary>
            /// Notification (OneWay) fire-and-forget dispatch — no duration histogram since
            /// the caller doesn't await; only the count is recorded with <c>kind=notification</c>.
            /// </summary>
            public static void OneWayDispatched(string toService)
                => SharedMetaMeters.CrossEntityCallCount.Add(1,
                    new KeyValuePair<string, object?>("to_service", toService),
                    new KeyValuePair<string, object?>("kind", "notification"));
        }

        public static class Broadcast
        {
            /// <summary>
            /// Records every per-fan-out metric SharedMeta emits: fan-out size histogram +
            /// per-variant payload bytes histograms + per-variant tailored-subscriber count.
            /// Pass <c>0</c> for <paramref name="replaySent"/> / <paramref name="patchSent"/>
            /// when that variant didn't reach any subscriber (no metric emitted for that path).
            /// </summary>
            public static void Recorded(string stateType, int sentCount,
                int replayLength, int replaySent, int patchLength, int patchSent)
            {
                var stTag = new KeyValuePair<string, object?>("state_type", stateType);
                SharedMetaMeters.BroadcastFanOutSize.Record(sentCount, stTag);

                if (replaySent > 0)
                {
                    SharedMetaMeters.BroadcastPayloadBytes.Record(replayLength, stTag,
                        new KeyValuePair<string, object?>("kind", "replay"));
                    SharedMetaMeters.BroadcastTailored.Add(replaySent, stTag,
                        new KeyValuePair<string, object?>("path", "replay"));
                }
                if (patchSent > 0)
                {
                    SharedMetaMeters.BroadcastPayloadBytes.Record(patchLength, stTag,
                        new KeyValuePair<string, object?>("kind", "patch"));
                    SharedMetaMeters.BroadcastTailored.Add(patchSent, stTag,
                        new KeyValuePair<string, object?>("path", "patch"));
                }
            }
        }
    }
}
