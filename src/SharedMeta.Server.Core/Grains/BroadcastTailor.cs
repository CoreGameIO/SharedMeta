using System.Collections.Generic;
using SharedMeta.Core.Packets;

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
        /// patch/replay axis. Nested trigger ops (<see cref="MetaOperation.Triggers"/>) are
        /// tailored recursively (a single dispatch can chain triggers that touch different
        /// services, and force-patch may flip arm between main and trigger).
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
            var tailoredOp = TailorOp(original.Op, subscriberMethodContributions, subscriberServiceContributions);
            if (ReferenceEquals(tailoredOp, original.Op)) return original;

            return new EntityBroadcast
            {
                ExcludePlayerId = original.ExcludePlayerId,
                Op = tailoredOp,
            };
        }

        /// <summary>
        /// Recursive helper that strips replay/patch on a single <see cref="MetaOperation"/>
        /// according to force-patch rules and recurses into nested
        /// <see cref="MetaOperation.Triggers"/>. Returns the original instance unchanged when
        /// no transformation was needed (zero-alloc fast path).
        /// </summary>
        private static MetaOperation TailorOp(
            MetaOperation op,
            IReadOnlyList<(string Service, string Alias, int Version)>? methodContributions,
            IReadOnlyList<string>? serviceContributions)
        {
            bool forcePatch = IsForcePatch(op.ServiceName, op.MethodName, op.MethodVersion,
                methodContributions, serviceContributions);

            List<MetaOperation>? tailoredTriggers = null;
            if (op.Triggers is { Count: > 0 } triggers)
            {
                bool anyChanged = false;
                tailoredTriggers = new List<MetaOperation>(triggers.Count);
                for (int i = 0; i < triggers.Count; i++)
                {
                    var tailored = TailorOp(triggers[i], methodContributions, serviceContributions);
                    if (!ReferenceEquals(tailored, triggers[i])) anyChanged = true;
                    tailoredTriggers.Add(tailored);
                }
                if (!anyChanged) tailoredTriggers = null;
            }

            if (!forcePatch && tailoredTriggers == null) return op;

            return new MetaOperation
            {
                ServiceName = op.ServiceName,
                MethodName = op.MethodName,
                MethodVersion = op.MethodVersion,
                Payload = op.Payload,
                CallerId = op.CallerId,
                ResultBytes = op.ResultBytes,
                ReplayPayload = forcePatch ? null : op.ReplayPayload,
                PatchBytes = forcePatch ? op.PatchBytes : null,
                StateBytes = op.StateBytes,
                RandomScrollDelta = op.RandomScrollDelta,
                NamedRandomScrollDeltas = op.NamedRandomScrollDeltas,
                ServerTimeTicks = op.ServerTimeTicks,
                ExecutedConfigVersion = op.ExecutedConfigVersion,
                Error = op.Error,
                DeepDesyncCrc = op.DeepDesyncCrc,
                Triggers = tailoredTriggers ?? op.Triggers,
                Debug = op.Debug,
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
