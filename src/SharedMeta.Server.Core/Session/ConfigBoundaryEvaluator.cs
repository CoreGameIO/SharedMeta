using System.Collections.Generic;
using SharedMeta.Core;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// 0.22.0+ Pure compute helpers for <c>[MetaConfigStructureBoundary]</c> evaluation.
    /// Stateless and side-effect-free so the logic is unit-testable independently of any
    /// Orleans grain, DI container, or live entity state.
    /// </summary>
    public static class ConfigBoundaryEvaluator
    {
        /// <summary>
        /// Decide which services on an entity need force-ServerPatch for a specific subscriber.
        /// Open-closed config evolution model: server config classes only ADD fields and mark
        /// old ones deprecated; the class never removes or restructures existing fields.
        /// Consequence — the boundary check is asymmetric:
        /// <list type="bullet">
        ///   <item><b>New client + old config bytes (clientCode &gt;= pinned):</b> client's class
        ///     contains every field the old config had (plus the new deprecated-aware ones).
        ///     Old method bodies still work against the old field subset. <b>No force-patch.</b></item>
        ///   <item><b>Old client + new config bytes (clientCode &lt; boundary &amp;&amp; pinned &gt;= boundary):</b>
        ///     client's class is missing fields the new structure adds; old method bodies
        ///     can't process the new shape. <b>Force-patch this service.</b></item>
        ///   <item><b>Same side of every boundary:</b> no force-patch.</item>
        /// </list>
        /// Formally: a boundary V triggers iff <c>clientCode.Version &lt; V &amp;&amp; pinned.Version &gt;= V</c>.
        /// Returns an empty list when no boundary triggers.
        /// </summary>
        /// <param name="boundaries">All declared structural boundaries (from
        ///   <see cref="MetaServerSignature.ConfigBoundaries"/>).</param>
        /// <param name="methods">All known server methods (from <see cref="MetaServerSignature.Methods"/>).
        ///   Used to map triggered config types back to affected service names.</param>
        /// <param name="pinned">The entity's pinned/effective config version (what the server
        ///   executes under).</param>
        /// <param name="clientCode">The client's natural config version (resolved purely from
        ///   their ClientVersion via <c>[MetaConfigVersion]</c> rules, unclamped by the
        ///   entity's pin).</param>
        public static List<string> ComputeAffectedServices(
            IReadOnlyList<ConfigBoundaryEntry> boundaries,
            IReadOnlyList<ServerMethodEntry> methods,
            MetaConfigVersion pinned,
            MetaConfigVersion clientCode)
        {
            var result = new List<string>();
            if (boundaries == null || boundaries.Count == 0) return result;

            HashSet<string>? triggeredConfigs = null;
            foreach (var b in boundaries)
            {
                var parts = b.MinConfigVersion.Split('.');
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[0], out var bMajor) || !int.TryParse(parts[1], out var bMinor)) continue;

                // Asymmetric trigger:
                //   - Client below the boundary (its code doesn't know the new fields).
                //   - Entity at or above the boundary (bytes contain post-boundary content).
                bool clientBelowBoundary =
                    clientCode.Major < bMajor
                    || (clientCode.Major == bMajor && clientCode.Minor < bMinor);
                bool pinnedAtOrAboveBoundary =
                    pinned.Major > bMajor
                    || (pinned.Major == bMajor && pinned.Minor >= bMinor);
                if (!(clientBelowBoundary && pinnedAtOrAboveBoundary)) continue;

                triggeredConfigs ??= new HashSet<string>(System.StringComparer.Ordinal);
                triggeredConfigs.Add(b.ConfigTypeFullName);
            }
            if (triggeredConfigs == null) return result;

            // Map triggered config types back to services on this surface.
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var m in methods)
            {
                if (!triggeredConfigs.Contains(m.ConfigTypeFullName)) continue;
                if (seen.Add(m.ServiceName)) result.Add(m.ServiceName);
            }
            return result;
        }

    }
}
