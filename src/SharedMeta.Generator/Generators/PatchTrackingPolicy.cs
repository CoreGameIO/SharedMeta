using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Single source of truth for the "does this service need a patch-tracking copy" decision,
    /// shared by every generator that must agree on it:
    /// <list type="bullet">
    ///   <item><c>GameServiceDiscoveryGenerator</c> — sets <c>ServerMethodEntry.PatchTrackingAvailable</c>
    ///     so negotiation can degrade a force-patch verdict to Reject when the server cannot patch.</item>
    ///   <item><c>PatchTrackedClassGenerator</c> — decides whether to emit the <c>{Impl}_PatchTracked</c> copy.</item>
    ///   <item><c>ServerMetaConfigurationGenerator</c> — decides whether to emit the dispatch fork and getter.</item>
    /// </list>
    /// All three read the same interface-level facts, so the copy, the dispatch fork, and the
    /// negotiation flag never disagree.
    /// </summary>
    internal static class PatchTrackingPolicy
    {
        private const string MetaServiceAttr = "SharedMeta.Core.MetaServiceAttribute";
        private const string MetaMethodAttr = "SharedMeta.Core.MetaMethodAttribute";
        private const string ConfigBoundaryAttr = "SharedMeta.Core.MetaConfigStructureBoundaryAttribute";

        // ExecutionMode enum values (SharedMeta.Core.ExecutionMode):
        // Local=0, Optimistic=1, Server=2, CrossOptimistic=3, ServerPatch=4, ServerReplace=5,
        // Query=6, Signal=7, Notification=8. A missing Mode arg defaults to Optimistic.
        // Optimistic, Server, and CrossOptimistic all run the method body on the client — Server
        // replays it from the recorded buffer (ServerRandom / cross-entity returns) after the
        // server responds — so all three diverge from a changed server body and are
        // force-downgradeable to ServerPatch. ServerReplace/Local/Query/Signal/Notification do not.
        private const int ModeOptimistic = 1;
        private const int ModeServer = 2;
        private const int ModeCrossOptimistic = 3;
        private const int ModeServerPatch = 4;

        /// <summary>
        /// A service needs the patch-tracking copy when it declares a client-callable method that
        /// can run under server-side patch tracking:
        /// <list type="bullet">
        ///   <item><b>ServerPatch</b> mode — always emits a diff, so the copy is required to produce it.</item>
        ///   <item><b>Optimistic / Server / CrossOptimistic</b> that can be force-downgraded to
        ///     ServerPatch: version-fallback band non-empty (<c>Version &gt; MinCompatibleVersion</c>)
        ///     or bound config carries <c>[MetaConfigStructureBoundary]</c>. All three run the method
        ///     body on the client (Server replays it from the recorded buffer after the server
        ///     responds), so all three diverge from a changed server body.</item>
        /// </list>
        /// Query / Signal / Notification / Local / ServerReplace methods never run a divergent local
        /// body a patch would reconcile, so a config boundary alone does not make such a service
        /// force-patch-able — it must also expose an Optimistic/Server/CrossOptimistic method.
        /// </summary>
        public static bool InterfaceIsForcePatchable(INamedTypeSymbol serviceInterface)
        {
            bool configHasBoundary = false;
            var msAttr = serviceInterface.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MetaServiceAttr);
            if (msAttr != null)
            {
                var cfgArg = msAttr.NamedArguments.FirstOrDefault(a => a.Key == "ConfigType");
                if (cfgArg.Value.Value is INamedTypeSymbol cfg &&
                    cfg.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ConfigBoundaryAttr))
                    configHasBoundary = true;
            }

            foreach (var m in serviceInterface.GetMembers().OfType<IMethodSymbol>())
            {
                if (m.MethodKind != MethodKind.Ordinary) continue;
                var mm = m.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MetaMethodAttr);

                int version = 0, minCompat = 0;
                bool clientCallable = true;
                int mode = ModeOptimistic; // missing [MetaMethod] / missing Mode → Optimistic default

                if (mm != null)
                {
                    var versionArg = mm.NamedArguments.FirstOrDefault(a => a.Key == "Version");
                    if (!versionArg.Value.IsNull && versionArg.Value.Value is int v) version = v;
                    var minArg = mm.NamedArguments.FirstOrDefault(a => a.Key == "MinCompatibleVersion");
                    if (!minArg.Value.IsNull && minArg.Value.Value is int mc) minCompat = mc;
                    var genArg = mm.NamedArguments.FirstOrDefault(a => a.Key == "GenerateClientApi");
                    if (!genArg.Value.IsNull && genArg.Value.Value is false) clientCallable = false;

                    var modeArg = mm.NamedArguments.FirstOrDefault(a => a.Key == "Mode");
                    if (!modeArg.Value.IsNull && modeArg.Value.Value is int md) mode = md;
                    // Legacy bool forms map to Query/Signal (never Optimistic-family).
                    var queryArg = mm.NamedArguments.FirstOrDefault(a => a.Key == "Query");
                    if (!queryArg.Value.IsNull && queryArg.Value.Value is true) mode = 6;
                    var signalArg = mm.NamedArguments.FirstOrDefault(a => a.Key == "Signal");
                    if (!signalArg.Value.IsNull && signalArg.Value.Value is true) mode = 7;
                }

                if (!clientCallable) continue;

                if (mode == ModeServerPatch)
                    return true; // always patches → needs the copy unconditionally
                if ((mode == ModeOptimistic || mode == ModeServer || mode == ModeCrossOptimistic)
                    && (version > minCompat || configHasBoundary))
                    return true; // runs a divergent local/replay body → force-downgradeable to ServerPatch
            }
            return false;
        }

        /// <summary>
        /// <c>[MetaService(PatchTracking = ...)]</c>, default <c>true</c>. When false the service
        /// opts out of copy generation and force-patch clients are rejected at negotiation.
        /// </summary>
        public static bool InterfacePatchTrackingOptIn(INamedTypeSymbol serviceInterface)
        {
            var msAttr = serviceInterface.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MetaServiceAttr);
            if (msAttr == null) return true;
            var pt = msAttr.NamedArguments.FirstOrDefault(a => a.Key == "PatchTracking");
            if (!pt.Value.IsNull && pt.Value.Value is bool b) return b;
            return true;
        }

        /// <summary>
        /// Negotiation flag: the server can produce a patch for this service's methods. True when
        /// the service is force-patch-able and has not opted out. DeepDesync is deliberately not
        /// folded in — a non-force-patch-able service never receives a ForceServerPatch verdict,
        /// and a force-patch-able service that opts out is rejected by policy even if DeepDesync
        /// independently emits the copy.
        /// </summary>
        public static bool PatchTrackingAvailable(INamedTypeSymbol serviceInterface)
            => InterfaceIsForcePatchable(serviceInterface) && InterfacePatchTrackingOptIn(serviceInterface);

        /// <summary>
        /// Copy-generation decision, evaluated at the **state** level: a <c>{Impl}_PatchTracked</c>
        /// copy is emitted for EVERY service on a state if ANY service on that state needs patch
        /// tracking — because patch tracking wraps the state, and a single force-patched call can
        /// fan out across sibling services on the same state (e.g. ProfileService.BuyEnergy →
        /// EnergyService.AddPurchasedEnergy). If a sibling ran its raw (non-tracked) impl, its
        /// mutations to the shared state would bypass the wrapper and be missing from the diff.
        /// <para>
        /// "Needs patch tracking" = a sibling impl is <c>DeepDesync</c>, OR a sibling interface is
        /// force-patch-able and opted in (<see cref="PatchTrackingAvailable"/>). A per-service
        /// opt-out (<c>PatchTracking = false</c>) does NOT suppress the copy when a sibling forces
        /// tracking — the copy is still emitted so the sibling can be tracked; the opt-out only
        /// governs the per-method negotiation verdict (<see cref="PatchTrackingAvailable"/>).
        /// </para>
        /// Scans the state's own assembly, so both <c>PatchTrackedClassGenerator</c> (which has the
        /// state symbol from the impl attribute) and <c>ServerMetaConfigurationGenerator</c> reach
        /// the identical verdict without threading a <c>Compilation</c> through.
        /// </summary>
        public static bool StateNeedsPatchCopy(INamedTypeSymbol stateType)
        {
            foreach (var type in EnumerateTypes(stateType.ContainingAssembly.GlobalNamespace))
            {
                var implAttr = type.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceImplAttribute");
                if (implAttr == null || implAttr.ConstructorArguments.Length < 2) continue;

                var implState = implAttr.ConstructorArguments[1].Value as INamedTypeSymbol;
                if (!SymbolEqualityComparer.Default.Equals(implState, stateType)) continue;

                var dd = implAttr.NamedArguments.FirstOrDefault(a => a.Key == "DeepDesync");
                if (!dd.Value.IsNull && dd.Value.Value is true) return true;

                if (implAttr.ConstructorArguments[0].Value is INamedTypeSymbol iface
                    && PatchTrackingAvailable(iface))
                    return true;
            }
            return false;
        }

        private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
        {
            foreach (var t in ns.GetTypeMembers())
            {
                yield return t;
                foreach (var nested in t.GetTypeMembers())
                    yield return nested;
            }
            foreach (var child in ns.GetNamespaceMembers())
                foreach (var t in EnumerateTypes(child))
                    yield return t;
        }
    }
}
