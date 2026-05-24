using System.Collections.Generic;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// 0.22.0+ static helpers consulted at the top of every generated <c>*ApiClient</c>
    /// method. Centralizes the rejected / force-ServerPatch lookup so each generated
    /// method body is a one-liner rather than an inlined loop.
    /// <para>
    /// All methods are <see langword="static"/> and allocation-free on the no-restrictions
    /// fast path (the typical case for an up-to-date client): a null <paramref name="caps"/>
    /// or empty lists return immediately with no allocations.
    /// </para>
    /// </summary>
    public static class CapabilitiesGate
    {
        /// <summary>
        /// 0.22.0+ Per-entity overlay variant: returns true when the service is in the
        /// per-entity <see cref="EntityAugmentedCapabilities.ForceServerPatchServices"/> list.
        /// Used at the gate alongside the session-level annotation-based check.
        /// </summary>
        public static bool IsServiceForcedServerPatchByEntity(EntityAugmentedCapabilities? entityCaps, string service)
        {
            if (entityCaps == null) return false;
            var services = entityCaps.ForceServerPatchServices;
            if (services == null || services.Count == 0) return false;
            for (int i = 0; i < services.Count; i++)
            {
                if (services[i] == service) return true;
            }
            return false;
        }

        /// <summary>
        /// 0.22.0+ Per-entity rejection check. True when the service is in the per-entity
        /// <see cref="EntityAugmentedCapabilities.RejectedServices"/> list.
        /// </summary>
        public static bool IsServiceRejectedByEntity(EntityAugmentedCapabilities? entityCaps, string service)
        {
            if (entityCaps == null) return false;
            var services = entityCaps.RejectedServices;
            if (services == null || services.Count == 0) return false;
            for (int i = 0; i < services.Count; i++)
            {
                if (services[i] == service) return true;
            }
            return false;
        }

        /// <summary>
        /// 0.24.0+ O(1) status lookup against the annotated form. Returns true when
        /// <see cref="ClientSignatureAnnotated.Statuses"/> at index <paramref name="methodId"/>
        /// is <see cref="MethodStatus.Rejected"/>. Replaces the alias/version triple-compare
        /// loop. Null <paramref name="annotated"/> or out-of-range <paramref name="methodId"/>
        /// (legacy/no-negotiation path) returns false — pass-through.
        /// </summary>
        public static bool IsRejected(ClientSignatureAnnotated? annotated, ushort methodId)
        {
            if (annotated == null) return false;
            var statuses = annotated.Statuses;
            return methodId < statuses.Length && statuses[methodId] == MethodStatus.Rejected;
        }

        /// <summary>
        /// 0.24.0+ O(1) status lookup against the annotated form. Returns true when
        /// <see cref="ClientSignatureAnnotated.Statuses"/> at index <paramref name="methodId"/>
        /// is <see cref="MethodStatus.ForceServerPatch"/>. Service-level force-patch is folded
        /// into per-method statuses at server compute time — no separate service list to consult.
        /// </summary>
        public static bool IsForcedServerPatch(ClientSignatureAnnotated? annotated, ushort methodId)
        {
            if (annotated == null) return false;
            var statuses = annotated.Statuses;
            return methodId < statuses.Length && statuses[methodId] == MethodStatus.ForceServerPatch;
        }

        /// <summary>
        /// 0.24.0+ Translate a server-side method id (received in inbound broadcast / op) to
        /// the client-side id used by the local dispatcher. Returns <c>null</c> when the
        /// client doesn't know this method (annotation sentinel
        /// <see cref="ClientSignatureAnnotated.UnknownClientMethodId"/>) — caller should drop
        /// the operation rather than dispatch a wrong handler.
        /// </summary>
        public static ushort? TranslateServerToClient(ClientSignatureAnnotated? annotated, ushort serverMethodId)
        {
            if (annotated == null) return serverMethodId;          // negotiation off — ids assumed identical
            var map = annotated.ServerToClient;
            if (serverMethodId >= map.Length) return null;
            var clientId = map[serverMethodId];
            return clientId == ClientSignatureAnnotated.UnknownClientMethodId ? (ushort?)null : clientId;
        }

        /// <summary>
        /// Helper used by generated code to build an <see cref="IncompatibleFeatureException"/>
        /// with a populated <see cref="FeatureRequirement"/> when a rejected call is detected
        /// at the gate. Keeps the generated emit a single line.
        /// </summary>
        public static IncompatibleFeatureException RejectedException(string service, string alias, int version)
        {
            return new IncompatibleFeatureException(new FeatureRequirement
            {
                FeatureKind = "Method",
                Identifier = $"{service}.{alias}",
                MinRequiredVersion = "",
                Reason = $"server has rejected this method (version {version}) for the current client signature; the client build is out of date for this call.",
            });
        }

        // 0.24.0+ TailorBroadcastPayload removed — broadcast variant selection now lives in
        // EntityGrain.SubscriberNeedsPatch / DistributeBroadcasts (server-side fan-out
        // pre-serializes replay/patch variants per subscriber), so the centralized helper has
        // no production callers. Tests for it lived in ClientSignatureCapabilitiesTests and
        // were dropped with the file in the 0.24.0 sweep.
    }
}
