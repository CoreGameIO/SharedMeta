using System.Collections.Generic;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// 0.22.0+ Pure compute helpers for per-subscriber broadcast tailoring. Stateless,
    /// side-effect-free — runs in <c>EntityGrain.DistributeBroadcasts</c> for each
    /// subscriber to decide whether the broadcast going out should carry the replay
    /// payload (modern) or the patch payload (legacy / boundary-affected).
    /// <para>
    /// Extracted so the logic is independently unit-testable.
    /// </para>
    /// </summary>
    public static class BroadcastTailor
    {
        /// <summary>
        /// Build a per-subscriber tailored <see cref="EntityBroadcast"/>. Strips
        /// <c>ReplayPayload</c> when the subscriber needs the patch (force-patch on this
        /// method's identity OR on this service); strips <c>PatchBytes</c> otherwise.
        /// <c>StateBytes</c> always preserved — ServerReplace is orthogonal to the
        /// patch/replay axis. Trigger broadcasts are tailored recursively (a single dispatch
        /// can chain triggers that touch different services, and force-patch may flip arm
        /// between main and trigger).
        /// </summary>
        /// <param name="original">The broadcast as produced by the provider (contains both
        ///   replay and patch when fan-out needs both variants).</param>
        /// <param name="subscriberMethodContributions">The subset of session-level
        ///   <c>ForceServerPatchMethods</c> that THIS subscriber contributed at subscribe time.
        ///   <c>null</c> when the subscriber didn't contribute any method-level entries.</param>
        /// <param name="subscriberServiceContributions">The subset of per-entity
        ///   <c>ForceServerPatchServices</c> contributed by THIS subscriber. <c>null</c> when
        ///   no service-level overlay applied.</param>
        /// <returns>Either the original broadcast unchanged (zero alloc, when no force-patch
        ///   fires and no trigger needed tailoring) or a freshly-built clone with stripped
        ///   payloads.</returns>
        public static EntityBroadcast TailorForSubscriber(
            EntityBroadcast original,
            IReadOnlyList<(string Service, string Alias, int Version)>? subscriberMethodContributions,
            IReadOnlyList<string>? subscriberServiceContributions)
        {
            bool forcePatch = IsForcePatch(
                original.ServiceName, original.MethodName, original.MethodVersion,
                subscriberMethodContributions, subscriberServiceContributions);

            List<EntityBroadcast>? tailoredTriggers = null;
            if (original.TriggerBroadcasts is { Count: > 0 })
            {
                tailoredTriggers = new List<EntityBroadcast>(original.TriggerBroadcasts.Count);
                bool anyTriggerChanged = false;
                foreach (var t in original.TriggerBroadcasts)
                {
                    var tailored = TailorForSubscriber(t, subscriberMethodContributions, subscriberServiceContributions);
                    if (!ReferenceEquals(tailored, t)) anyTriggerChanged = true;
                    tailoredTriggers.Add(tailored);
                }
                if (!anyTriggerChanged) tailoredTriggers = null;  // keep the original list ref
            }

            if (!forcePatch && tailoredTriggers == null) return original;

            return new EntityBroadcast
            {
                ServiceName = original.ServiceName,
                MethodName = original.MethodName,
                MethodVersion = original.MethodVersion,
                Payload = original.Payload,
                ExcludePlayerId = original.ExcludePlayerId,
                ReplayPayload = forcePatch ? null : original.ReplayPayload,
                TriggerBroadcasts = tailoredTriggers ?? original.TriggerBroadcasts,
                ServerTimeTicks = original.ServerTimeTicks,
                RandomScrollDelta = original.RandomScrollDelta,
                PatchBytes = forcePatch ? original.PatchBytes : null,
                StateBytes = original.StateBytes,
                NamedRandomScrollDeltas = original.NamedRandomScrollDeltas,
                ExecutedConfigVersion = original.ExecutedConfigVersion,
            };
        }

        /// <summary>
        /// True when either subscriber-contributed list flags this <c>(service, method, version)</c>
        /// for force-patch.
        /// </summary>
        public static bool IsForcePatch(
            string serviceName, string methodName, int methodVersion,
            IReadOnlyList<(string Service, string Alias, int Version)>? methodContributions,
            IReadOnlyList<string>? serviceContributions)
        {
            if (methodContributions != null)
            {
                var key = (serviceName, methodName, methodVersion);
                for (int i = 0; i < methodContributions.Count; i++)
                {
                    if (methodContributions[i] == key) return true;
                }
            }
            if (serviceContributions != null)
            {
                for (int i = 0; i < serviceContributions.Count; i++)
                {
                    if (serviceContributions[i] == serviceName) return true;
                }
            }
            return false;
        }
    }
}
