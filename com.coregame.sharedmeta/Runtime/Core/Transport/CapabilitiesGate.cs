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
        /// Returns true when the given <c>(service, alias, version)</c> appears in
        /// <see cref="ClientCapabilities.RejectedMethods"/>. Inlined in generated
        /// <c>*ApiClient</c> methods immediately before serialization to short-circuit
        /// rejected calls (no wire round trip, no local execution).
        /// </summary>
        public static bool IsRejected(ClientCapabilities? caps, string service, string alias, int version)
        {
            if (caps == null) return false;
            var list = caps.RejectedMethods;
            if (list == null || list.Count == 0) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m.Version == version && m.Alias == alias && m.ServiceName == service)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true when the given <c>(service, alias, version)</c> must be force-downgraded
        /// to ServerPatch execution — either the method is in
        /// <see cref="ClientCapabilities.ForceServerPatchMethods"/>, or the entire service is in
        /// <see cref="ClientCapabilities.ForceServerPatchServices"/>.
        /// </summary>
        public static bool IsForcedServerPatch(ClientCapabilities? caps, string service, string alias, int version)
        {
            if (caps == null) return false;

            var services = caps.ForceServerPatchServices;
            if (services != null && services.Count > 0)
            {
                for (int i = 0; i < services.Count; i++)
                {
                    if (services[i] == service) return true;
                }
            }

            var methods = caps.ForceServerPatchMethods;
            if (methods == null || methods.Count == 0) return false;
            for (int i = 0; i < methods.Count; i++)
            {
                var m = methods[i];
                if (m.Version == version && m.Alias == alias && m.ServiceName == service)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 0.22.0+ Per-entity overlay variant: returns true when the service is in the
        /// per-entity <see cref="EntityAugmentedCapabilities.ForceServerPatchServices"/> list.
        /// Used at the gate alongside session-level <see cref="IsForcedServerPatch"/>.
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

        /// <summary>
        /// 0.22.0+ Per-subscriber broadcast tailoring. Decides whether the player receiving
        /// this broadcast should see replay payload or patch bytes, based on whether the
        /// dispatched method is force-patched at either the session level (caps) OR the
        /// per-entity level (entityCaps).
        /// <list type="bullet">
        ///   <item>Legacy subscriber (force-patch hit at either level): replayOut = <c>null</c>, patchOut = original patch.</item>
        ///   <item>Modern subscriber (no force-patch anywhere): replayOut = original replay, patchOut = <c>null</c>.</item>
        ///   <item><c>caps == null</c> AND <c>entityCaps == null</c> (negotiation disabled): both pass through unchanged.</item>
        /// </list>
        /// <c>StateBytes</c> is preserved unconditionally by the caller — ServerReplace is
        /// independent of the patch/replay axis and applies regardless of compatibility.
        /// </summary>
        public static (byte[]? replayOut, byte[]? patchOut) TailorBroadcastPayload(
            ClientCapabilities? caps, EntityAugmentedCapabilities? entityCaps,
            string service, string alias, int version,
            byte[]? replayPayload, byte[]? patchBytes)
        {
            if (caps == null && entityCaps == null)
            {
                // Negotiation fully disabled (no session caps, no per-entity overlay) — pass through.
                return (replayPayload, patchBytes);
            }

            var forcePatch = IsForcedServerPatch(caps, service, alias, version)
                          || IsServiceForcedServerPatchByEntity(entityCaps, service);
            return forcePatch
                ? (null, patchBytes)       // Legacy: only the patch
                : (replayPayload, null);   // Modern: only the replay
        }
    }
}
