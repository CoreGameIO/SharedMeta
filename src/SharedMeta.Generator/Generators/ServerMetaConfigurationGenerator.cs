using System.Text;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Information about a [MetaServiceImpl] class for server configuration.
    /// </summary>
    public class ServiceImplInfo
    {
        public string InterfaceName { get; set; } = "";
        public string InterfaceFullName { get; set; } = "";
        public string ImplClassName { get; set; } = "";
        public string ImplClassFullName { get; set; } = "";
        public string StateTypeName { get; set; } = "";
        public string StateTypeFullName { get; set; } = "";
        public string Namespace { get; set; } = "";
        public List<string> SubscriberInterfaces { get; set; } = new();
        public List<string> ServerDependencies { get; set; } = new();
        public List<MethodSignatureInfo> MethodSignatures { get; set; } = new();
        public int AccessPolicy { get; set; } // 0=Open, 1=Authorized, 2=OwnerOnly
        public bool HasIsAuthorizedMethod { get; set; }
        public string? MetaInitMethodName { get; set; }
        /// <summary>
        /// Number of parameters on the <c>[MetaInit]</c> method.
        /// 1 = legacy <c>Init(int version)</c>; 2 = <c>Init(int version, int target)</c>.
        /// Generator emits the matching call shape.
        /// </summary>
        public int MetaInitParameterCount { get; set; } = 1;
        /// <summary>
        /// Config type full name from [MetaService(ConfigType = typeof(...))] or resolved default config.
        /// </summary>
        public string? ConfigTypeFullName { get; set; }
        /// <summary>
        /// True if [MetaService(DefaultConfig = true)] — needs resolution at aggregation time.
        /// </summary>
        public bool UsesDefaultConfig { get; set; }

        /// <summary>
        /// 0.33.0+ Full type names of [ServiceConfig] entries declared on the service
        /// interface, in declaration order — each independently versioned/published.
        /// </summary>
        public List<string> ServiceConfigTypeFullNames { get; set; } = new();
        /// <summary>True if [MetaServiceImpl(DeepDesync = true)].</summary>
        public bool DeepDesync { get; set; }

        /// <summary>0.24.2+ True when the <c>{Impl}_PatchTracked</c> copy is generated — either
        /// DeepDesync requested it or the service is force-patch-able and opted in. Drives the
        /// dispatch fork and the lazy getter. See <c>PatchTrackingPolicy</c>.</summary>
        public bool GeneratePatchTrackedCopy { get; set; }

        /// <summary>
        /// Named randoms declared via [NamedRandom] on the state class, in attribute declaration order.
        /// </summary>
        public List<NamedRandomDeclaration> NamedRandoms { get; set; } = new();

        /// <summary>
        /// Migration conditions declared via [MetaStateVersion] on the state class, sorted by StateVersion.
        /// Multiple conditions with the same StateVersion form an AND gate.
        /// </summary>
        public List<MigrationCondition> MigrationConditions { get; set; } = new();

        /// <summary>
        /// <c>EntityScope</c> declared via <c>[EntityScope(EntityScope.X)]</c> on the state
        /// class, defaulting to <c>Private</c> (enum value 0) when absent. Drives per-scope
        /// runtime pin behaviour in EntityGrain (Phases 5-7).
        /// </summary>
        public int EntityScopeValue { get; set; } = 0; // 0=Private, 1=Shared, 2=Global

        /// <summary>
        /// True when the impl class carries <c>[MetaServiceImpl]</c> but its declared service interface
        /// is marked <c>[ServerMetaService]</c> (and NOT <c>[MetaService]</c>) — a category error that
        /// would otherwise produce a confusing CS0103 on the non-existent dispatcher type.
        /// When true, the generator emits a <c>#error</c> naming the class and skips dispatcher-map
        /// emission for this entry.
        /// </summary>
        public bool HasInvalidServerMetaServiceCombo { get; set; }
    }

    /// <summary>
    /// Captured [NamedRandom] declaration for codegen emission.
    /// </summary>
    public class NamedRandomDeclaration
    {
        public string Name { get; set; } = "";
        public string? SeedOverride { get; set; }
    }

    /// <summary>
    /// A single condition declared by <c>[MetaStateVersion(stateVersion, "Major.Minor", typeof(ConfigType))]</c>.
    /// Multiple conditions with the same <see cref="StateVersion"/> form an AND gate.
    /// </summary>
    public class MigrationCondition
    {
        /// <summary>Target state schema version.</summary>
        public int StateVersion { get; set; }
        /// <summary>Minimum config Major component.</summary>
        public int Major { get; set; }
        /// <summary>Minimum config Minor component.</summary>
        public int Minor { get; set; }
        /// <summary>Config type full name. Null = primary config of the state.</summary>
        public string? ConfigTypeFullName { get; set; }
        /// <summary>Safe identifier suffix for field names (e.g. "MyGame_ExpeditionConfig").</summary>
        public string ConfigTypeIdent { get; set; } = "";
        /// <summary>
        /// 0.22.0+: <c>true</c> when <c>[MetaStateVersion(..., Breaking = true)]</c> — state
        /// shape changed in a way old clients cannot deserialize. Generator emits a strict
        /// <c>IsClientConfigCompatible</c> gate only for these. Non-breaking schema bumps
        /// (default <c>Breaking = false</c>) allow old clients to subscribe and rely on
        /// MemoryPack <c>VersionTolerant</c> / MessagePack key-based deserialization to
        /// tolerate the extra fields.
        /// </summary>
        public bool Breaking { get; set; }
    }

    /// <summary>
    /// Generates server-side meta configuration code:
    /// 1. GameServiceDiscovery - concrete implementation with dispatchers
    /// 2. Generated{State}MetaProvider - complete MetaProvider per state type
    /// 3. Generated{State}MetaProviderFactory - factory per state type
    /// 4. MetaServiceCollectionExtensions - ConfigureMeta() extension method
    ///
    /// This removes all meta configuration from Program.cs - just call:
    ///   services.ConfigureMeta();
    /// </summary>
    public static class ServerMetaConfigurationGenerator
    {
        /// <summary>
        /// Analyze a [MetaServiceImpl] class and extract info.
        /// </summary>
        public static ServiceImplInfo? Analyze(INamedTypeSymbol symbol)
        {
            // Check for [MetaServiceImpl] attribute
            var attr = symbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceImplAttribute");
            if (attr == null) return null;

            // Get constructor arguments: (serviceInterface, stateType, params dependencies)
            if (attr.ConstructorArguments.Length < 2) return null;

            var serviceInterfaceArg = attr.ConstructorArguments[0];
            var stateTypeArg = attr.ConstructorArguments[1];

            var serviceInterface = serviceInterfaceArg.Value as INamedTypeSymbol;
            var stateType = stateTypeArg.Value as INamedTypeSymbol;

            if (serviceInterface == null || stateType == null) return null;

            var info = new ServiceImplInfo
            {
                InterfaceName = serviceInterface.Name,
                InterfaceFullName = serviceInterface.ToDisplayString(),
                ImplClassName = symbol.Name,
                ImplClassFullName = symbol.ToDisplayString(),
                StateTypeName = stateType.Name,
                StateTypeFullName = stateType.ToDisplayString(),
                Namespace = symbol.ContainingNamespace.ToDisplayString()
            };

            // Get subscriber interfaces from the service interface's [MetaService] attribute
            var metaServiceAttr = serviceInterface.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceAttribute");

            // Detect the category error: [MetaServiceImpl(iface, stateType)] on a class whose
            // iface is [ServerMetaService] (a bridge) rather than [MetaService] (an entity service).
            // [ServerMetaService] services must be plain POCOs registered in DI — pairing them with
            // [MetaServiceImpl] + a state type is a semantic mismatch (bridge has no shared state,
            // no dispatcher, no subscribe). Flagging here lets Generate() emit a clear #error in
            // ServerMetaConfiguration.g.cs instead of the misleading downstream CS0103 on the
            // non-existent {Iface}Dispatcher type.
            if (metaServiceAttr == null)
            {
                var hasServerMetaService = serviceInterface.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.ServerMetaServiceAttribute");
                if (hasServerMetaService)
                {
                    info.HasInvalidServerMetaServiceCombo = true;
                    // Skip the rest of Analyze — downstream reads (SubscriberInterfaces, AccessPolicy,
                    // ConfigType, MethodSignatures, etc.) all look for [MetaService] which is absent.
                    // Returning the half-populated info is enough for the diagnostic path.
                    return info;
                }
            }
            if (metaServiceAttr != null)
            {
                var subscriberArg = metaServiceAttr.NamedArguments.FirstOrDefault(a => a.Key == "SubscriberInterfaces");
                if (!subscriberArg.Value.IsNull && subscriberArg.Value.Values.Length > 0)
                {
                    foreach (var val in subscriberArg.Value.Values)
                    {
                        if (val.Value is INamedTypeSymbol subscriberType)
                        {
                            info.SubscriberInterfaces.Add(subscriberType.ToDisplayString());
                        }
                    }
                }
            }

            // Read AccessPolicy from [MetaService] attribute
            if (metaServiceAttr != null)
            {
                var accessPolicyArg = metaServiceAttr.NamedArguments.FirstOrDefault(a => a.Key == "AccessPolicy");
                if (!accessPolicyArg.Value.IsNull && accessPolicyArg.Value.Value is int policyValue)
                {
                    info.AccessPolicy = policyValue;
                }
            }

            // Read ConfigType and DefaultConfig from [MetaService] attribute
            if (metaServiceAttr != null)
            {
                var configTypeArg = metaServiceAttr.NamedArguments.FirstOrDefault(a => a.Key == "ConfigType");
                if (!configTypeArg.Value.IsNull && configTypeArg.Value.Value is INamedTypeSymbol configType)
                {
                    info.ConfigTypeFullName = configType.ToDisplayString();
                }
                else
                {
                    // Check DefaultConfig flag
                    var defaultConfigArg = metaServiceAttr.NamedArguments.FirstOrDefault(a => a.Key == "DefaultConfig");
                    if (!defaultConfigArg.Value.IsNull && defaultConfigArg.Value.Value is true)
                    {
                        info.UsesDefaultConfig = true;
                    }
                }

                // 0.33.0+ [ServiceConfig] entries — repeatable, positional, on the service interface.
                foreach (var scAttr in serviceInterface.GetAttributes()
                    .Where(a => a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.ServiceConfigAttribute"))
                {
                    if (scAttr.ConstructorArguments.Length > 0 && scAttr.ConstructorArguments[0].Value is INamedTypeSymbol scType)
                    {
                        info.ServiceConfigTypeFullNames.Add(scType.ToDisplayString());
                    }
                }
            }

            // Check if impl class has IsAuthorized(string) method (for Authorized policy)
            info.HasIsAuthorizedMethod = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(m => m.Name == "IsAuthorized"
                       && m.Parameters.Length == 1
                       && m.Parameters[0].Type.SpecialType == SpecialType.System_String
                       && (m.ReturnType.SpecialType == SpecialType.System_Boolean
                           || m.ReturnType.ToDisplayString() == "bool"));

            // Check for [MetaInit] method
            var metaInitMethod = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaInitAttribute"));
            if (metaInitMethod != null)
            {
                info.MetaInitMethodName = metaInitMethod.Name;
                info.MetaInitParameterCount = metaInitMethod.Parameters.Length;
            }

            // Check for DeepDesync = true
            var deepDesyncArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "DeepDesync");
            info.DeepDesync = !deepDesyncArg.Value.IsNull && deepDesyncArg.Value.Value is true;

            // 0.24.2+ Per-STATE decision (must match PatchTrackedClassGenerator's gate exactly so
            // the dispatch fork / sibling resolver never reference a copy that wasn't emitted):
            // the copy exists for every service on a state if any sibling on that state needs patch
            // tracking. Both generators call StateNeedsPatchCopy with the same state symbol.
            info.GeneratePatchTrackedCopy = info.DeepDesync || PatchTrackingPolicy.StateNeedsPatchCopy(stateType);

            // Collect [NamedRandom] attributes from the state type (positional — declaration order matters)
            foreach (var stateAttr in stateType.GetAttributes())
            {
                if (stateAttr.AttributeClass?.ToDisplayString() != "SharedMeta.Core.NamedRandomAttribute")
                    continue;
                if (stateAttr.ConstructorArguments.Length == 0) continue;
                if (stateAttr.ConstructorArguments[0].Value is not string name || string.IsNullOrEmpty(name))
                    continue;

                string? seedOverride = null;
                var seedArg = stateAttr.NamedArguments.FirstOrDefault(a => a.Key == "Seed");
                if (!seedArg.Value.IsNull && seedArg.Value.Value is string seedStr && !string.IsNullOrEmpty(seedStr))
                    seedOverride = seedStr;

                info.NamedRandoms.Add(new NamedRandomDeclaration { Name = name, SeedOverride = seedOverride });
            }

            // Collect [MetaStateVersion] attributes from the state class (migration breakpoints)
            foreach (var stateAttr in stateType.GetAttributes())
            {
                if (stateAttr.AttributeClass?.ToDisplayString() != "SharedMeta.Core.MetaStateVersionAttribute")
                    continue;
                if (stateAttr.ConstructorArguments.Length < 2) continue;

                if (stateAttr.ConstructorArguments[0].Value is not int stateVer) continue;
                if (stateAttr.ConstructorArguments[1].Value is not string verStr) continue;

                var configTypeSymbol = stateAttr.ConstructorArguments.Length > 2
                    ? stateAttr.ConstructorArguments[2].Value as INamedTypeSymbol
                    : null;

                var parts = verStr.Split('.');
                if (parts.Length < 2 ||
                    !int.TryParse(parts[0], out var major) ||
                    !int.TryParse(parts[1], out var minor))
                    continue;

                var configTypeFull = configTypeSymbol?.ToDisplayString();
                var configTypeIdent = configTypeFull != null
                    ? new string(configTypeFull.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())
                    : "";

                // 0.22.0: Breaking named-arg drives the IsClientConfigCompatible gate.
                // Default false = old clients still subscribe; true = reject with FeatureRequirement.
                var breakingArg = stateAttr.NamedArguments.FirstOrDefault(a => a.Key == "Breaking");
                var breaking = !breakingArg.Value.IsNull && breakingArg.Value.Value is bool b && b;

                info.MigrationConditions.Add(new MigrationCondition
                {
                    StateVersion  = stateVer,
                    Major         = major,
                    Minor         = minor,
                    ConfigTypeFullName = configTypeFull,
                    ConfigTypeIdent    = configTypeIdent,
                    Breaking      = breaking,
                });
            }
            // Sort by ascending StateVersion so the generator emits steps in order
            info.MigrationConditions.Sort((a, b) => a.StateVersion.CompareTo(b.StateVersion));

            // [EntityScope(EntityScope.X)] on the state class — 0.21.0 Phase 4. Default
            // (no attribute) is Private (0). Aggregated per-state at the end of Analyze
            // so multiple services on the same state share the same scope.
            foreach (var stateAttr in stateType.GetAttributes())
            {
                if (stateAttr.AttributeClass?.ToDisplayString() != "SharedMeta.Core.EntityScopeAttribute")
                    continue;
                if (stateAttr.ConstructorArguments.Length == 0) continue;
                if (stateAttr.ConstructorArguments[0].Value is int scopeVal)
                    info.EntityScopeValue = scopeVal;
                break; // single-attribute usage
            }

            // Get server dependencies from constructor arguments (params Type[])
            if (attr.ConstructorArguments.Length > 2)
            {
                var depsArg = attr.ConstructorArguments[2];
                if (!depsArg.IsNull && depsArg.Values.Length > 0)
                {
                    foreach (var dep in depsArg.Values)
                    {
                        if (dep.Value is INamedTypeSymbol depSymbol)
                        {
                            // Check if it's a server service ([ServerMetaService] attribute)
                            if (depSymbol.GetAttributes().Any(a =>
                                a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.ServerMetaServiceAttribute"))
                            {
                                info.ServerDependencies.Add(depSymbol.ToDisplayString());
                            }
                        }
                    }
                }
            }

            // Collect method signatures from the service interface for validation
            foreach (var member in serviceInterface.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary) continue;

                // Get method alias from [MetaMethod] attribute
                var metaMethodAttr = member.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaMethodAttribute");

                var methodAlias = member.Name;
                if (metaMethodAttr != null)
                {
                    var aliasArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Alias");
                    if (!aliasArg.Value.IsNull && aliasArg.Value.Value is string alias && !string.IsNullOrEmpty(alias))
                    {
                        methodAlias = alias;
                    }
                }

                // Read Query, Signal (both new Mode-based and legacy bool forms) and OpenAccess.
                // Canonical form is Mode = ExecutionMode.Query | Signal; legacy bools are kept for
                // back-compat (marked [Obsolete] on the attribute itself).
                bool isQuery = false;
                bool isOpenAccess = false;
                bool isSignal = false;
                bool isLocalQuery = false;
                if (metaMethodAttr != null)
                {
                    // Legacy bool form.
                    var queryArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Query");
                    bool legacyQuery = !queryArg.Value.IsNull && queryArg.Value.Value is true;

                    var signalArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Signal");
                    bool legacySignal = !signalArg.Value.IsNull && signalArg.Value.Value is true;

                    // Canonical Mode form. Mode is an int (enum value); compare against known positions.
                    // ExecutionMode layout: LocalQuery=0, Optimistic=1, Server=2, CrossOptimistic=3,
                    // ServerPatch=4, ServerReplace=5, Query=6, Signal=7.
                    bool modeIsQuery = false;
                    bool modeIsSignal = false;
                    var modeArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Mode");
                    if (!modeArg.Value.IsNull && modeArg.Value.Value is int modeVal)
                    {
                        if (modeVal == 0) isLocalQuery = true;
                        else if (modeVal == 6) modeIsQuery = true;
                        else if (modeVal == 7) modeIsSignal = true;
                    }

                    isQuery = legacyQuery || modeIsQuery;
                    isSignal = legacySignal || modeIsSignal;

                    var openAccessArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "OpenAccess");
                    isOpenAccess = !openAccessArg.Value.IsNull && openAccessArg.Value.Value is true;
                }

                // GenerateClientApi defaults to true; only false explicitly opts out of client RPC.
                bool generateClientApi = true;
                int methodVersion = 0;
                if (metaMethodAttr != null)
                {
                    var genApiArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "GenerateClientApi");
                    if (!genApiArg.Value.IsNull && genApiArg.Value.Value is false)
                        generateClientApi = false;

                    // [MetaMethod(Version = N)] — must match what GameServiceDiscoveryGenerator
                    // bakes into the GameMethodIds constant name (I{Svc}_{Alias}_v{Version}).
                    // Omitting it left Version=0 here, so DispatchCall / signal / migration switches
                    // emitted "_v0" constants that don't exist once a method declares Version != 0.
                    var versionArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Version");
                    if (!versionArg.Value.IsNull && versionArg.Value.Value is int v)
                        methodVersion = v;
                }

                var signatureString = SignatureHashGenerator.BuildSignatureString(info.InterfaceName, methodAlias, member);
                var signatureHash = SignatureHashGenerator.ComputeFnv1aHash(signatureString);

                // Per-method migration policy:
                //   [NoMigrate]            → SkipMigration = true
                //   [MinStateVersion(N)]   → MinStateVersion = N
                bool skipMigration = member.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.NoMigrateAttribute");
                int? minStateVersion = null;
                var minStateAttr = member.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MinStateVersionAttribute");
                if (minStateAttr != null && minStateAttr.ConstructorArguments.Length > 0
                    && minStateAttr.ConstructorArguments[0].Value is int msv)
                {
                    minStateVersion = msv;
                }

                info.MethodSignatures.Add(new MethodSignatureInfo
                {
                    ServiceName = info.InterfaceName,
                    MethodAlias = methodAlias,
                    Version = methodVersion,
                    SignatureString = signatureString,
                    SignatureHash = signatureHash,
                    IsQuery = isQuery,
                    IsOpenAccess = isOpenAccess,
                    IsSignal = isSignal,
                    IsLocalQuery = isLocalQuery,
                    SkipMigration = skipMigration,
                    MinStateVersion = minStateVersion,
                    GenerateClientApi = generateClientApi
                });
            }

            return info;
        }

        /// <summary>
        /// Resolve DefaultConfig references by finding the class with [MetaConfig(Default = true)]
        /// in the given compilation or referenced assemblies.
        /// Call this after collecting all ServiceImplInfos.
        /// </summary>
        public static void ResolveDefaultConfigs(IEnumerable<ServiceImplInfo> services, Compilation compilation)
        {
            string? defaultConfigType = null;

            // Search for [MetaConfig(Default = true)] in source types
            defaultConfigType = FindDefaultConfigType(compilation.Assembly.GlobalNamespace);

            // If not found in source, search referenced assemblies
            if (defaultConfigType == null)
            {
                foreach (var reference in compilation.References)
                {
                    var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                    if (assemblySymbol == null) continue;
                    var name = assemblySymbol.Name;
                    if (name.StartsWith("System") || name.StartsWith("Microsoft") ||
                        name.StartsWith("netstandard") || name.StartsWith("SharedMeta"))
                        continue;

                    defaultConfigType = FindDefaultConfigType(assemblySymbol.GlobalNamespace);
                    if (defaultConfigType != null) break;
                }
            }

            if (defaultConfigType == null) return;

            foreach (var service in services)
            {
                if (service.UsesDefaultConfig && service.ConfigTypeFullName == null)
                {
                    service.ConfigTypeFullName = defaultConfigType;
                }
            }
        }

        private static string? FindDefaultConfigType(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                var attr = type.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaConfigAttribute");
                if (attr != null)
                {
                    var defaultArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "Default");
                    if (!defaultArg.Value.IsNull && defaultArg.Value.Value is true)
                    {
                        return type.ToDisplayString();
                    }
                }
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                var result = FindDefaultConfigType(childNs);
                if (result != null) return result;
            }

            return null;
        }

        /// <summary>
        /// Emit one <c>#error</c> directive per misconfigured impl class. Called from both output
        /// paths (with and without the <c>#if SHAREDMETA_SERVER</c> wrapper) so the diagnostic is
        /// always visible regardless of how the generator runs.
        /// Directives are placed before any <c>using</c> / <c>namespace</c> / <c>#if</c> so Roslyn
        /// surfaces them reliably as CS1029 with the author's message, naming both the impl class
        /// and the interface that carries the conflicting attributes.
        /// </summary>
        private static void EmitServerMetaServiceComboErrors(StringBuilder sb, List<ServiceImplInfo> invalidServices)
        {
            if (invalidServices.Count == 0) return;
            foreach (var bad in invalidServices)
            {
                sb.AppendLine($"#error SharedMeta: '{bad.ImplClassFullName}' carries [MetaServiceImpl] but its service interface '{bad.InterfaceFullName}' is marked [ServerMetaService]. These attributes are mutually exclusive — [ServerMetaService] declares a bridge (no state, no dispatcher, no subscribe) and its impl must be a plain class registered in DI. Either drop [MetaServiceImpl] from the class and register it in DI, or replace [ServerMetaService] with [MetaService(StateType = ..., AccessPolicy = ...)] on the interface. See GUIDE.md §6.5.");
            }
        }

        /// <summary>
        /// Generate all server-side configuration code for Server projects.
        /// This version does NOT use #if SHAREDMETA_SERVER since it runs directly in the Server project.
        /// </summary>
        public static string? GenerateForServerProject(string rootNamespace, IEnumerable<ServiceImplInfo> serviceImpls, bool hasOrleansConfig = true)
        {
            var allServices = serviceImpls.Where(s => s != null).ToList();
            if (allServices.Count == 0) return null;

            // Split off misconfigured entries. They are emitted as #error directives at the top of the
            // generated file and excluded from the rest of the pipeline to suppress follow-on errors
            // (missing dispatcher type, missing state type, etc.) that would otherwise drown the real diagnostic.
            var invalidServices = allServices.Where(s => s.HasInvalidServerMetaServiceCombo).ToList();
            var services = allServices.Where(s => !s.HasInvalidServerMetaServiceCombo).ToList();

            // Group by state type
            var byStateType = services
                .GroupBy(s => s.StateTypeFullName)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Collect all unique server dependencies
            var allServerDeps = services
                .SelectMany(s => s.ServerDependencies)
                .Distinct()
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Server-side meta configuration - generated directly in Server project.");
            EmitServerMetaServiceComboErrors(sb, invalidServices);
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Transport;");
            sb.AppendLine("using SharedMeta.Server;");
            sb.AppendLine("using SharedMeta.Server.Core;");
            sb.AppendLine("using SharedMeta.Server.Core.Grains;");
            sb.AppendLine("using Microsoft.Extensions.Logging;");

            // Collect all unique namespaces
            var namespaces = services
                .Select(s => s.Namespace)
                .Distinct()
                .OrderBy(n => n);
            foreach (var ns in namespaces)
            {
                sb.AppendLine($"using {ns};");
            }
            // Add Server namespace for dispatchers
            foreach (var ns in namespaces.Where(n => !n.EndsWith(".Server")))
            {
                sb.AppendLine($"using {ns}.Server;");
            }

            sb.AppendLine();
            sb.AppendLine($"namespace {rootNamespace}.Server");
            sb.AppendLine("{");

            // 1. Generate GameServiceDiscovery concrete implementation
            GenerateGameServiceDiscovery(sb, services);
            sb.AppendLine();

            // 2. Generate MetaProvider for each state type
            foreach (var kvp in byStateType)
            {
                GenerateMetaProvider(sb, kvp.Key, kvp.Value, allServerDeps);
                sb.AppendLine();
                GenerateMetaProviderFactory(sb, kvp.Key, kvp.Value.First().StateTypeName, kvp.Value.Any(s => s.DeepDesync));
                sb.AppendLine();
            }

            // 3. Generate EntityGrainResolver
            GenerateEntityGrainResolver(sb, byStateType);
            sb.AppendLine();

            // 3b. Generate ConfigDownloadUrlResolver
            GenerateConfigDownloadUrlResolver(sb, byStateType);
            sb.AppendLine();

            // 3c. Generate IConfigByteSource (0.26.2+): non-generic byte source paired with
            //     app.MapMetaConfigDownload() (no generic) — auto-routes by stateTypeName.
            GenerateConfigByteSource(sb, byStateType);
            sb.AppendLine();

            // 4. Generate ConfigureMeta extension method
            GenerateConfigureMetaExtension(sb, byStateType, allServerDeps, rootNamespace, hasOrleansConfig);
            sb.AppendLine();

            // 0.24.0+ legacy MetaMethodSignatureValidator emit removed — replaced by
            // ClientSignatureAnnotated handshake (per docs/adr/0.24.0-server-signature-handshake.md).

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Generate all server-side configuration code.
        /// Uses #if SHAREDMETA_SERVER wrapper (legacy mode for Shared project generation).
        /// </summary>
        public static string? Generate(string rootNamespace, IEnumerable<ServiceImplInfo> serviceImpls, bool hasOrleansConfig = true)
        {
            var allServices = serviceImpls.Where(s => s != null).ToList();
            if (allServices.Count == 0) return null;

            // Misconfigured entries are surfaced as #error directives at file top and excluded
            // from the rest of the generation (see GenerateForServerProject for the matching split).
            var invalidServices = allServices.Where(s => s.HasInvalidServerMetaServiceCombo).ToList();
            var services = allServices.Where(s => !s.HasInvalidServerMetaServiceCombo).ToList();

            // Group by state type
            var byStateType = services
                .GroupBy(s => s.StateTypeFullName)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Collect all unique server dependencies
            var allServerDeps = services
                .SelectMany(s => s.ServerDependencies)
                .Distinct()
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Server-side meta configuration - only compiled when SHAREDMETA_SERVER is defined.");
            sb.AppendLine("// Add <DefineConstants>SHAREDMETA_SERVER</DefineConstants> to your server project.");
            EmitServerMetaServiceComboErrors(sb, invalidServices);
            sb.AppendLine("#if SHAREDMETA_SERVER");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Transport;");
            sb.AppendLine("using SharedMeta.Server;");
            sb.AppendLine("using SharedMeta.Server.Core;");
            sb.AppendLine("using SharedMeta.Server.Core.Grains;");
            sb.AppendLine();

            // Collect all unique namespaces
            var namespaces = services
                .Select(s => s.Namespace)
                .Distinct()
                .OrderBy(n => n);
            foreach (var ns in namespaces)
            {
                sb.AppendLine($"using {ns};");
            }
            // Add Server namespace for dispatchers
            foreach (var ns in namespaces.Where(n => !n.EndsWith(".Server")))
            {
                sb.AppendLine($"using {ns}.Server;");
            }

            sb.AppendLine();
            sb.AppendLine($"namespace {rootNamespace}.Server");
            sb.AppendLine("{");

            // 1. Generate GameServiceDiscovery concrete implementation
            GenerateGameServiceDiscovery(sb, services);
            sb.AppendLine();

            // 2. Generate MetaProvider for each state type
            foreach (var kvp in byStateType)
            {
                GenerateMetaProvider(sb, kvp.Key, kvp.Value, allServerDeps);
                sb.AppendLine();
                GenerateMetaProviderFactory(sb, kvp.Key, kvp.Value.First().StateTypeName, kvp.Value.Any(s => s.DeepDesync));
                sb.AppendLine();
            }

            // 3. Generate EntityGrainResolver
            GenerateEntityGrainResolver(sb, byStateType);
            sb.AppendLine();

            // 3b. Generate ConfigDownloadUrlResolver
            GenerateConfigDownloadUrlResolver(sb, byStateType);
            sb.AppendLine();

            // 3c. Generate IConfigByteSource (0.26.2+): non-generic byte source paired with
            //     app.MapMetaConfigDownload() (no generic) — auto-routes by stateTypeName.
            GenerateConfigByteSource(sb, byStateType);
            sb.AppendLine();

            // 4. Generate ConfigureMeta extension method
            GenerateConfigureMetaExtension(sb, byStateType, allServerDeps, rootNamespace, hasOrleansConfig);
            sb.AppendLine();

            // 0.24.0+ legacy MetaMethodSignatureValidator emit removed — replaced by
            // ClientSignatureAnnotated handshake (per docs/adr/0.24.0-server-signature-handshake.md).

            sb.AppendLine("}");
            sb.AppendLine("#endif // SHAREDMETA_SERVER");
            return sb.ToString();
        }

        private static void GenerateGameServiceDiscovery(StringBuilder sb, List<ServiceImplInfo> services)
        {
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Generated server implementation of GameServiceDiscoveryBase.");
            sb.AppendLine("    /// Provides service dispatchers and factory methods.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public sealed class GameServiceDiscovery : GameServiceDiscoveryBase");
            sb.AppendLine("    {");
            sb.AppendLine("        public static readonly GameServiceDiscovery Instance = new();");
            sb.AppendLine();
            sb.AppendLine("        private GameServiceDiscovery() { }");
            sb.AppendLine();

            // GetDispatcher
            sb.AppendLine("        public override ServerDispatcher? GetDispatcher(string serviceName)");
            sb.AppendLine("        {");
            sb.AppendLine("            return serviceName switch");
            sb.AppendLine("            {");
            foreach (var service in services)
            {
                sb.AppendLine($"                \"{service.InterfaceName}\" => (svc, methodId, payload, ser) => {service.InterfaceName}Dispatcher.Dispatch(({service.InterfaceName})svc, methodId, payload, ser),");
            }
            sb.AppendLine("                _ => null");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine();

            // GetSubscriberDispatcher
            sb.AppendLine("        public override SubscriberDispatcher? GetSubscriberDispatcher(string serviceName)");
            sb.AppendLine("        {");
            sb.AppendLine("            return serviceName switch");
            sb.AppendLine("            {");
            foreach (var service in services.Where(s => s.SubscriberInterfaces.Count > 0))
            {
                var baseName = GetBaseName(service.InterfaceName);
                sb.AppendLine($"                \"{service.InterfaceName}\" => (svc, methodId, data, ser) => global::{service.Namespace}.Server.{baseName}SubscriberDispatcher.Dispatch(svc, methodId, data, ser),");
            }
            sb.AppendLine("                _ => null");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine();

            // CreateService
            sb.AppendLine("        public override object CreateService(string serviceName)");
            sb.AppendLine("        {");
            sb.AppendLine("            return serviceName switch");
            sb.AppendLine("            {");
            foreach (var service in services)
            {
                sb.AppendLine($"                \"{service.InterfaceName}\" => new {service.ImplClassFullName}(),");
            }
            sb.AppendLine("                _ => throw new InvalidOperationException($\"Unknown service: {serviceName}\")");
            sb.AppendLine("            };");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
        }

        private static void GenerateMetaProvider(
            StringBuilder sb,
            string stateTypeFullName,
            List<ServiceImplInfo> services,
            List<string> allServerDeps)
        {
            var stateTypeName = services.First().StateTypeName;
            var className = $"Generated{stateTypeName}MetaProvider";
            var servicesWithSubscribers = services.Where(s => s.SubscriberInterfaces.Count > 0).ToList();

            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Generated MetaProvider for {stateTypeName}.");
            sb.AppendLine($"    /// Inherits common functionality from MetaProviderBase, only implements dispatch logic.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public sealed class {className} : MetaProviderBase<{stateTypeFullName}>");
            sb.AppendLine("    {");

            // Cached service instances
            sb.AppendLine("        // Cached service instances");
            foreach (var service in services)
            {
                var fieldName = GetFieldName(service.InterfaceName);
                sb.AppendLine($"        private {service.InterfaceName}? {fieldName};");
            }
            sb.AppendLine();

            // Constructor
            sb.AppendLine($"        public {className}(");
            sb.AppendLine("            Func<Type, object>? serviceResolver = null,");
            sb.AppendLine("            global::SharedMeta.Server.EntityCallHandler? entityCallHandler = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            ServiceResolver = serviceResolver;");
            sb.AppendLine("            EntityCallHandler = entityCallHandler;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // 0.21.0 Phase 4: EntityScope override — emitted only when the state declares
            // a non-Private scope (Private is the base default, no override needed).
            // All services on the same state share the scope; take the first.
            var stateScope = services.Select(s => s.EntityScopeValue).FirstOrDefault();
            if (stateScope != 0)
            {
                var scopeName = stateScope == 1 ? "Shared" : stateScope == 2 ? "Global" : "Private";
                sb.AppendLine($"        public override SharedMeta.Core.EntityScope Scope => SharedMeta.Core.EntityScope.{scopeName};");
                sb.AppendLine();

                // For Global: migration driver is server's IConfigVersionResolver.CurrentClientVersion,
                // NOT the caller's version. Override only when the generated provider has access
                // to _configVersionResolver (i.e., it has a primary config provider).
                if (stateScope == 2)
                {
                    sb.AppendLine("        protected override string? ResolveMigrationDriverForGlobal(string? callerClientVersion)");
                    sb.AppendLine("            => _configVersionResolver?.CurrentClientVersion ?? callerClientVersion;");
                    sb.AppendLine();
                }
            }

            // NamedRandomDescriptors override — union of [NamedRandom] attributes across services sharing this state.
            // Since multiple services may share the same state, use any service's collected list (should be identical).
            var namedRandoms = services
                .Select(s => s.NamedRandoms)
                .FirstOrDefault(nr => nr.Count > 0);
            if (namedRandoms is { Count: > 0 })
            {
                sb.AppendLine("        private static readonly SharedMeta.Core.Random.NamedRandomDescriptor[] _namedRandomDescriptors = new[]");
                sb.AppendLine("        {");
                foreach (var nr in namedRandoms)
                {
                    var seedArg = nr.SeedOverride != null ? $"\"{nr.SeedOverride}\"" : "null";
                    sb.AppendLine($"            new SharedMeta.Core.Random.NamedRandomDescriptor(\"{nr.Name}\", {seedArg}),");
                }
                sb.AppendLine("        };");
                sb.AppendLine();
                sb.AppendLine("        protected override IReadOnlyList<SharedMeta.Core.Random.NamedRandomDescriptor> NamedRandomDescriptors => _namedRandomDescriptors;");
                sb.AppendLine();
            }

            // Override OnDeactivating to clear service cache
            sb.AppendLine("        public override void OnDeactivating()");
            sb.AppendLine("        {");
            foreach (var service in services)
            {
                var fieldName = GetFieldName(service.InterfaceName);
                sb.AppendLine($"            {fieldName} = null;");
            }
            // Clear per-call config cache (cleared anyway on grain teardown, explicit for clarity).
            sb.AppendLine("            ClearConfigCache();");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Override InitializeConfig to resolve config by version.
            // Also emit secondary config providers for [MetaStateVersion] conditions that reference
            // additional config types beyond the primary service config.
            var configType = services.Select(s => s.ConfigTypeFullName).FirstOrDefault(c => c != null);

            // Collect all migration conditions (dedup across services sharing the same state).
            var allMigConds = services
                .SelectMany(s => s.MigrationConditions)
                .GroupBy(c => $"{c.StateVersion}:{c.ConfigTypeFullName ?? "__primary__"}")
                .Select(g => g.First())
                .OrderBy(c => c.StateVersion)
                .ToList();

            // Secondary config types: referenced in migration conditions but NOT the primary service config.
            var secondaryProviders = allMigConds
                .Where(c => c.ConfigTypeFullName != null && c.ConfigTypeFullName != configType)
                .GroupBy(c => c.ConfigTypeFullName!)
                .Select(g => (ConfigType: g.Key, Ident: g.First().ConfigTypeIdent))
                .ToList();

            // 0.33.0+ [ServiceConfig] entries — aggregated across every [MetaServiceImpl]
            // sharing this state, deduped by type (first-seen order). Same state-wide collapsing
            // simplification `configType` above already uses for multi-service states —
            // Context.Configs is one shared list per entity, not per-service. Each is
            // independently versioned/published — no pin / EntityScope.Global support yet
            // (unlike the legacy primary config's GetCachedConfigForClient below); follow-up work.
            var serviceConfigTypes = services
                .SelectMany(s => s.ServiceConfigTypeFullNames)
                .Distinct()
                .Select(t => (ConfigType: t, Ident: new string(t.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())))
                .ToList();

            // Emit OnInitialize when a primary config exists, migration conditions reference
            // secondary config types, or the state has [ServiceConfig] entries.
            bool hasAnyConfigProvider = configType != null || secondaryProviders.Count > 0 || serviceConfigTypes.Count > 0;

            if (configType != null)
            {
                sb.AppendLine($"        private SharedMeta.Server.Core.IMetaConfigProvider<{configType}>? _configProvider;");
            }
            // _configVersionResolver is needed by the legacy primary's Global branch AND by
            // [ServiceConfig]'s Global branch (GetCachedServiceConfigsForClient/Versions below) —
            // declare/resolve it whenever ANY config provider exists, not just the legacy primary.
            if (hasAnyConfigProvider)
            {
                sb.AppendLine($"        private SharedMeta.Server.Core.IConfigVersionResolver? _configVersionResolver;");
            }
            foreach (var sec in secondaryProviders)
                sb.AppendLine($"        private SharedMeta.Server.Core.IMetaConfigProvider<{sec.ConfigType}>? _configProvider_{sec.Ident};");
            foreach (var sc in serviceConfigTypes)
            {
                sb.AppendLine($"        private SharedMeta.Server.Core.IMetaConfigProvider<{sc.ConfigType}>? _serviceConfigProvider_{sc.Ident};");
                sb.AppendLine($"        private static readonly SharedMeta.Core.MetaConfigVersionResolver? _serviceConfigVersionPolicyResolver_{sc.Ident}");
                sb.AppendLine($"            = SharedMeta.Core.MetaConfigVersionResolver.ForType(typeof({sc.ConfigType}));");
                sb.AppendLine($"        private readonly System.Collections.Generic.Dictionary<string, {sc.ConfigType}> _serviceConfigCacheByClient_{sc.Ident} = new System.Collections.Generic.Dictionary<string, {sc.ConfigType}>();");
            }

            if (hasAnyConfigProvider)
            {
                // Every config resolution funnels its incoming version through this. A
                // server-originated call (admin action, framework grain, timer) carries no
                // CallerClientVersion, and providers reject a missing version outright — each
                // resolution site that forgot to substitute was its own latent
                // "clientAppVersion is required" crash.
                sb.AppendLine();
                sb.AppendLine("        private string? EffectiveClientVersion(string? clientVersion)");
                sb.AppendLine("            => string.IsNullOrEmpty(clientVersion) ? _configVersionResolver?.CurrentClientVersion : clientVersion;");

                sb.AppendLine();
                sb.AppendLine("        protected override void OnInitialize(Microsoft.Extensions.Logging.ILogger? logger)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (ServiceResolver != null)");
                sb.AppendLine("            {");
                if (configType != null)
                {
                    sb.AppendLine($"                _configProvider = (SharedMeta.Server.Core.IMetaConfigProvider<{configType}>)ServiceResolver(typeof(SharedMeta.Server.Core.IMetaConfigProvider<{configType}>));");
                }
                // IConfigVersionResolver is optional server-wide infrastructure (Global-scope
                // client-version substitution) — most projects never register it, so its absence
                // is expected and stays silent.
                sb.AppendLine($"                try {{ _configVersionResolver = ServiceResolver(typeof(SharedMeta.Server.Core.IConfigVersionResolver)) as SharedMeta.Server.Core.IConfigVersionResolver; }}");
                sb.AppendLine($"                catch (Exception e) {{ logger?.LogError(e, \"Unable resolve ConfigVersionResolver\"); }}");
                // Secondary/[ServiceConfig] providers are NOT optional — the state declared them,
                // so a missing DI registration is a real misconfiguration. Previously caught and
                // discarded silently: the field stayed null, GetCachedServiceConfigVersionsForClient
                // skipped it and returned default(0,0,0) for that slot with no visible error anywhere
                // — a config-version resolution failure that looked identical to a legitimate
                // unmatched [MetaConfigVersion] rule. Logging here (Logger is set at
                // MetaProviderBase.cs:163, before OnInitialize() runs at line 190) surfaces the
                // real cause instead.
                foreach (var sec in secondaryProviders)
                {
                    sb.AppendLine($"                try {{ _configProvider_{sec.Ident} = ServiceResolver(typeof(SharedMeta.Server.Core.IMetaConfigProvider<{sec.ConfigType}>)) as SharedMeta.Server.Core.IMetaConfigProvider<{sec.ConfigType}>; }}");
                    sb.AppendLine($"                catch (System.Exception ex) {{ logger?.LogError(ex, \"[{{StateType}}] Failed to resolve IMetaConfigProvider<{sec.ConfigType}> from DI — config version for this type will silently default to 0.0.0. Register it in ConfigureMeta.\", \"{stateTypeFullName}\"); }}");
                }
                foreach (var sc in serviceConfigTypes)
                {
                    sb.AppendLine($"                try {{ _serviceConfigProvider_{sc.Ident} = ServiceResolver(typeof(SharedMeta.Server.Core.IMetaConfigProvider<{sc.ConfigType}>)) as SharedMeta.Server.Core.IMetaConfigProvider<{sc.ConfigType}>; }}");
                    sb.AppendLine($"                catch (System.Exception ex) {{ logger?.LogError(ex, \"[{{StateType}}] Failed to resolve IMetaConfigProvider<{sc.ConfigType}> from DI — config version for this type will silently default to 0.0.0. Register it in ConfigureMeta.\", \"{stateTypeFullName}\"); }}");
                }
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();

                // 0.21.0 Phase 5: EstablishConfigPinsFromClientVersion override —
                // resolves the client's config version for each registered IMetaConfigProvider<>
                // (primary + secondaries) and pins it. Called from EntityGrain.SubscribeAsync
                // for Private (and Shared) scopes; Global never reaches here.
                sb.AppendLine("        public override void EstablishConfigPinsFromClientVersion(string? clientVersion)");
                sb.AppendLine("        {");
                sb.AppendLine("            clientVersion = EffectiveClientVersion(clientVersion);");
                if (configType != null)
                {
                    sb.AppendLine("            if (_configProvider != null)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var _pv = _configProvider.ResolveForClient(clientVersion, _configVersionPolicyResolver);");
                    sb.AppendLine($"                SetConfigPin(typeof({configType}).FullName!, _pv);");
                    sb.AppendLine("            }");
                }
                foreach (var sec in secondaryProviders)
                {
                    sb.AppendLine($"            if (_configProvider_{sec.Ident} != null)");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                var _sv = _configProvider_{sec.Ident}.ResolveForClient(clientVersion, null);");
                    sb.AppendLine($"                SetConfigPin(typeof({sec.ConfigType}).FullName!, _sv);");
                    sb.AppendLine("            }");
                }
                // 0.33.0+ [ServiceConfig] entries pin the same way the primary does — the pin
                // dictionary is already keyed by config type full name (type-agnostic), so this
                // is the same call shape as the primary/secondary loops above, just looped per
                // declared [ServiceConfig] type.
                foreach (var sc in serviceConfigTypes)
                {
                    sb.AppendLine($"            if (_serviceConfigProvider_{sc.Ident} != null)");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                var _scv = _serviceConfigProvider_{sc.Ident}.ResolveForClient(clientVersion, _serviceConfigVersionPolicyResolver_{sc.Ident});");
                    sb.AppendLine($"                SetConfigPin(typeof({sc.ConfigType}).FullName!, _scv);");
                    sb.AppendLine("            }");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                // 0.22.0: ValidateClientCompatibleWithPins is now permissive on shared-scope
                // cross-version subscriptions. Open-Closed config evolution + boundary-driven
                // force-patch (see [MetaConfigStructureBoundary] + EntityGrain.ComputePerEntityCapabilities)
                // handle compatibility:
                //   • joiner.Version > pin.Version → newer client has all old code paths;
                //     runs native on the older-pinned data (additive fields default to zero).
                //   • joiner.Version < pin.Version, no boundary in range → old code paths still
                //     valid on new data (additive evolution); native execution.
                //   • joiner.Version < pin.Version, boundary declared → ComputePerEntityCapabilities
                //     emits ForceServerPatchServices for the affected services; broadcasts to this
                //     subscriber are tailored to PatchBytes by BroadcastTailor.
                // The supported version range is enforced upstream at the transport level
                // (MetaTransportOptions.MinClientVersion / MaxClientVersion). The pre-0.22.0
                // strict reject was redundant once boundary-driven force-patch landed.
                if (stateScope == 1) // EntityScope.Shared
                {
                    sb.AppendLine("        public override bool ValidateClientCompatibleWithPins(string? clientVersion, out string? reason)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            // 0.22.0: permissive — Open-Closed + boundary force-patch handles cross-version subscribe.");
                    sb.AppendLine("            reason = null;");
                    sb.AppendLine("            return true;");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            if (configType != null)
            {
                // 0.21.0: emit InitializeConfigAsync (preferred — uses GetConfigAsync so
                // BroadcastingConfigProvider et al. can fetch from registry without sync-over-async)
                // AND a sync InitializeConfig that forwards to the async variant via blocking wait
                // for code paths that still call it. Most paths go through EntityGrain.SubscribeAsync
                // which awaits the async variant.
                sb.AppendLine("        public override async System.Threading.Tasks.Task InitializeConfigAsync(SharedMeta.Core.MetaConfigVersion version)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_configProvider == null) { base.InitializeConfig(version); return; }");
                sb.AppendLine();
                sb.AppendLine("            // Resolve version for new entities (persisted version is 0,0).");
                sb.AppendLine("            // 0.21.0+: when IConfigVersionResolver IS registered, default version is derived from");
                sb.AppendLine("            // CurrentClientVersion via ResolveForClient using the config class's [MetaConfigVersion]");
                sb.AppendLine("            // rules. When NOT registered, fall back to default (0,0,0) for projects without configs.");
                sb.AppendLine("            if (version.Major == 0 && version.Minor == 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                if (_configVersionResolver != null && !string.IsNullOrEmpty(_configVersionResolver.CurrentClientVersion))");
                sb.AppendLine("                {");
                sb.AppendLine("                    var defaultVersion = _configProvider.ResolveForClient(_configVersionResolver.CurrentClientVersion, _configVersionPolicyResolver);");
                sb.AppendLine($"                    version = _configVersionResolver.ResolveVersion(\"{stateTypeFullName}\", Context.EntityId, defaultVersion);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            base.InitializeConfig(version);");
                sb.AppendLine("            // Async fetch — works with sync providers (returns immediately via Task.FromResult) and");
                sb.AppendLine("            // with async providers like BroadcastingConfigProvider (registry round-trip on cold cache).");
                sb.AppendLine("            MetaContext!.Config = await _configProvider.GetConfigAsync(version);");
                sb.AppendLine("        }");
                sb.AppendLine();

                // ResolveClientConfigVersion — uses [MetaConfigVersion] rules on the config class
                // to pick the config branch appropriate for the connecting client's app version.
                sb.AppendLine($"        private static readonly SharedMeta.Core.MetaConfigVersionResolver? _configVersionPolicyResolver");
                sb.AppendLine($"            = SharedMeta.Core.MetaConfigVersionResolver.ForType(typeof({configType}));");
                sb.AppendLine();
                sb.AppendLine("        public override SharedMeta.Core.MetaConfigVersion ResolveClientConfigVersion(string? clientVersion)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_configProvider == null) return ConfigVersion;");
                sb.AppendLine("            return _configProvider.ResolveForClient(EffectiveClientVersion(clientVersion), _configVersionPolicyResolver);");
                sb.AppendLine("        }");
                sb.AppendLine();

                // 0.21.0: scope-aware effective version for the subscribe response — mirrors
                // GetCachedConfigForClient resolution. Private/Shared returns pin if set;
                // Global substitutes IConfigVersionResolver.CurrentClientVersion.
                sb.AppendLine("        public override SharedMeta.Core.MetaConfigVersion ResolveEffectiveConfigVersion(string? clientVersion)");
                sb.AppendLine("        {");
                if (stateScope == 2) // Global
                {
                    sb.AppendLine("            if (_configProvider == null) return default;");
                    sb.AppendLine("            if (_configVersionResolver == null || string.IsNullOrEmpty(_configVersionResolver.CurrentClientVersion))");
                    sb.AppendLine("                return default;");
                    sb.AppendLine("            return _configProvider.ResolveForClient(_configVersionResolver.CurrentClientVersion, _configVersionPolicyResolver);");
                }
                else
                {
                    sb.AppendLine($"            if (TryGetConfigPin(typeof({configType}).FullName!, out var _pin))");
                    sb.AppendLine("                return _pin;");
                    sb.AppendLine("            return _configProvider?.ResolveForClient(clientVersion, _configVersionPolicyResolver) ?? ConfigVersion;");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                // GetCachedConfigForClient — per-call config cache:
                //   1. clientVersion → resolved MetaConfigVersion (via [MetaConfigVersion] rules)
                //   2. resolved version → TConfig instance (one entry per branch per grain activation)
                // 0.21.0: cache is cleared only on grain deactivation (ClearConfigCache). Live admin
                // rollouts of a new config patch arrive via the BroadcastingConfigProvider observer
                // path, which drops the stale TConfig entry from the provider's internal cache; the
                // next call here re-fetches via GetConfig and stores the fresh instance under the
                // same clientVersion key (because clientVersion → resolved-version mapping is stable
                // for that branch).
                sb.AppendLine($"        private readonly System.Collections.Generic.Dictionary<string, {configType}> _configCacheByClient = new System.Collections.Generic.Dictionary<string, {configType}>();");
                sb.AppendLine();
                sb.AppendLine("        protected override object? GetCachedConfigForClient(string? clientVersion)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_configProvider == null) return null;");
                if (stateScope == 2) // EntityScope.Global
                {
                    sb.AppendLine("            // 0.21.0 Phase 7 — EntityScope.Global: ignore the caller's clientVersion and");
                    sb.AppendLine("            // resolve under IConfigVersionResolver.CurrentClientVersion. Every observer of a");
                    sb.AppendLine("            // Global entity sees the same config regardless of who triggered the call.");
                    sb.AppendLine("            if (_configVersionResolver == null || string.IsNullOrEmpty(_configVersionResolver.CurrentClientVersion))");
                    sb.AppendLine("                throw new System.InvalidOperationException(");
                    sb.AppendLine($"                    \"[EntityScope(Global)] state '{stateTypeFullName}' requires IConfigVersionResolver.CurrentClientVersion. \" +");
                    sb.AppendLine("                    \"Register IConfigVersionResolver in DI and ensure CurrentClientVersion is non-empty.\");");
                    sb.AppendLine("            var effectiveClient = _configVersionResolver.CurrentClientVersion;");
                    sb.AppendLine("            var key = effectiveClient ?? string.Empty;");
                    sb.AppendLine("            if (!_configCacheByClient.TryGetValue(key, out var cached))");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var resolved = _configProvider.ResolveForClient(effectiveClient, _configVersionPolicyResolver);");
                    sb.AppendLine("                cached = _configProvider.GetConfig(resolved);");
                    sb.AppendLine("                _configCacheByClient[key] = cached;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            return cached;");
                }
                else
                {
                    sb.AppendLine("            // 0.21.0 Phase 5: pin (if active) overrides per-client resolution at the");
                    sb.AppendLine("            // dispatch boundary. Subscribe-time validation paths (compat gate, migration");
                    sb.AppendLine("            // cap) keep using ResolveClientConfigVersion directly — pin only applies to");
                    sb.AppendLine("            // per-call config materialization. Cache by clientVersion still; cache miss");
                    sb.AppendLine("            // path resolves: pin first, then ResolveClientConfigVersion.");
                    sb.AppendLine("            // clientVersion already carries the effective version: the caller's own, or the");
                    sb.AppendLine("            // entity owner's when the call is server-originated (MetaProviderBase resolves");
                    sb.AppendLine("            // it). EffectiveClientVersion only backstops an entity that never had a");
                    sb.AppendLine("            // subscriber, where there is no player to be wrong about.");
                    sb.AppendLine($"            bool _hasPin = TryGetConfigPin(typeof({configType}).FullName!, out var _pinned);");
                    sb.AppendLine("            var _effectiveClient = EffectiveClientVersion(clientVersion);");
                    sb.AppendLine("            var key = _effectiveClient ?? string.Empty;");
                    sb.AppendLine("            if (!_configCacheByClient.TryGetValue(key, out var cached))");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var resolved = _hasPin ? _pinned : ResolveClientConfigVersion(_effectiveClient);");
                    sb.AppendLine("                cached = _configProvider.GetConfig(resolved);");
                    sb.AppendLine("                _configCacheByClient[key] = cached;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            return cached;");
                }
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (hasAnyConfigProvider)
            {
                sb.AppendLine("        protected override void ClearConfigCache()");
                sb.AppendLine("        {");
                if (configType != null)
                    sb.AppendLine("            _configCacheByClient.Clear();");
                foreach (var sc in serviceConfigTypes)
                    sb.AppendLine($"            _serviceConfigCacheByClient_{sc.Ident}.Clear();");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (serviceConfigTypes.Count > 0)
            {
                // InitializeConfigsAsync — async pre-warm, called once by EntityGrain
                // right after EstablishConfigPinsFromClientVersion. Resolves the exact same
                // (scope, pin) branch GetCachedServiceConfigsForClient below will read
                // synchronously, but fetches via GetConfigAsync and pre-populates
                // _serviceConfigCacheByClient_X under the identical key — so an async-backed
                // provider (BroadcastingConfigProvider et al.) is never read cold from
                // HandleCallAsync's sync path.
                sb.AppendLine("        public override async System.Threading.Tasks.Task InitializeConfigsAsync(string? clientVersion)");
                sb.AppendLine("        {");
                for (int i = 0; i < serviceConfigTypes.Count; i++)
                {
                    var sc = serviceConfigTypes[i];
                    sb.AppendLine($"            if (_serviceConfigProvider_{sc.Ident} != null)");
                    sb.AppendLine("            {");
                    if (stateScope == 2) // EntityScope.Global
                    {
                        sb.AppendLine("                if (_configVersionResolver != null && !string.IsNullOrEmpty(_configVersionResolver.CurrentClientVersion))");
                        sb.AppendLine("                {");
                        sb.AppendLine("                    var _effectiveClient = _configVersionResolver.CurrentClientVersion;");
                        sb.AppendLine("                    var _key = _effectiveClient ?? string.Empty;");
                        sb.AppendLine($"                    if (!_serviceConfigCacheByClient_{sc.Ident}.TryGetValue(_key, out _))");
                        sb.AppendLine("                    {");
                        sb.AppendLine($"                        var _resolved = _serviceConfigProvider_{sc.Ident}.ResolveForClient(_effectiveClient, _serviceConfigVersionPolicyResolver_{sc.Ident});");
                        sb.AppendLine($"                        var _cfg = await _serviceConfigProvider_{sc.Ident}.GetConfigAsync(_resolved);");
                        sb.AppendLine($"                        _serviceConfigCacheByClient_{sc.Ident}[_key] = _cfg;");
                        sb.AppendLine("                    }");
                        sb.AppendLine("                }");
                    }
                    else
                    {
                        sb.AppendLine($"                bool _hasPin = TryGetConfigPin(typeof({sc.ConfigType}).FullName!, out var _pinned);");
                        sb.AppendLine("                string? _effectiveClient = clientVersion;");
                        sb.AppendLine("                if (_hasPin || !string.IsNullOrEmpty(_effectiveClient) || !string.IsNullOrEmpty(_configVersionResolver?.CurrentClientVersion))");
                        sb.AppendLine("                {");
                        sb.AppendLine("                    if (!_hasPin && string.IsNullOrEmpty(_effectiveClient))");
                        sb.AppendLine("                        _effectiveClient = _configVersionResolver?.CurrentClientVersion;");
                        sb.AppendLine("                    var _key = _effectiveClient ?? string.Empty;");
                        sb.AppendLine($"                    if (!_serviceConfigCacheByClient_{sc.Ident}.TryGetValue(_key, out _))");
                        sb.AppendLine("                    {");
                        sb.AppendLine($"                        var _resolved = _hasPin ? _pinned : _serviceConfigProvider_{sc.Ident}.ResolveForClient(_effectiveClient, _serviceConfigVersionPolicyResolver_{sc.Ident});");
                        sb.AppendLine($"                        var _cfg = await _serviceConfigProvider_{sc.Ident}.GetConfigAsync(_resolved);");
                        sb.AppendLine($"                        _serviceConfigCacheByClient_{sc.Ident}[_key] = _cfg;");
                        sb.AppendLine("                    }");
                        sb.AppendLine("                }");
                        sb.AppendLine("                // else: no pin, no CallerClientVersion, no CurrentClientVersion — nothing to");
                        sb.AppendLine("                // resolve yet. Leave uncached; the sync accessor's own cold-call guard");
                        sb.AppendLine("                // throws a clear error if this state is ever really called that way.");
                    }
                    sb.AppendLine("            }");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                // GetCachedServiceConfigsForClient / GetCachedServiceConfigVersionsForClient —
                // 0.33.0+ full parity with GetCachedConfigForClient: pin-first (set by
                // EstablishConfigPinsFromClientVersion above), EntityScope.Global substitutes
                // IConfigVersionResolver.CurrentClientVersion (every observer of a Global entity
                // sees the same branch, regardless of who's calling), else per-client resolve+cache.
                sb.AppendLine("        protected override object[] GetCachedServiceConfigsForClient(string? clientVersion)");
                sb.AppendLine("        {");
                sb.AppendLine($"            var _result = new object[{serviceConfigTypes.Count}];");
                for (int i = 0; i < serviceConfigTypes.Count; i++)
                {
                    var sc = serviceConfigTypes[i];
                    sb.AppendLine($"            if (_serviceConfigProvider_{sc.Ident} != null)");
                    sb.AppendLine("            {");
                    if (stateScope == 2) // EntityScope.Global
                    {
                        sb.AppendLine("                if (_configVersionResolver == null || string.IsNullOrEmpty(_configVersionResolver.CurrentClientVersion))");
                        sb.AppendLine("                    throw new System.InvalidOperationException(");
                        sb.AppendLine($"                        \"[EntityScope(Global)] state '{stateTypeFullName}' requires IConfigVersionResolver.CurrentClientVersion. \" +");
                        sb.AppendLine("                        \"Register IConfigVersionResolver in DI and ensure CurrentClientVersion is non-empty.\");");
                        sb.AppendLine("                var _effectiveClient = _configVersionResolver.CurrentClientVersion;");
                        sb.AppendLine("                var _key = _effectiveClient ?? string.Empty;");
                        sb.AppendLine($"                if (!_serviceConfigCacheByClient_{sc.Ident}.TryGetValue(_key, out var _cached))");
                        sb.AppendLine("                {");
                        sb.AppendLine($"                    var _resolved = _serviceConfigProvider_{sc.Ident}.ResolveForClient(_effectiveClient, _serviceConfigVersionPolicyResolver_{sc.Ident});");
                        sb.AppendLine($"                    _cached = _serviceConfigProvider_{sc.Ident}.GetConfig(_resolved);");
                        sb.AppendLine($"                    _serviceConfigCacheByClient_{sc.Ident}[_key] = _cached;");
                        sb.AppendLine("                }");
                        sb.AppendLine($"                _result[{i}] = _cached;");
                    }
                    else
                    {
                        sb.AppendLine($"                bool _hasPin = TryGetConfigPin(typeof({sc.ConfigType}).FullName!, out var _pinned);");
                        sb.AppendLine("                string? _effectiveClient = clientVersion;");
                        sb.AppendLine("                if (!_hasPin && string.IsNullOrEmpty(_effectiveClient))");
                        sb.AppendLine("                {");
                        sb.AppendLine("                    _effectiveClient = _configVersionResolver?.CurrentClientVersion;");
                        sb.AppendLine("                    if (string.IsNullOrEmpty(_effectiveClient))");
                        sb.AppendLine("                        throw new System.InvalidOperationException(");
                        sb.AppendLine($"                            \"Cold call into '{stateTypeFullName}' without CallerClientVersion and no IConfigVersionResolver.CurrentClientVersion configured. \" +");
                        sb.AppendLine("                            \"Register IConfigVersionResolver in DI or pass CallerClientVersion explicitly from the calling code.\");");
                        sb.AppendLine("                }");
                        sb.AppendLine("                var _key = _effectiveClient ?? string.Empty;");
                        sb.AppendLine($"                if (!_serviceConfigCacheByClient_{sc.Ident}.TryGetValue(_key, out var _cached))");
                        sb.AppendLine("                {");
                        sb.AppendLine($"                    var _resolved = _hasPin ? _pinned : _serviceConfigProvider_{sc.Ident}.ResolveForClient(_effectiveClient, _serviceConfigVersionPolicyResolver_{sc.Ident});");
                        sb.AppendLine($"                    _cached = _serviceConfigProvider_{sc.Ident}.GetConfig(_resolved);");
                        sb.AppendLine($"                    _serviceConfigCacheByClient_{sc.Ident}[_key] = _cached;");
                        sb.AppendLine("                }");
                        sb.AppendLine($"                _result[{i}] = _cached;");
                    }
                    sb.AppendLine("            }");
                }
                sb.AppendLine("            return _result;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine("        protected override SharedMeta.Core.MetaConfigVersion[] GetCachedServiceConfigVersionsForClient(string? clientVersion)");
                sb.AppendLine("        {");
                sb.AppendLine("            clientVersion = EffectiveClientVersion(clientVersion);");
                sb.AppendLine($"            var _result = new SharedMeta.Core.MetaConfigVersion[{serviceConfigTypes.Count}];");
                for (int i = 0; i < serviceConfigTypes.Count; i++)
                {
                    var sc = serviceConfigTypes[i];
                    sb.AppendLine($"            if (_serviceConfigProvider_{sc.Ident} != null)");
                    sb.AppendLine("            {");
                    if (stateScope == 2) // EntityScope.Global
                    {
                        sb.AppendLine("                if (_configVersionResolver != null && !string.IsNullOrEmpty(_configVersionResolver.CurrentClientVersion))");
                        sb.AppendLine($"                    _result[{i}] = _serviceConfigProvider_{sc.Ident}.ResolveForClient(_configVersionResolver.CurrentClientVersion, _serviceConfigVersionPolicyResolver_{sc.Ident});");
                    }
                    else
                    {
                        sb.AppendLine($"                if (TryGetConfigPin(typeof({sc.ConfigType}).FullName!, out var _pin))");
                        sb.AppendLine($"                    _result[{i}] = _pin;");
                        sb.AppendLine("                else");
                        sb.AppendLine($"                    _result[{i}] = _serviceConfigProvider_{sc.Ident}.ResolveForClient(clientVersion, _serviceConfigVersionPolicyResolver_{sc.Ident});");
                    }
                    sb.AppendLine("            }");
                }
                sb.AppendLine("            return _result;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Implement abstract DispatchCall — 0.22.0+: methodVersion routes (Alias, Version) tuples
            // to the matching declared body. Legacy/unversioned callers pass methodVersion=0 and the
            // generated dispatcher routes them to the lowest-versioned implementation under the alias.
            // 0.23.0+ Non-async: dispatchers return ValueTask<DispatchResult> directly, sync-completed
            // for sync-eligible methods. Removing `async` from this override skips the state-machine
            // box at the DispatchCall layer.
            // 0.24.0+ Switch on global ushort method id. Each method's case routes to its
            // owning service's dispatcher; the dispatcher's inner switch then finds the
            // specific impl by the same id. Jump table on ushort lowers to a computed branch.
            sb.AppendLine("        protected override System.Threading.Tasks.ValueTask<DispatchResult> DispatchCall(ushort methodId, System.ReadOnlyMemory<byte> payload)");
            sb.AppendLine("        {");
            sb.AppendLine("            return methodId switch");
            sb.AppendLine("            {");
            foreach (var service in services)
            {
                var baseName = GetBaseName(service.InterfaceName);
                // Queries route through DispatchCall (MetaProviderBase.HandleQueryAsync calls
                // DispatchCall(call.MethodId, ...)), so they must have cases here. Signals are
                // routed by the separate {Service}SignalDispatcher (see DispatchSignal override)
                // and stay out of the main DispatchCall switch. LocalQuery methods never reach the
                // server (the client runs them synchronously over local state), so they are excluded
                // here and from the per-service dispatcher switch — no server dispatch case at all.
                foreach (var ms in service.MethodSignatures.Where(m => !m.IsSignal && !m.IsLocalQuery))
                {
                    var idConst = "global::" + service.Namespace + ".Generated.GameMethodIds." + SignatureHashGenerator.MakeMethodIdConstName(ms.ServiceName, ms.MethodAlias, ms.Version);
                    if (service.GeneratePatchTrackedCopy)
                    {
                        sb.AppendLine($"                {idConst} => MetaContext!.PatchWrapper != null");
                        sb.AppendLine($"                    ? {service.InterfaceName}Dispatcher.Dispatch(Get{baseName}PatchTracked(), methodId, payload, Context.Serializer)");
                        sb.AppendLine($"                    : {service.InterfaceName}Dispatcher.Dispatch(Get{baseName}(), methodId, payload, Context.Serializer),");
                    }
                    else
                    {
                        sb.AppendLine($"                {idConst} => {service.InterfaceName}Dispatcher.Dispatch(Get{baseName}(), methodId, payload, Context.Serializer),");
                    }
                }
            }
            sb.AppendLine("                _ => throw new InvalidOperationException($\"Unknown method id: {methodId}\")");
            sb.AppendLine("            };");
            sb.AppendLine("        }");

            // Override DispatchEvent if there are subscriber interfaces. 0.24.0+: dispatch
            // by framework subscriber methodId (FrameworkMethodIds.*) rather than the
            // legacy (subscriberInterface, methodName) string pair. We fan out to every
            // service-side SubscriberDispatcher; each one's switch only matches the ids
            // belonging to interfaces it implements, so non-matching ones return null and
            // exit cheaply. Service count per state is small in practice.
            if (servicesWithSubscribers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        protected override System.Threading.Tasks.ValueTask<DispatchResult> DispatchEvent(ushort methodId, System.ReadOnlyMemory<byte> eventData)");
                sb.AppendLine("        {");
                foreach (var service in servicesWithSubscribers)
                {
                    var baseName = GetBaseName(service.InterfaceName);
                    sb.AppendLine($"            global::{service.Namespace}.Server.{baseName}SubscriberDispatcher.Dispatch(Get{baseName}(), methodId, eventData, Context.Serializer);");
                }
                sb.AppendLine("            return new System.Threading.Tasks.ValueTask<DispatchResult>(new DispatchResult());");
                sb.AppendLine("        }");
            }

            sb.AppendLine();

            // Access policy override
            var maxPolicy = services.Select(s => s.AccessPolicy).DefaultIfEmpty(0).Max();
            if (maxPolicy > 0)
            {
                var policyName = maxPolicy switch { 3 => "UserOwned", 2 => "OwnerOnly", 1 => "Authorized", _ => "Open" };
                sb.AppendLine($"        public override SharedMeta.Core.EntityAccessPolicy AccessPolicy => SharedMeta.Core.EntityAccessPolicy.{policyName};");
                sb.AppendLine();

                // For Authorized: generate CheckAccessAsync that calls service.IsAuthorized(playerId)
                if (maxPolicy == 1) // Authorized
                {
                    var authService = services.FirstOrDefault(s => s.HasIsAuthorizedMethod);
                    if (authService != null)
                    {
                        var authBaseName = GetBaseName(authService.InterfaceName);
                        sb.AppendLine("        public override System.Threading.Tasks.Task<bool> CheckAccessAsync(string playerId)");
                        sb.AppendLine("        {");
                        sb.AppendLine("            SharedMeta.Core.MetaContextAccessor.Current = MetaContext;");
                        sb.AppendLine("            try");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                return System.Threading.Tasks.Task.FromResult((({authService.ImplClassFullName})Get{authBaseName}()).IsAuthorized(playerId));");
                        sb.AppendLine("            }");
                        sb.AppendLine("            finally");
                        sb.AppendLine("            {");
                        sb.AppendLine("                SharedMeta.Core.MetaContextAccessor.Current = null;");
                        sb.AppendLine("            }");
                        sb.AppendLine("        }");
                        sb.AppendLine();
                    }
                }
            }

            // CreatePatchWrapper override for ServerPatch mode
            sb.AppendLine();
            sb.AppendLine("        protected override object? CreatePatchWrapper(SharedMeta.Core.Patch.PatchNode root)");
            sb.AppendLine("        {");
            sb.AppendLine($"            return new {stateTypeFullName}PatchWrapper(State, root, Context.Serializer);");
            sb.AppendLine("        }");
            sb.AppendLine();

            // 0.20.0: validation lives where it's natural to live.
            //   * GenerateClientApi=false  → inline `if (context.IsClientCall) throw ...` in
            //                                 each affected method's case in the generated
            //                                 service Dispatcher / SignalDispatcher.
            //   * is-query / is-signal     → inline switch in the HandleQueryAsync /
            //                                 HandleSignalAsync override below — overrides are
            //                                 emitted only when there is at least one method
            //                                 of that kind to register.
            //   * AccessPolicy ladder      → ditto, only when AccessPolicy != Open.
            // Projects with no Query / no Signal / no GenerateClientApi=false methods
            // generate zero validation code for those gates.
            var allQueryMethods = services
                .SelectMany(s => s.MethodSignatures)
                .Where(m => m.IsQuery)
                .ToList();
            var openAccessMethods = allQueryMethods.Where(m => m.IsOpenAccess).ToList();
            var allSignalMethods = services
                .SelectMany(s => s.MethodSignatures.Select(m => new { Impl = s, Sig = m }))
                .Where(x => x.Sig.IsSignal)
                .ToList();

            // 0.24.0+ GameMethodIds lives at {assemblyRoot}.Generated.GameMethodIds. Each
            // [MetaService] assembly has its own table; here we pick the first service's
            // namespace as the assembly-shared root (mirrors the GameServiceDiscoveryGenerator
            // convention). The is-query / is-signal switches below qualify ids by this prefix.
            var idsNs = services.FirstOrDefault()?.Namespace ?? "";

            // HandleQueryAsync override — emitted only when at least one query method exists.
            // Inline-validates: (1) is-a-query-method, (2) access-policy ladder (only when
            // AccessPolicy != Open). The GenerateClientApi=false gate per query method lives
            // inside the per-case dispatcher switch and is reached after delegation to base.
            if (allQueryMethods.Count > 0)
            {
                sb.AppendLine("        public override async System.Threading.Tasks.ValueTask<QueryCallResponse> HandleQueryAsync(RpcCall call)");
                sb.AppendLine("        {");
                sb.AppendLine("            bool isQuery = call.MethodId switch");
                sb.AppendLine("            {");
                foreach (var m in allQueryMethods)
                    sb.AppendLine($"                global::{idsNs}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(m.ServiceName, m.MethodAlias, m.Version)} => true,");
                sb.AppendLine("                _ => false");
                sb.AppendLine("            };");
                sb.AppendLine("            if (!isQuery)");
                sb.AppendLine("                return new QueryCallResponse { Error = $\"Method id {call.MethodId} is not a query method\" };");
                sb.AppendLine();

                if (maxPolicy > 0)
                {
                    if (openAccessMethods.Count > 0)
                    {
                        sb.AppendLine("            bool isOpenAccess = call.MethodId switch");
                        sb.AppendLine("            {");
                        foreach (var m in openAccessMethods)
                            sb.AppendLine($"                global::{idsNs}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(m.ServiceName, m.MethodAlias, m.Version)} => true,");
                        sb.AppendLine("                _ => false");
                        sb.AppendLine("            };");
                        sb.AppendLine("            if (!isOpenAccess && AccessPolicy != SharedMeta.Core.EntityAccessPolicy.Open)");
                    }
                    else
                    {
                        sb.AppendLine("            if (AccessPolicy != SharedMeta.Core.EntityAccessPolicy.Open)");
                    }
                    sb.AppendLine("            {");
                    sb.AppendLine("                bool allowed;");
                    sb.AppendLine("                if (AccessPolicy is SharedMeta.Core.EntityAccessPolicy.OwnerOnly or SharedMeta.Core.EntityAccessPolicy.UserOwned)");
                    sb.AppendLine("                    allowed = MetaContext!.EntityId == call.CallerId;");
                    sb.AppendLine("                else");
                    sb.AppendLine("                    allowed = await CheckAccessAsync(call.CallerId ?? \"\");");
                    sb.AppendLine("                if (!allowed)");
                    sb.AppendLine("                    return new QueryCallResponse { Error = $\"Access denied for query on entity '{MetaContext!.EntityId}'\" };");
                    sb.AppendLine("            }");
                    sb.AppendLine();
                }

                sb.AppendLine("            return await base.HandleQueryAsync(call);");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // HandleSignalAsync override — emitted only when at least one signal method exists.
            // Inline-validates: (1) is-a-signal-method, (2) access-policy ladder (only when
            // AccessPolicy != Open). The GenerateClientApi=false gate per signal method lives
            // inside the per-case SignalDispatcher switch and is reached after delegation to base.
            if (allSignalMethods.Count > 0)
            {
                sb.AppendLine("        public override async System.Threading.Tasks.ValueTask HandleSignalAsync(RpcCall call)");
                sb.AppendLine("        {");
                sb.AppendLine("            bool isSignal = call.MethodId switch");
                sb.AppendLine("            {");
                foreach (var x in allSignalMethods)
                    sb.AppendLine($"                global::{idsNs}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(x.Sig.ServiceName, x.Sig.MethodAlias, x.Sig.Version)} => true,");
                sb.AppendLine("                _ => false");
                sb.AppendLine("            };");
                sb.AppendLine("            if (!isSignal)");
                sb.AppendLine("            {");
                sb.AppendLine("                LogProviderCallError(new System.InvalidOperationException($\"Method id {call.MethodId} is not a signal method\"), call.MethodId);");
                sb.AppendLine("                return;");
                sb.AppendLine("            }");
                sb.AppendLine();

                if (maxPolicy > 0)
                {
                    sb.AppendLine("            if (AccessPolicy != SharedMeta.Core.EntityAccessPolicy.Open)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                bool allowed;");
                    sb.AppendLine("                if (AccessPolicy is SharedMeta.Core.EntityAccessPolicy.OwnerOnly or SharedMeta.Core.EntityAccessPolicy.UserOwned)");
                    sb.AppendLine("                    allowed = MetaContext!.EntityId == call.CallerId;");
                    sb.AppendLine("                else");
                    sb.AppendLine("                    allowed = await CheckAccessAsync(call.CallerId ?? \"\");");
                    sb.AppendLine("                if (!allowed)");
                    sb.AppendLine("                {");
                    sb.AppendLine("                    LogProviderCallError(new System.UnauthorizedAccessException($\"Access denied for signal id {call.MethodId} from caller '{call.CallerId}' on entity '{MetaContext!.EntityId}'\"), call.MethodId);");
                    sb.AppendLine("                    return;");
                    sb.AppendLine("                }");
                    sb.AppendLine("            }");
                    sb.AppendLine();
                }

                sb.AppendLine("            await base.HandleSignalAsync(call);");
                sb.AppendLine("        }");
                sb.AppendLine();

                // 0.24.0+ DispatchSignal is keyed by ushort methodId. Every signal method id
                // routes to its owning service's SignalDispatcher (which itself switches on
                // methodId). Two nested O(1) switches — no string-name plumbing on the hot path.
                sb.AppendLine("        protected override async Task DispatchSignal(ushort methodId, System.ReadOnlyMemory<byte> payload)");
                sb.AppendLine("        {");
                sb.AppendLine("            switch (methodId)");
                sb.AppendLine("            {");
                foreach (var x in allSignalMethods)
                {
                    var baseName = GetBaseName(x.Impl.InterfaceName);
                    var idConst = "global::" + idsNs + ".Generated.GameMethodIds." +
                        SignatureHashGenerator.MakeMethodIdConstName(x.Sig.ServiceName, x.Sig.MethodAlias, x.Sig.Version);
                    sb.AppendLine($"                case {idConst}:");
                    sb.AppendLine($"                    await {x.Impl.InterfaceName}SignalDispatcher.Dispatch(Get{baseName}(), methodId, payload, Context.Serializer);");
                    sb.AppendLine("                    break;");
                }
                sb.AppendLine("                default: throw new MissingMethodException($\"Signal methodId {methodId} not found\");");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // InitializeStateAsync / migration override (if any service has [MetaInit])
            var servicesWithInit = services.Where(s => s.MetaInitMethodName != null).ToList();
            if (servicesWithInit.Count > 0)
            {
                // Group migration conditions by target state schema version (AND semantics per group).
                var migStepGroups = allMigConds
                    .GroupBy(c => c.StateVersion)
                    .OrderBy(g => g.Key)
                    .ToList();

                bool hasMigration = migStepGroups.Count > 0;

                sb.AppendLine("        protected override async System.Threading.Tasks.Task<int> RunInitAsync(int currentVersion)");
                sb.AppendLine("        {");
                sb.AppendLine("            var maxVersion = currentVersion;");

                if (hasMigration)
                {
                    // Schema 1 is "explicit" when the user declared [MetaStateVersion(1, …)] —
                    // meaning they want schema 1 gated by its own config threshold. If no such
                    // entry exists, schema 1 is "implicit base init" and we emit Step 0 to run
                    // it unconditionally for fresh entities.
                    bool hasExplicitSchemaOne = migStepGroups.Any(g => g.Key == 1);

                    // Step 0 — base init for primary-config services. Always runs for fresh
                    // entities (currentVersion < 1) regardless of which migration steps fire.
                    // Pinned to (1, 0, 0) so the user's base init has a well-defined config
                    // branch (the "1.x" baseline). This decouples first-time init from the
                    // schema-2+ migration steps gated by client-resolved config.
                    var primaryServicesWithInit = servicesWithInit
                        .Where(s => s.ConfigTypeFullName == null || s.ConfigTypeFullName == configType)
                        .ToList();
                    // 0.33.0+ services whose [MetaInit] is tied ONLY to [ServiceConfig] entries
                    // (no legacy primary at all) also need a Step 0 base-init branch when schema 1
                    // isn't explicitly gated — same "give it a well-defined 1.0 baseline" reasoning
                    // as the primary case, just targeting Context.Configs[i] for each declared
                    // entry instead of the legacy Context.Config.
                    var serviceConfigServicesWithInit = servicesWithInit
                        .Where(s => s.ConfigTypeFullName == null && s.ServiceConfigTypeFullNames.Count > 0)
                        .ToList();
                    bool step0HasPrimaryWork = primaryServicesWithInit.Count > 0 && configType != null;
                    bool step0HasServiceConfigWork = serviceConfigServicesWithInit.Count > 0 && serviceConfigTypes.Count > 0;
                    if (!hasExplicitSchemaOne && (step0HasPrimaryWork || step0HasServiceConfigWork))
                    {
                        var guard = step0HasPrimaryWork ? "currentVersion < 1 && 1 <= _migrationCap && _configProvider != null" : "currentVersion < 1 && 1 <= _migrationCap";
                        sb.AppendLine($"            if ({guard})");
                        sb.AppendLine("            {");
                        sb.AppendLine("                var _savedConfig0 = MetaContext!.Config;");
                        sb.AppendLine("                var _savedConfigs0 = MetaContext!.Configs;");
                        sb.AppendLine("                var _savedConfigVersions0 = MetaContext!.ConfigVersions;");
                        sb.AppendLine("                try");
                        sb.AppendLine("                {");
                        if (step0HasPrimaryWork)
                        {
                            sb.AppendLine("                    MetaContext!.Config = _configProvider!.GetConfig(new SharedMeta.Core.MetaConfigVersion(1, 0, 0));");
                            sb.AppendLine("                    MetaContext!.ConfigVersion = new SharedMeta.Core.MetaConfigVersion(1, 0, 0);");
                        }
                        if (step0HasServiceConfigWork)
                        {
                            sb.AppendLine($"                    var _step0Configs = new object[{serviceConfigTypes.Count}];");
                            sb.AppendLine($"                    var _step0Versions = new SharedMeta.Core.MetaConfigVersion[{serviceConfigTypes.Count}];");
                            for (int i = 0; i < serviceConfigTypes.Count; i++)
                            {
                                var sc = serviceConfigTypes[i];
                                sb.AppendLine($"                    if (_serviceConfigProvider_{sc.Ident} != null) {{ _step0Configs[{i}] = _serviceConfigProvider_{sc.Ident}.GetConfig(new SharedMeta.Core.MetaConfigVersion(1, 0, 0)); _step0Versions[{i}] = new SharedMeta.Core.MetaConfigVersion(1, 0, 0); }}");
                            }
                            sb.AppendLine("                    MetaContext!.Configs = _step0Configs;");
                            sb.AppendLine("                    MetaContext!.ConfigVersions = _step0Versions;");
                        }
                        sb.AppendLine("                    MetaContext!.Version = currentVersion;");
                        foreach (var svc in primaryServicesWithInit.Concat(serviceConfigServicesWithInit))
                        {
                            var baseName = GetBaseName(svc.InterfaceName);
                            var initArgs = svc.MetaInitParameterCount == 2 ? "currentVersion, 1" : "currentVersion";
                            sb.AppendLine($"                    maxVersion = System.Math.Max(maxVersion, await (({svc.ImplClassFullName})Get{baseName}()).{svc.MetaInitMethodName}({initArgs}));");
                        }
                        sb.AppendLine("                }");
                        sb.AppendLine("                finally { MetaContext!.Config = _savedConfig0; MetaContext!.Configs = _savedConfigs0; MetaContext!.ConfigVersions = _savedConfigVersions0; }");
                        sb.AppendLine("                currentVersion = maxVersion;");
                        sb.AppendLine("            }");
                    }

                    // Migration-aware path: for each schema step above currentVersion, check AND
                    // conditions and call each service's [MetaInit] with the transition config.
                    foreach (var group in migStepGroups)
                    {
                        var targetSchema = group.Key;
                        var conds = group.ToList();

                        // Build null-guard: all providers referenced in this group must be non-null.
                        var providerGuards = new List<string>();
                        if (conds.Any(c => c.ConfigTypeFullName == null || c.ConfigTypeFullName == configType))
                            providerGuards.Add("_configProvider != null");
                        foreach (var sec in conds
                            .Where(c => c.ConfigTypeFullName != null && c.ConfigTypeFullName != configType)
                            .Select(c => $"_configProvider_{c.ConfigTypeIdent}")
                            .Distinct())
                            providerGuards.Add($"{sec} != null");

                        // _migrationCap honours [MinStateVersion(N)] on the dispatched method.
                        // int.MaxValue (default) means uncapped, so the && comparison is a no-op.
                        var capGuard = $"{targetSchema} <= _migrationCap";
                        var guardExpr = providerGuards.Count > 0
                            ? $"currentVersion < {targetSchema} && {capGuard} && {string.Join(" && ", providerGuards)}"
                            : $"currentVersion < {targetSchema} && {capGuard}";

                        sb.AppendLine($"            if ({guardExpr})");
                        sb.AppendLine("            {");

                        // 0.21.0: inner step conditions are no longer evaluated here against
                        // provider.CurrentVersion. _migrationCap (set by CheckAndRunLazyMigrationAsync
                        // from ComputeRequiredStateSchema(MigrationClientVersion)) already gates
                        // each step by the caller's resolved config — running the same AND-check
                        // twice was redundant. The outer guard `targetSchema <= _migrationCap`
                        // is the only gate; if we reach here, all AND-conditions are satisfied.
                        // 0.33.0+ also save/restore Configs/ConfigVersions — a condition whose type
                        // is a [ServiceConfig] entry (rather than the legacy primary) needs its
                        // transition version written into Context.Configs[i], since that's what the
                        // generated per-entry accessor reads, not the legacy Context.Config.
                        sb.AppendLine("                var _savedConfig = MetaContext!.Config;");
                        sb.AppendLine("                var _savedConfigs = MetaContext!.Configs;");
                        sb.AppendLine("                var _savedConfigVersions = MetaContext!.ConfigVersions;");
                        sb.AppendLine("                try");
                        sb.AppendLine("                {");

                        // For each condition: find the service whose config type matches, set config, call [MetaInit].
                        foreach (var cond in conds)
                        {
                            bool isPrimary = cond.ConfigTypeFullName == null || cond.ConfigTypeFullName == configType;
                            var provField = isPrimary ? "_configProvider" : $"_configProvider_{cond.ConfigTypeIdent}";
                            int scIndex = isPrimary ? -1 : serviceConfigTypes.FindIndex(sc => sc.ConfigType == cond.ConfigTypeFullName);
                            // Find matching service: legacy ConfigType match (primary/secondary), OR
                            // a service that declares this condition's type via [ServiceConfig].
                            var matchingSvc = servicesWithInit.FirstOrDefault(s =>
                                (cond.ConfigTypeFullName == null && (s.ConfigTypeFullName == null || s.ConfigTypeFullName == configType))
                                || s.ConfigTypeFullName == cond.ConfigTypeFullName
                                || (cond.ConfigTypeFullName != null && s.ServiceConfigTypeFullNames.Contains(cond.ConfigTypeFullName)));
                            if (matchingSvc == null) continue;

                            var baseName = GetBaseName(matchingSvc.InterfaceName);
                            var initArgs = matchingSvc.MetaInitParameterCount == 2
                                ? $"currentVersion, {targetSchema}"
                                : "currentVersion";
                            sb.AppendLine($"                    MetaContext!.Config = {provField}!.GetConfig(new SharedMeta.Core.MetaConfigVersion({cond.Major}, {cond.Minor}, 0));");
                            sb.AppendLine($"                    MetaContext!.ConfigVersion = new SharedMeta.Core.MetaConfigVersion({cond.Major}, {cond.Minor}, 0);");
                            if (scIndex >= 0)
                            {
                                sb.AppendLine($"                    {{ var _stepConfigs = new object[{serviceConfigTypes.Count}]; var _priorConfigs = MetaContext!.Configs; if (_priorConfigs != null) for (int _i = 0; _i < _priorConfigs.Count && _i < {serviceConfigTypes.Count}; _i++) _stepConfigs[_i] = _priorConfigs[_i]; _stepConfigs[{scIndex}] = {provField}!.GetConfig(new SharedMeta.Core.MetaConfigVersion({cond.Major}, {cond.Minor}, 0)); MetaContext!.Configs = _stepConfigs; }}");
                                sb.AppendLine($"                    {{ var _stepVersions = new SharedMeta.Core.MetaConfigVersion[{serviceConfigTypes.Count}]; var _priorVersions = MetaContext!.ConfigVersions; if (_priorVersions != null) for (int _i = 0; _i < _priorVersions.Count && _i < {serviceConfigTypes.Count}; _i++) _stepVersions[_i] = _priorVersions[_i]; _stepVersions[{scIndex}] = new SharedMeta.Core.MetaConfigVersion({cond.Major}, {cond.Minor}, 0); MetaContext!.ConfigVersions = _stepVersions; }}");
                            }
                            sb.AppendLine($"                    MetaContext!.Version = currentVersion;");
                            sb.AppendLine($"                    maxVersion = System.Math.Max(maxVersion, await (({matchingSvc.ImplClassFullName})Get{baseName}()).{matchingSvc.MetaInitMethodName}({initArgs}));");
                        }

                        sb.AppendLine("                }");
                        sb.AppendLine("                finally { MetaContext!.Config = _savedConfig; MetaContext!.Configs = _savedConfigs; MetaContext!.ConfigVersions = _savedConfigVersions; }");
                        sb.AppendLine("            }");

                        // After successful step, currentVersion tracks progress for next step guard.
                        sb.AppendLine("            currentVersion = maxVersion;");
                    }

                    // Call [MetaInit] for services whose config type doesn't appear in any migration
                    // condition — backward-compatible path (they use version-counter logic internally).
                    // 0.33.0+ also exclude services already handled via a [ServiceConfig]-linked
                    // condition above (checked via ServiceConfigTypeFullNames) — otherwise a
                    // ServiceConfig-only service (no legacy ConfigTypeFullName) whose type IS
                    // covered by a migration condition would double-run [MetaInit].
                    var handledConfigTypes = new HashSet<string?>(allMigConds.Select(c => c.ConfigTypeFullName));
                    var uncoveredServices = servicesWithInit
                        .Where(s => !handledConfigTypes.Contains(s.ConfigTypeFullName)
                                 && !handledConfigTypes.Contains(null)
                                 && !s.ServiceConfigTypeFullNames.Any(t => handledConfigTypes.Contains(t)))
                        .ToList();
                    foreach (var svc in uncoveredServices)
                    {
                        var baseName = GetBaseName(svc.InterfaceName);
                        // Uncovered services have no [MetaStateVersion] entries → target defaults to 1.
                        var initArgs = svc.MetaInitParameterCount == 2 ? "currentVersion, 1" : "currentVersion";
                        sb.AppendLine($"            MetaContext!.Version = currentVersion;");
                        sb.AppendLine($"            maxVersion = System.Math.Max(maxVersion, await (({svc.ImplClassFullName})Get{baseName}()).{svc.MetaInitMethodName}({initArgs}));");
                    }
                }
                else
                {
                    // No migration conditions — simple legacy path (version-counter only).
                    foreach (var service in servicesWithInit)
                    {
                        var baseName = GetBaseName(service.InterfaceName);
                        // No [MetaStateVersion] declared → target = 1 for two-arg form.
                        var initArgs = service.MetaInitParameterCount == 2 ? "currentVersion, 1" : "currentVersion";
                        sb.AppendLine($"            MetaContext!.Version = currentVersion;");
                        sb.AppendLine($"            maxVersion = System.Math.Max(maxVersion, await (({service.ImplClassFullName})Get{baseName}()).{service.MetaInitMethodName}({initArgs}));");
                    }
                }

                sb.AppendLine("            return maxVersion;");
                sb.AppendLine("        }");
                sb.AppendLine();

                // Emit ComputeRequiredStateSchema + CheckAndRunLazyMigrationAsync only when
                // migration conditions are declared — otherwise the base-class no-op suffices.
                if (hasMigration)
                {
                    // ComputeRequiredStateSchema — generated lookup. 0.21.0: each AND-condition
                    // resolves the relevant config version from the *caller's* client version via
                    // ResolveForClient (per-config [MetaConfigVersion] rules), not from a server-
                    // wide CurrentVersion. callerClientVersion comes from the call's
                    // RpcCall.CallerClientVersion (or from SubscribeRequest.ClientVersion at
                    // Subscribe time). Null/empty → no migration available (returns 0).
                    sb.AppendLine("        private int ComputeRequiredStateSchema(string? callerClientVersion)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (string.IsNullOrEmpty(callerClientVersion)) return 0;");
                    sb.AppendLine("            int required = 0;");
                    foreach (var group in migStepGroups)
                    {
                        var targetSchema = group.Key;
                        var conds = group.ToList();

                        var nullGuards = new List<string>();
                        if (conds.Any(c => c.ConfigTypeFullName == null || c.ConfigTypeFullName == configType))
                            nullGuards.Add("_configProvider != null");
                        foreach (var ident in conds
                            .Where(c => c.ConfigTypeFullName != null && c.ConfigTypeFullName != configType)
                            .Select(c => $"_configProvider_{c.ConfigTypeIdent}").Distinct())
                            nullGuards.Add($"{ident} != null");

                        var nullGuardExpr = nullGuards.Count > 0
                            ? "if (" + string.Join(" && ", nullGuards) + ")"
                            : "";

                        if (nullGuardExpr.Length > 0) sb.AppendLine($"            {nullGuardExpr}");
                        sb.AppendLine("            {");

                        for (int ci = 0; ci < conds.Count; ci++)
                        {
                            var cond = conds[ci];
                            bool isPrimary = cond.ConfigTypeFullName == null || cond.ConfigTypeFullName == configType;
                            var provField = isPrimary ? "_configProvider" : $"_configProvider_{cond.ConfigTypeIdent}";
                            // Primary configs reuse the cached _configVersionPolicyResolver; secondary
                            // configs let ResolveForClient pick up rules from their own TConfig via
                            // MetaConfigVersionResolver.ForType (called once inside ResolveForClient).
                            var resolverArg = isPrimary ? "_configVersionPolicyResolver" : "null";
                            sb.AppendLine($"                var _rcv{ci} = {provField}!.ResolveForClient(callerClientVersion, {resolverArg});");
                            sb.AppendLine($"                bool _rok{ci} = _rcv{ci}.Major > {cond.Major} || (_rcv{ci}.Major == {cond.Major} && _rcv{ci}.Minor >= {cond.Minor});");
                        }

                        var allOk = string.Join(" && ", Enumerable.Range(0, conds.Count).Select(i => $"_rok{i}"));
                        sb.AppendLine($"                if ({allOk}) required = System.Math.Max(required, {targetSchema});");
                        sb.AppendLine("            }");
                    }
                    sb.AppendLine("            return required;");
                    sb.AppendLine("        }");
                    sb.AppendLine();

                    // _migrationCap is honoured by RunInitAsync's per-step guards. int.MaxValue
                    // means uncapped (default). [MinStateVersion(N)] sets it to N for one call.
                    sb.AppendLine("        private int _migrationCap = int.MaxValue;");
                    sb.AppendLine();

                    // CheckAndRunLazyMigrationAsync — 0.21.0: callerClientVersion drives both
                    // ComputeRequiredStateSchema (which AND-conditions are satisfied for this
                    // client) and the per-step config resolves used by RunInitAsync's
                    // ResolveForClient calls (via the MigrationClientVersion field set below).
                    sb.AppendLine("        protected override async System.Threading.Tasks.Task<bool> CheckAndRunLazyMigrationAsync(string? callerClientVersion, int? schemaCap = null)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            var required = ComputeRequiredStateSchema(callerClientVersion);");
                    sb.AppendLine("            if (schemaCap.HasValue) required = System.Math.Min(required, schemaCap.Value);");
                    // Fresh-entity floor: schema 1 base init runs unconditionally on first
                    // interaction ONLY when there's no explicit [MetaStateVersion(1, …)] gate.
                    // When schema 1 is gated, the user wants config to control whether base
                    // init runs at all — respect their declaration.
                    if (!migStepGroups.Any(g => g.Key == 1))
                    {
                        sb.AppendLine("            if (CurrentStateSchemaVersion == 0 && (!schemaCap.HasValue || schemaCap.Value >= 1))");
                        sb.AppendLine("                required = System.Math.Max(required, 1);");
                    }
                    sb.AppendLine("            if (required <= CurrentStateSchemaVersion) return false;");
                    sb.AppendLine("            var before = CurrentStateSchemaVersion;");
                    sb.AppendLine("            var prevCap = _migrationCap;");
                    sb.AppendLine("            var prevMigClient = MigrationClientVersion;");
                    sb.AppendLine("            // 0.21.0: cap migration at the AND-gate-adjusted `required`, not the outer");
                    sb.AppendLine("            // method/client schemaCap. Otherwise a multi-config [MetaStateVersion] step");
                    sb.AppendLine("            // whose primary config satisfies the threshold but secondary doesn't would");
                    sb.AppendLine("            // still run (the per-step outer guard only checks _migrationCap, not the");
                    sb.AppendLine("            // AND-conditions resolved against caller's client version).");
                    sb.AppendLine("            _migrationCap = required;");
                    sb.AppendLine("            MigrationClientVersion = callerClientVersion;");
                    sb.AppendLine("            try");
                    sb.AppendLine("            {");
                    sb.AppendLine("                await InitializeStateAsync(CurrentStateSchemaVersion);");
                    sb.AppendLine("            }");
                    sb.AppendLine("            finally { _migrationCap = prevCap; MigrationClientVersion = prevMigClient; }");
                    sb.AppendLine("            if (CurrentStateSchemaVersion > before)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                LazyMigrationNewVersion = CurrentStateSchemaVersion;");
                    sb.AppendLine("                LazyMigrationCompleted = true;");
                    sb.AppendLine("                return true;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            return false;");
                    sb.AppendLine("        }");
                    sb.AppendLine();

                    // IsClientConfigCompatible — per-entity gate: checks that the client's
                    // resolved config version covers the entity's current state schema.
                    // 0.22.0: only Breaking = true schema steps reject. Non-breaking schemas
                    // allow old clients to subscribe (MemoryPack VersionTolerant tolerates
                    // additive fields). Reason: most schema bumps are additive; explicit
                    // Breaking opt-in surfaces real structural breaks as user-actionable
                    // "update required" notifications instead of silent fail-loud on every bump.
                    // 0.33.0+ callerClientVersion lets non-primary conditions (whether a plain
                    // secondary or a [ServiceConfig]-linked type) resolve and check their OWN
                    // version independently — the AND-gate now covers every declared type, not
                    // just the primary.
                    sb.AppendLine("        public override bool IsClientConfigCompatible(SharedMeta.Core.MetaConfigVersion clientConfigVersion, string? callerClientVersion)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            int schema = CurrentStateSchemaVersion;");
                    sb.AppendLine("            if (schema <= 0) return true;");

                    foreach (var group in migStepGroups)
                    {
                        var targetSchema = group.Key;
                        var conds = group.ToList();
                        // 0.22.0: gate this schema step only when ANY of its conditions is Breaking.
                        // If all conditions in the group are non-breaking, the schema bump is
                        // backward-compatible — emit nothing for that step.
                        if (!conds.Any(c => c.Breaking)) continue;

                        sb.AppendLine($"            // Schema {targetSchema} gate — BREAKING; rejects old clients");
                        sb.AppendLine($"            if (schema >= {targetSchema})");
                        sb.AppendLine("            {");
                        for (int ci = 0; ci < conds.Count; ci++)
                        {
                            var cond = conds[ci];
                            bool isPrimary = cond.ConfigTypeFullName == null || cond.ConfigTypeFullName == configType;
                            if (isPrimary)
                            {
                                sb.AppendLine($"                bool _gc{ci} = clientConfigVersion.Major > {cond.Major} || (clientConfigVersion.Major == {cond.Major} && clientConfigVersion.Minor >= {cond.Minor});");
                                sb.AppendLine($"                if (!_gc{ci}) return false;");
                            }
                            else
                            {
                                sb.AppendLine($"                if (_configProvider_{cond.ConfigTypeIdent} != null)");
                                sb.AppendLine("                {");
                                sb.AppendLine($"                    var _rc{ci} = _configProvider_{cond.ConfigTypeIdent}.ResolveForClient(callerClientVersion, null);");
                                sb.AppendLine($"                    bool _gc{ci} = _rc{ci}.Major > {cond.Major} || (_rc{ci}.Major == {cond.Major} && _rc{ci}.Minor >= {cond.Minor});");
                                sb.AppendLine($"                    if (!_gc{ci}) return false;");
                                sb.AppendLine("                }");
                            }
                        }
                        sb.AppendLine("            }");
                    }

                    sb.AppendLine("            return true;");
                    sb.AppendLine("        }");
                    sb.AppendLine();

                    // ComputeSchemaCapForClient — caps init/migration to what the connecting
                    // client's resolved config branch supports. Used by EntityGrain.SubscribeAsync
                    // and MetaProviderBase.HandleCallAsync to prevent premature migration
                    // (a 1.x client triggering a 2.0 schema jump on a fresh entity).
                    // 0.33.0+ every declared type's threshold must be met for the cap to advance
                    // to that schema — not just the primary's (mirrors ComputeRequiredStateSchema's
                    // AND-gate, which already did this).
                    // Cap baseline: 1 when schema 1 is implicit base init (no explicit
                    // [MetaStateVersion(1, …)]); 0 when schema 1 is gated, because in that case
                    // running base init below the gate would contradict the user's declaration.
                    bool hasExplicitSchemaOneCap = migStepGroups.Any(g => g.Key == 1);
                    int capBaseline = hasExplicitSchemaOneCap ? 0 : 1;

                    sb.AppendLine("        public override int? ComputeSchemaCapForClient(string? clientVersion)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            int cap = {capBaseline};");
                    if (configType != null)
                        sb.AppendLine("            var resolved = _configProvider != null ? ResolveClientConfigVersion(clientVersion) : default;");
                    int capGroupIdx = 0;
                    foreach (var grp in migStepGroups)
                    {
                        var conds = grp.ToList();
                        var checkParts = new List<string>();
                        sb.AppendLine("            {");
                        for (int ci = 0; ci < conds.Count; ci++)
                        {
                            var cond = conds[ci];
                            bool isPrimary = cond.ConfigTypeFullName == null || cond.ConfigTypeFullName == configType;
                            var flag = $"_cap{capGroupIdx}_{ci}";
                            if (isPrimary)
                            {
                                sb.AppendLine($"                bool {flag} = _configProvider != null && (resolved.Major > {cond.Major} || (resolved.Major == {cond.Major} && resolved.Minor >= {cond.Minor}));");
                            }
                            else
                            {
                                sb.AppendLine($"                bool {flag} = false;");
                                sb.AppendLine($"                if (_configProvider_{cond.ConfigTypeIdent} != null)");
                                sb.AppendLine("                {");
                                sb.AppendLine($"                    var _crv{capGroupIdx}_{ci} = _configProvider_{cond.ConfigTypeIdent}.ResolveForClient(clientVersion, null);");
                                sb.AppendLine($"                    {flag} = _crv{capGroupIdx}_{ci}.Major > {cond.Major} || (_crv{capGroupIdx}_{ci}.Major == {cond.Major} && _crv{capGroupIdx}_{ci}.Minor >= {cond.Minor});");
                                sb.AppendLine("                }");
                            }
                            checkParts.Add(flag);
                        }
                        var combined = string.Join(" && ", checkParts);
                        sb.AppendLine($"                if ({combined}) cap = System.Math.Max(cap, {grp.Key});");
                        sb.AppendLine("            }");
                        capGroupIdx++;
                    }
                    sb.AppendLine("            return cap;");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
                else
                {
                    // Non-migration path: services have [MetaInit] but the state has no
                    // [MetaStateVersion] declarations. Emit a minimal CheckAndRunLazyMigrationAsync
                    // so SubscribeAsync/HandleCallAsync still trigger first-time init for fresh
                    // entities (since OnActivateAsync no longer drives it). 0.21.0 signature
                    // includes callerClientVersion for consistency, though it's unused here
                    // (no per-step config resolves without [MetaStateVersion] declarations).
                    sb.AppendLine("        protected override async System.Threading.Tasks.Task<bool> CheckAndRunLazyMigrationAsync(string? callerClientVersion, int? schemaCap = null)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (CurrentStateSchemaVersion > 0) return false;");
                    sb.AppendLine("            if (schemaCap.HasValue && schemaCap.Value < 1) return false;");
                    sb.AppendLine("            var before = CurrentStateSchemaVersion;");
                    sb.AppendLine("            var prevMigClient = MigrationClientVersion;");
                    sb.AppendLine("            MigrationClientVersion = callerClientVersion;");
                    sb.AppendLine("            try { await InitializeStateAsync(CurrentStateSchemaVersion); }");
                    sb.AppendLine("            finally { MigrationClientVersion = prevMigClient; }");
                    sb.AppendLine("            if (CurrentStateSchemaVersion > before)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                LazyMigrationNewVersion = CurrentStateSchemaVersion;");
                    sb.AppendLine("                LazyMigrationCompleted = true;");
                    sb.AppendLine("                return true;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            return false;");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            // Per-method migration policy overrides — only emitted if any [MetaMethod] in this
            // provider's services carries [NoMigrate] or [MinStateVersion(N)].
            var allSkipMigrationMethods = services
                .SelectMany(s => s.MethodSignatures.Where(m => m.SkipMigration).Select(m => m))
                .ToList();
            var allMinStateMethods = services
                .SelectMany(s => s.MethodSignatures.Where(m => m.MinStateVersion.HasValue).Select(m => m))
                .ToList();

            // GameMethodIds lives in {rootNamespace}.Generated — same per-assembly root for
            // every service in this provider (they all compile into one assembly).
            var migrationIdsNs = services.FirstOrDefault()?.Namespace ?? "";

            if (allSkipMigrationMethods.Count > 0)
            {
                // 0.24.0+ Keyed by client-local MethodId. Generator emits GameMethodIds constants
                // per declared [NoMigrate] method — the provider's hot path consumes ushort
                // ids only, no name plumbing.
                sb.AppendLine("        protected override bool ShouldSkipMigration(ushort methodId)");
                sb.AppendLine("        {");
                sb.AppendLine("            return methodId switch");
                sb.AppendLine("            {");
                foreach (var m in allSkipMigrationMethods)
                    sb.AppendLine($"                global::{migrationIdsNs}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(m.ServiceName, m.MethodAlias, m.Version)} => true,");
                sb.AppendLine("                _ => false");
                sb.AppendLine("            };");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (allMinStateMethods.Count > 0)
            {
                sb.AppendLine("        protected override int? GetMethodMinStateVersion(ushort methodId)");
                sb.AppendLine("        {");
                sb.AppendLine("            return methodId switch");
                sb.AppendLine("            {");
                foreach (var m in allMinStateMethods)
                    sb.AppendLine($"                global::{migrationIdsNs}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(m.ServiceName, m.MethodAlias, m.Version)} => {m.MinStateVersion!.Value},");
                sb.AppendLine("                _ => null");
                sb.AppendLine("            };");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // GetSchemaFloorConfig / GetSchemaFloorConfigVersion — used when [NoMigrate] is in
            // play. Resolve to the lowest config version that satisfies all migration thresholds
            // for the current state schema (so the call sees the same config branch the entity
            // was last persisted under). Only emitted when both:
            //   1. The state declares [MetaStateVersion] migration breakpoints, AND
            //   2. At least one method on the provider's services carries [NoMigrate].
            // Single primary-config path only — secondary configs use their own provider's
            // CurrentVersion (no per-schema floor for now).
            if (allSkipMigrationMethods.Count > 0 && configType != null)
            {
                var primaryStepGroups = allMigConds
                    .Where(c => c.ConfigTypeFullName == null || c.ConfigTypeFullName == configType)
                    .GroupBy(c => c.StateVersion)
                    .OrderBy(g => g.Key)
                    .ToList();

                sb.AppendLine($"        protected override SharedMeta.Core.MetaConfigVersion GetSchemaFloorConfigVersion(int stateSchema)");
                sb.AppendLine("        {");
                if (primaryStepGroups.Count > 0)
                {
                    sb.AppendLine("            return stateSchema switch");
                    sb.AppendLine("            {");
                    foreach (var grp in primaryStepGroups)
                    {
                        // Threshold for schema N is the MinConfigVersion of any one of its conditions
                        // (AND group, but they target the same step — pick any primary one).
                        var primaryCond = grp.First();
                        sb.AppendLine($"                {grp.Key} => new SharedMeta.Core.MetaConfigVersion({primaryCond.Major}, {primaryCond.Minor}, 0),");
                    }
                    // For schemas below first threshold, use (1, 0, 0) — the legacy 1.x branch.
                    sb.AppendLine("                _ => new SharedMeta.Core.MetaConfigVersion(1, 0, 0)");
                    sb.AppendLine("            };");
                }
                else
                {
                    sb.AppendLine("            return new SharedMeta.Core.MetaConfigVersion(1, 0, 0);");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        protected override object? GetSchemaFloorConfig(int stateSchema)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_configProvider == null) return null;");
                sb.AppendLine("            return _configProvider.GetConfig(GetSchemaFloorConfigVersion(stateSchema));");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // 0.33.0+ Per-[ServiceConfig]-type equivalent of GetSchemaFloorConfig/Version above —
            // same "[NoMigrate] sees the schema-consistent branch" guarantee, but per declared
            // entry instead of just the legacy primary. An entry with no [MetaStateVersion]
            // condition referencing it falls back to (1,0,0), same default the primary uses.
            if (allSkipMigrationMethods.Count > 0 && serviceConfigTypes.Count > 0)
            {
                sb.AppendLine("        protected override SharedMeta.Core.MetaConfigVersion[] GetSchemaFloorServiceConfigVersionsForClient(int stateSchema)");
                sb.AppendLine("        {");
                sb.AppendLine($"            var _result = new SharedMeta.Core.MetaConfigVersion[{serviceConfigTypes.Count}];");
                for (int i = 0; i < serviceConfigTypes.Count; i++)
                {
                    var sc = serviceConfigTypes[i];
                    var typeStepGroups = allMigConds
                        .Where(c => c.ConfigTypeFullName == sc.ConfigType)
                        .GroupBy(c => c.StateVersion)
                        .OrderBy(g => g.Key)
                        .ToList();
                    if (typeStepGroups.Count > 0)
                    {
                        sb.AppendLine($"            _result[{i}] = stateSchema switch");
                        sb.AppendLine("            {");
                        foreach (var grp in typeStepGroups)
                        {
                            var cond = grp.First();
                            sb.AppendLine($"                {grp.Key} => new SharedMeta.Core.MetaConfigVersion({cond.Major}, {cond.Minor}, 0),");
                        }
                        sb.AppendLine("                _ => new SharedMeta.Core.MetaConfigVersion(1, 0, 0)");
                        sb.AppendLine("            };");
                    }
                    else
                    {
                        sb.AppendLine($"            _result[{i}] = new SharedMeta.Core.MetaConfigVersion(1, 0, 0);");
                    }
                }
                sb.AppendLine("            return _result;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine("        protected override object?[] GetSchemaFloorServiceConfigsForClient(int stateSchema)");
                sb.AppendLine("        {");
                sb.AppendLine("            var _versions = GetSchemaFloorServiceConfigVersionsForClient(stateSchema);");
                sb.AppendLine($"            var _result = new object?[{serviceConfigTypes.Count}];");
                for (int i = 0; i < serviceConfigTypes.Count; i++)
                {
                    var sc = serviceConfigTypes[i];
                    sb.AppendLine($"            if (_serviceConfigProvider_{sc.Ident} != null)");
                    sb.AppendLine($"                _result[{i}] = _serviceConfigProvider_{sc.Ident}.GetConfig(_versions[{i}]);");
                }
                sb.AppendLine("            return _result;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Service getter methods.
            // 0.20.0: lazy creation now ALSO sets the service's instance Context property so
            // it doesn't have to look it up via MetaContextAccessor on every member access.
            // Each [MetaServiceImpl] partial declares `public MetaContext<TState> Context { get; internal set; }`.
            sb.AppendLine("        // Service getters");
            foreach (var service in services)
            {
                var baseName = GetBaseName(service.InterfaceName);
                var fieldName = GetFieldName(service.InterfaceName);
                sb.AppendLine($"        private {service.InterfaceName} Get{baseName}() => {fieldName} ??= new {service.ImplClassFullName}() {{ Context = MetaContext! }};");
                if (service.GeneratePatchTrackedCopy)
                {
                    var ptFieldName = fieldName + "PT";
                    sb.AppendLine($"        private {service.ImplClassFullName}_PatchTracked? {ptFieldName};");
                    sb.AppendLine($"        private {service.InterfaceName} Get{baseName}PatchTracked() => {ptFieldName} ??= new {service.ImplClassFullName}_PatchTracked() {{ Context = MetaContext! }};");
                }
            }

            // 0.20.0: Sibling-resolver override. Returns the cached impl instance for any
            // service hosted on this provider's TState — null for any other interface.
            // The instance is wired into MetaContext.SiblingServiceResolver in
            // MetaProviderBase.Initialize, so cross-entity getters can short-circuit
            // self-targeted calls into typed sibling invocations (no serialization).
            sb.AppendLine();
            sb.AppendLine("        public override object? ResolveSiblingByType(System.Type interfaceType)");
            sb.AppendLine("        {");
            foreach (var service in services)
            {
                var baseName = GetBaseName(service.InterfaceName);
                if (service.GeneratePatchTrackedCopy)
                {
                    // When patch tracking is active (force-patch / ServerPatch / deep desync), hand
                    // out the sibling's PatchTracked copy so its mutations to the shared state route
                    // through the wrapper and land in the diff — otherwise a sibling call from a
                    // force-patched method would write the raw state and go missing from the patch.
                    sb.AppendLine($"            if (interfaceType == typeof({service.InterfaceName})) return MetaContext!.PatchWrapper != null ? Get{baseName}PatchTracked() : Get{baseName}();");
                }
                else
                {
                    sb.AppendLine($"            if (interfaceType == typeof({service.InterfaceName})) return Get{baseName}();");
                }
            }
            sb.AppendLine("            return null;");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
        }

        private static void GenerateMetaProviderFactory(StringBuilder sb, string stateTypeFullName, string stateTypeName, bool deepDesync = false)
        {
            var providerName = $"Generated{stateTypeName}MetaProvider";
            var factoryName = $"Generated{stateTypeName}MetaProviderFactory";

            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Factory for {providerName}.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public sealed class {factoryName} : IMetaProviderFactory<{stateTypeFullName}>");
            sb.AppendLine("    {");
            sb.AppendLine("        private readonly Func<Type, object>? _serviceResolver;");
            sb.AppendLine("        private readonly global::SharedMeta.Server.EntityCallHandler? _entityCallHandler;");
            sb.AppendLine();
            sb.AppendLine($"        public {factoryName}(");
            sb.AppendLine("            Func<Type, object>? serviceResolver = null,");
            sb.AppendLine("            global::SharedMeta.Server.EntityCallHandler? entityCallHandler = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            _serviceResolver = serviceResolver;");
            sb.AppendLine("            _entityCallHandler = entityCallHandler;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IMetaProvider<{stateTypeFullName}> Create()");
            sb.AppendLine("        {");
            // Note: [MetaServiceImpl(DeepDesync = true)] only generates the supporting
            // infrastructure (PatchTracked service copy, PatchSchema, etc). Runtime
            // activation is opt-in via EntityGrainOptions.DeepDesyncEnabled (global)
            // or the client-side SetDebugOptions toggle (per session).
            sb.AppendLine($"            return new {providerName}(_serviceResolver, _entityCallHandler);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        private static void GenerateConfigureMetaExtension(
            StringBuilder sb,
            Dictionary<string, List<ServiceImplInfo>> byStateType,
            List<string> serverDeps,
            string rootNamespace,
            bool hasOrleansConfig)
        {
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Extension methods for configuring meta services.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class MetaServiceCollectionExtensions");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Register all meta services and provider factories.");
            sb.AppendLine("        /// Call this from your Orleans silo configuration.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <param name=\"services\">The service collection.</param>");
            sb.AppendLine("        /// <param name=\"configureServices\">Optional callback to configure server services (IRandomService, etc).</param>");
            sb.AppendLine("        public static IServiceCollection ConfigureMeta(");
            sb.AppendLine("            this IServiceCollection services,");
            sb.AppendLine("            Action<IServiceCollection>? configureServices = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            // Configure server-side services (e.g., IRandomService)");
            sb.AppendLine("            configureServices?.Invoke(services);");
            sb.AppendLine();
            sb.AppendLine("            // Populate DispatchResult.True/False/Int/Void cache at host startup —");
            sb.AppendLine("            // hosted service ctor resolves IMetaSerializer and calls InitializeCache");
            sb.AppendLine("            // before any grain activation can dispatch a method.");
            sb.AppendLine("            services.AddHostedService<global::SharedMeta.Server.Core.DispatchResultCacheInitializer>();");
            sb.AppendLine();
            sb.AppendLine("            // Service resolver (resolves from DI)");
            sb.AppendLine("            Func<Type, object> serviceResolver = type =>");
            sb.AppendLine("            {");
            sb.AppendLine("                var sp = services.BuildServiceProvider();");
            sb.AppendLine("                return sp.GetRequiredService(type);");
            sb.AppendLine("            };");
            sb.AppendLine();
            sb.AppendLine("            // Register provider factories for each state type");
            sb.AppendLine("            // Note: entityCallHandler is null here - set by Orleans grain when needed for cross-entity calls");

            foreach (var kvp in byStateType)
            {
                var stateTypeName = kvp.Value.First().StateTypeName;
                var stateTypeFullName = kvp.Key;
                var factoryName = $"Generated{stateTypeName}MetaProviderFactory";

                sb.AppendLine($"            services.AddSingleton<IMetaProviderFactory<{stateTypeFullName}>>(sp =>");
                sb.AppendLine($"                new {factoryName}(");
                sb.AppendLine("                    t => sp.GetRequiredService(t),");
                sb.AppendLine("                    entityCallHandler: null));");
                sb.AppendLine();
            }

            sb.AppendLine("            // Register entity grain resolver");
            sb.AppendLine("            services.AddSingleton<SharedMeta.Server.Core.Grains.IEntityGrainResolver>(GeneratedEntityGrainResolver.Instance);");
            sb.AppendLine();
            sb.AppendLine("            // DI handle for the generated {Service}ServerApi accessors, so callers never have to");
            sb.AppendLine("            // resolve and pass a serializer that DI already holds.");
            sb.AppendLine("            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions");
            sb.AppendLine("                .TryAddSingleton<SharedMeta.Server.Core.IMetaServerApiFactory, SharedMeta.Server.Core.MetaServerApiFactory>(services);");
            sb.AppendLine();
            sb.AppendLine("            // 0.24.0+ Register the generated MetaServerSignature singleton so EntityGrain can");
            sb.AppendLine("            // resolve server-side method ids for the wire MetaOperation.MethodId field, and");
            sb.AppendLine("            // ClientSignatureRegistry can compute per-client capability + method-id maps.");
            sb.AppendLine($"            services.AddSingleton<SharedMeta.Core.Transport.MetaServerSignature>(global::{rootNamespace}.GameServiceDiscoveryBase.ServerSignature);");
            sb.AppendLine();
            sb.AppendLine("            // 0.22.0+ Per-silo client-signature registry — idempotent (TryAddSingleton).");
            sb.AppendLine("            SharedMeta.Server.Core.Session.ClientSignatureRegistryExtensions.AddSharedMetaClientSignatureRegistry(services);");
            sb.AppendLine();
            sb.AppendLine("            // 0.26.2+: Register config download URL resolver as fallback only — TryAdd lets a host-side");
            sb.AppendLine("            // AddSingleton<IConfigDownloadUrlResolver>(...) win without RemoveAll/ordering tricks. The");
            sb.AppendLine("            // default GeneratedConfigDownloadUrlResolver delegates to IMetaConfigProvider<T>.GetDownloadUrl");
            sb.AppendLine("            // which defaults to null; hosts that serve real download URLs ship their own resolver.");
            sb.AppendLine("            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions");
            sb.AppendLine("                .TryAddSingleton<SharedMeta.Server.Core.IConfigDownloadUrlResolver>(services, sp => new GeneratedConfigDownloadUrlResolver(sp));");
            sb.AppendLine();
            sb.AppendLine("            // 0.26.2+: Register IConfigByteSource — non-generic source that materializes config bytes");
            sb.AppendLine("            // for any stateTypeName declared in this assembly. Used by app.MapMetaConfigDownload() to");
            sb.AppendLine("            // serve all configs from a single endpoint.");
            sb.AppendLine("            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions");
            sb.AppendLine("                .TryAddSingleton<SharedMeta.Server.Core.IConfigByteSource>(services, sp => new GeneratedConfigByteSource(sp));");
            sb.AppendLine();
            sb.AppendLine("            // 0.28.0+: Register IConfigCatalog — typed dispatcher (one HandleAsync<TConfig> per");
            sb.AppendLine("            // [MetaConfig]) consumed by the admin grain and bootstrap hosted service.");
            sb.AppendLine("            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions");
            sb.AppendLine("                .TryAddSingleton<SharedMeta.Server.Core.Config.Admin.IConfigCatalog, GeneratedConfigCatalog>(services);");
            sb.AppendLine();
            // 0.27.0+: Auto-register IConfigRegistry façade + a BroadcastingConfigProvider<T>
            // per discovered [MetaConfig] type. Replaces per-type AddSharedMetaConfigProvider<T>()
            // boilerplate that previously lived in user Program.cs. Emitted only when the host
            // references SharedMeta.Orleans — pure SharedMeta.Server.Core hosts (tests, alt
            // backends) skip the Orleans wiring.
            if (hasOrleansConfig)
            {
                // 0.33.0+ fix: this used to collect only the legacy ConfigTypeFullName, so any
                // config declared solely via [ServiceConfig] (no legacy primary anywhere on the
                // state) never got a BroadcastingConfigProvider<T> auto-registered at all —
                // ServiceResolver(typeof(IMetaConfigProvider<T>)) then throws in the generated
                // OnInitialize(), silently caught, leaving that config's resolved version pinned
                // at default(0,0,0) with no visible error. Same root pattern as the 0.33.0
                // config-download-multi-config-identity fix — the 0.33.0 [ServiceConfig] rework
                // added a second way to declare a config type without updating every place that
                // still assumed "legacy ConfigType is the only source".
                var configTypesForRegistration = byStateType
                    .SelectMany(kvp => kvp.Value
                        .SelectMany(s => (s.ConfigTypeFullName != null ? new[] { s.ConfigTypeFullName! } : System.Array.Empty<string>())
                            .Concat(s.ServiceConfigTypeFullNames)))
                    .Distinct()
                    .ToList();
                if (configTypesForRegistration.Count > 0)
                {
                    sb.AppendLine("            // 0.27.0+ Auto-registered IConfigRegistry façade + BroadcastingConfigProvider<T> per [MetaConfig].");
                    sb.AppendLine("            SharedMeta.Orleans.Config.ConfigVersioningExtensions.AddSharedMetaConfigVersioning(services);");
                    foreach (var configFullName in configTypesForRegistration)
                    {
                        sb.AppendLine($"            SharedMeta.Orleans.Config.ConfigVersioningExtensions.AddSharedMetaConfigProvider<{configFullName}>(services);");
                    }
                    sb.AppendLine();
                }
            }
            sb.AppendLine("            // Register patch schema registry (used by diagnostic / desync paths to render PatchNode trees as readable JSON)");
            sb.AppendLine("            services.AddSingleton<SharedMeta.Core.Patch.IPatchSchemaRegistry>(sp =>");
            sb.AppendLine("            {");
            sb.AppendLine("                var byState = new System.Collections.Generic.Dictionary<string, SharedMeta.Core.Patch.IPatchSchema>");
            sb.AppendLine("                {");
            foreach (var kvp in byStateType)
            {
                var stateTypeFullName = kvp.Key;
                sb.AppendLine($"                    [\"{stateTypeFullName}\"] = {stateTypeFullName}PatchSchema.Instance,");
            }
            sb.AppendLine("                };");
            sb.AppendLine("                var byService = new System.Collections.Generic.Dictionary<string, SharedMeta.Core.Patch.IPatchSchema>");
            sb.AppendLine("                {");
            foreach (var kvp in byStateType)
            {
                var stateTypeFullName = kvp.Key;
                foreach (var svc in kvp.Value)
                {
                    sb.AppendLine($"                    [\"{svc.InterfaceName}\"] = {stateTypeFullName}PatchSchema.Instance,");
                }
            }
            sb.AppendLine("                };");
            sb.AppendLine("                return new SharedMeta.Core.Patch.PatchSchemaRegistry(byState, byService);");
            sb.AppendLine("            });");
            sb.AppendLine();
            sb.AppendLine("            // Register ClientVersionPolicy (runtime-mutable version gate)");
            sb.AppendLine("            // Initialized from MetaTransportOptions; update MinClientVersion at runtime without restart.");
            sb.AppendLine("            if (!services.Any(s => s.ServiceType == typeof(SharedMeta.Server.Core.Transport.ClientVersionPolicy)))");
            sb.AppendLine("            {");
            sb.AppendLine("                services.AddSingleton(sp =>");
            sb.AppendLine("                {");
            sb.AppendLine("                    var opts = sp.GetService<SharedMeta.Server.Core.Transport.MetaTransportOptions>();");
            sb.AppendLine("                    var grainFactory = sp.GetService<Orleans.IGrainFactory>();");
            sb.AppendLine("                    return new SharedMeta.Server.Core.Transport.ClientVersionPolicy(opts?.ServerVersion, opts?.MinClientVersion, opts?.MaxClientVersion, grainFactory);");
            sb.AppendLine("                });");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            // Register MetaConnectionHandlerFactory with signature validator + transport options + serializer + schema registry + version policy + client signature registry + server signature");
            sb.AppendLine("            services.AddSingleton<SharedMeta.Server.Core.Transport.IMetaConnectionHandlerFactory>(sp =>");
            sb.AppendLine("                new SharedMeta.Server.Core.Transport.MetaConnectionHandlerFactory(");
            sb.AppendLine("                    sp.GetRequiredService<Orleans.IGrainFactory>(),");
            sb.AppendLine("                    sp.GetRequiredService<SharedMeta.Server.Core.Grains.IEntityGrainResolver>(),");
            sb.AppendLine("                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Server.Core.Transport.MetaTransportOptions>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Core.IMetaSerializer>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Core.Patch.IPatchSchemaRegistry>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Server.Core.Transport.ClientVersionPolicy>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Server.Core.Session.IClientSignatureRegistry>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Core.Transport.MetaServerSignature>()));");
            sb.AppendLine();

            sb.AppendLine("            return services;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        private static string GetBaseName(string interfaceName)
        {
            if (interfaceName.StartsWith("I") && interfaceName.Length > 1 && char.IsUpper(interfaceName[1]))
            {
                return interfaceName.Substring(1);
            }
            return interfaceName;
        }

        private static string GetFieldName(string interfaceName)
        {
            var baseName = GetBaseName(interfaceName);
            return "_" + char.ToLower(baseName[0]) + baseName.Substring(1);
        }

        /// <summary>
        /// Generate compile-time entity grain resolver.
        /// Replaces runtime reflection (FindStateType + MakeGenericType) with a generated switch.
        /// </summary>
        private static void GenerateEntityGrainResolver(
            StringBuilder sb,
            Dictionary<string, List<ServiceImplInfo>> byStateType)
        {
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Generated entity grain resolver.");
            sb.AppendLine("    /// Resolves state type names to IEntityGrain references using compile-time switch.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public sealed class GeneratedEntityGrainResolver : SharedMeta.Server.Core.Grains.IEntityGrainResolver");
            sb.AppendLine("    {");
            sb.AppendLine("        public static readonly GeneratedEntityGrainResolver Instance = new();");
            sb.AppendLine();
            sb.AppendLine("        public IEntityGrainBase? GetEntityGrain(Orleans.IGrainFactory grainFactory, string stateTypeName, string entityId)");
            sb.AppendLine("        {");
            sb.AppendLine("            return stateTypeName switch");
            sb.AppendLine("            {");

            foreach (var kvp in byStateType)
            {
                var stateTypeFullName = kvp.Key;
                var stateTypeName = kvp.Value.First().StateTypeName;
                sb.AppendLine($"                \"{stateTypeName}\" or \"{stateTypeFullName}\"");
                sb.AppendLine($"                    => grainFactory.GetGrain<IEntityGrain<{stateTypeFullName}>>(entityId),");
            }

            sb.AppendLine("                _ => null");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Generate GetEntityGrainByService (for cross-entity calls by service name)
            sb.AppendLine("        public IEntityGrainBase? GetEntityGrainByService(Orleans.IGrainFactory grainFactory, string serviceName, string entityId)");
            sb.AppendLine("        {");
            sb.AppendLine("            return serviceName switch");
            sb.AppendLine("            {");

            foreach (var kvp in byStateType)
            {
                var stateTypeFullName = kvp.Key;
                foreach (var service in kvp.Value)
                {
                    sb.AppendLine($"                \"{service.InterfaceName}\" => grainFactory.GetGrain<IEntityGrain<{stateTypeFullName}>>(entityId),");
                }
            }

            sb.AppendLine("                _ => null");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        /// <summary>
        /// 0.33.0+ One row per (state, configType) pair a state exposes — the legacy primary
        /// <c>[MetaService(ConfigType=...)]</c> (state-wide first-non-null, same simplification
        /// used elsewhere for the legacy primary) PLUS every <c>[ServiceConfig]</c> entry across
        /// every service sharing the state (deduped by type). Replaces the pre-0.33.0
        /// "one config per state" assumption in <see cref="GenerateConfigByteSource"/> /
        /// <see cref="GenerateConfigDownloadUrlResolver"/>, which silently exposed only the
        /// legacy primary (or nothing at all for [ServiceConfig]-only states).
        /// </summary>
        private static List<(string StateTypeFullName, string StateTypeName, string ConfigTypeFullName)> CollectStateConfigPairs(
            Dictionary<string, List<ServiceImplInfo>> byStateType)
        {
            var result = new List<(string, string, string)>();
            foreach (var kvp in byStateType)
            {
                var stateTypeFullName = kvp.Key;
                var stateTypeName = kvp.Value.First().StateTypeName;

                var configTypes = new List<string>();
                var legacy = kvp.Value.Select(s => s.ConfigTypeFullName).FirstOrDefault(c => c != null);
                if (legacy != null)
                    configTypes.Add(legacy);
                foreach (var t in kvp.Value.SelectMany(s => s.ServiceConfigTypeFullNames).Distinct())
                {
                    if (!configTypes.Contains(t)) configTypes.Add(t);
                }

                foreach (var configType in configTypes)
                    result.Add((stateTypeFullName, stateTypeName, configType));
            }
            return result;
        }

        private static HashSet<string/*ConfigTypeFullName*/> CollectConfigs(Dictionary<string, List<ServiceImplInfo>> byStateType)
        {
            var result = new HashSet<string>();
            foreach (var kvp in byStateType) {
                var legacy = kvp.Value.Select(s => s.ConfigTypeFullName).FirstOrDefault(c => c != null);
                if (legacy != null)
                    result.Add(legacy);
                foreach (var t in kvp.Value.SelectMany(s => s.ServiceConfigTypeFullNames).Distinct())
                {
                    result.Add(t);
                }
            }
            return result;
        }

        // Field identifier is per CONFIG TYPE (not per state) — several states commonly share
        // one config type (e.g. a SessionConfig used by many services), and DI resolves the
        // same IMetaConfigProvider<TConfig> singleton either way, so one field/one GetService
        // call covers every (state, configType) pair naming that type.
        private static string ConfigFieldIdent(string configTypeFullName)
            => "_config_" + new string(configTypeFullName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        /// <summary>
        /// 0.26.2+: Generate IConfigByteSource implementation — non-generic,
        /// auto-routes (stateTypeName, configTypeName, version) to the right
        /// IMetaConfigProvider&lt;TConfig&gt; using the same state→config map as
        /// GeneratedConfigDownloadUrlResolver, serializes via the supplied IMetaSerializer.
        /// </summary>
        private static void GenerateConfigByteSource(
            StringBuilder sb,
            Dictionary<string, List<ServiceImplInfo>> byStateType)
        {
            var stateConfigPairs = CollectStateConfigPairs(byStateType);
            var distinctConfigTypes = stateConfigPairs.Select(p => p.ConfigTypeFullName).Distinct().ToList();

            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Generated non-generic config byte source. Pair with app.MapMetaConfigDownload()");
            sb.AppendLine("    /// (no generic parameter) to serve all configs from a single endpoint, routed by");
            sb.AppendLine("    /// (stateTypeName, configTypeName) at request time.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public sealed class GeneratedConfigByteSource : SharedMeta.Server.Core.IConfigByteSource");
            sb.AppendLine("    {");

            if (distinctConfigTypes.Count > 0)
            {
                foreach (var configType in distinctConfigTypes)
                {
                    sb.AppendLine($"        private readonly SharedMeta.Server.Core.IMetaConfigProvider<{configType}>? {ConfigFieldIdent(configType)};");
                }
                sb.AppendLine();

                sb.AppendLine("        public GeneratedConfigByteSource(System.IServiceProvider sp)");
                sb.AppendLine("        {");
                foreach (var configType in distinctConfigTypes)
                {
                    sb.AppendLine($"            {ConfigFieldIdent(configType)} = sp.GetService<SharedMeta.Server.Core.IMetaConfigProvider<{configType}>>();");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine("        public byte[]? GetBytes(SharedMeta.Core.IMetaSerializer serializer, string configTypeName, SharedMeta.Core.MetaConfigVersion version)");
                sb.AppendLine("        {");
                sb.AppendLine("            switch (configTypeName)");
                sb.AppendLine("            {");

                foreach (var сonfigTypeFullName in distinctConfigTypes)
                {
                    var fieldName = ConfigFieldIdent(сonfigTypeFullName);
                    sb.AppendLine($"                case \"{сonfigTypeFullName}\":");
                    sb.AppendLine($"                {{");
                    sb.AppendLine($"                    if ({fieldName} == null) return null;");
                    sb.AppendLine($"                    var cfg = {fieldName}.GetConfig(version);");
                    sb.AppendLine($"                    return cfg == null ? null : serializer.PackForExternalUsage(cfg);");
                    sb.AppendLine($"                }}");
                }

                sb.AppendLine("                default: return null;");
                sb.AppendLine("            }");
                sb.AppendLine("    }");

                sb.AppendLine("        public async Task<byte[]?> GetBytesAsync(SharedMeta.Core.IMetaSerializer serializer, string configTypeName, SharedMeta.Core.MetaConfigVersion version)");
                sb.AppendLine("        {");
                sb.AppendLine("            switch (configTypeName)");
                sb.AppendLine("            {");

                foreach (var сonfigTypeFullName in distinctConfigTypes)
                {
                    var fieldName = ConfigFieldIdent(сonfigTypeFullName);
                    sb.AppendLine($"                case \"{сonfigTypeFullName}\":");
                    sb.AppendLine($"                {{");
                    sb.AppendLine($"                    if ({fieldName} == null) return null;");
                    sb.AppendLine($"                    var cfg = await {fieldName}.GetConfigAsync(version);");
                    sb.AppendLine($"                    return cfg == null ? null : serializer.PackForExternalUsage(cfg);");
                    sb.AppendLine($"                }}");
                }

                sb.AppendLine("                default: return null;");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
            }
            else
            {
                sb.AppendLine("        public GeneratedConfigByteSource(System.IServiceProvider sp) { }");
                sb.AppendLine();
                sb.AppendLine("        public byte[]? GetBytes(SharedMeta.Core.IMetaSerializer serializer, string configTypeName, SharedMeta.Core.MetaConfigVersion version) => null;");
                sb.AppendLine("        public Task<byte[]?> GetBytesAsync(SharedMeta.Core.IMetaSerializer serializer, string configTypeName, SharedMeta.Core.MetaConfigVersion version) => Task.FromResult<byte[]?>(null);");
            }

            sb.AppendLine("    }");

            // 0.28.0+ Generated IConfigCatalog — typed dispatcher used by the admin grain and
            // bootstrap hosted service. Closes TConfig at compile time per [MetaConfig] type;
            // framework callers invoke HandleAsync<TConfig> through IConfigCatalogHandler.
            // The catalog is per [MetaConfig] TYPE, but stateConfigPairs holds one record per
            // (state, configType) pair. Dedup by ConfigTypeFullName so N states/services sharing
            // one config type (or a state exposing one config as both legacy primary and via
            // [ServiceConfig]) don't produce duplicate catalog rows — ConfigAdminGrain.ListConfigsAsync
            // would otherwise show N sections for the same config and ForEachAsync would warm the
            // same provider N times. OwnerStateType takes the first pair's state (optional
            // metadata only — dispatch is by HandleAsync<TConfig>, not by state).
            GenerateConfigCatalog(sb, stateConfigPairs
                .GroupBy(e => e.ConfigTypeFullName)
                .Select(g => g.First())
                .Select(e => (e.ConfigTypeFullName, e.StateTypeFullName))
                .ToList());
        }

        /// <summary>
        /// 0.28.0+ Emits <c>GeneratedConfigCatalog : IConfigCatalog</c> with one
        /// <c>HandleAsync&lt;TConfig&gt;</c> dispatch per <c>[MetaConfig]</c> type. Replaces
        /// the runtime <c>IConfigByteSource.Configs</c> list — framework code consumes the
        /// catalog through the visitor interface without ever touching <c>System.Type</c>.
        /// </summary>
        private static void GenerateConfigCatalog(StringBuilder sb, List<(string ConfigTypeFullName, string StateTypeFullName)> entries)
        {
            sb.AppendLine();
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// 0.28.0+ Generated typed dispatcher over every [MetaConfig] in this assembly.");
            sb.AppendLine("    /// Closes TConfig at the compile-time callback sites — bootstrap + admin grain");
            sb.AppendLine("    /// iterate without reflection or Type arguments.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public sealed class GeneratedConfigCatalog : SharedMeta.Server.Core.Config.Admin.IConfigCatalog");
            sb.AppendLine("    {");
            sb.AppendLine("        private static readonly System.Collections.Generic.IReadOnlyList<SharedMeta.Server.Core.Config.Admin.ConfigCatalogEntry> _entries = new SharedMeta.Server.Core.Config.Admin.ConfigCatalogEntry[]");
            sb.AppendLine("        {");
            foreach (var e in entries)
            {
                sb.AppendLine("            new SharedMeta.Server.Core.Config.Admin.ConfigCatalogEntry");
                sb.AppendLine("            {");
                sb.AppendLine($"                FullName = typeof({e.ConfigTypeFullName}).FullName!,");
                sb.AppendLine($"                DisplayName = typeof({e.ConfigTypeFullName}).Name,");
                sb.AppendLine($"                OwnerStateType = typeof({e.StateTypeFullName}),");
                sb.AppendLine("            },");
            }
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        public System.Collections.Generic.IReadOnlyList<SharedMeta.Server.Core.Config.Admin.ConfigCatalogEntry> Entries => _entries;");
            sb.AppendLine();
            sb.AppendLine("        public async System.Threading.Tasks.Task ForEachAsync(");
            sb.AppendLine("            SharedMeta.Server.Core.Config.Admin.IConfigCatalogHandler handler,");
            sb.AppendLine("            System.Threading.CancellationToken cancellationToken = default)");
            sb.AppendLine("        {");
            foreach (var e in entries)
            {
                sb.AppendLine($"            await handler.HandleAsync<{e.ConfigTypeFullName}>(typeof({e.ConfigTypeFullName}).FullName!, typeof({e.ConfigTypeFullName}).Name, cancellationToken);");
            }
            if (entries.Count == 0)
                sb.AppendLine("            await System.Threading.Tasks.Task.CompletedTask;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public async System.Threading.Tasks.Task<bool> TryDispatchAsync(");
            sb.AppendLine("            string name,");
            sb.AppendLine("            SharedMeta.Server.Core.Config.Admin.IConfigCatalogHandler handler,");
            sb.AppendLine("            System.Threading.CancellationToken cancellationToken = default)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (name)");
            sb.AppendLine("            {");
            foreach (var e in entries)
            {
                sb.AppendLine($"                case var n when n == typeof({e.ConfigTypeFullName}).FullName || n == typeof({e.ConfigTypeFullName}).Name:");
                sb.AppendLine($"                    await handler.HandleAsync<{e.ConfigTypeFullName}>(typeof({e.ConfigTypeFullName}).FullName!, typeof({e.ConfigTypeFullName}).Name, cancellationToken);");
                sb.AppendLine("                    return true;");
            }
            sb.AppendLine("                default: return false;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        /// <summary>
        /// Generate IConfigDownloadUrlResolver implementation.
        /// Resolves config download URLs via IMetaConfigProvider instances from DI.
        /// </summary>
        private static void GenerateConfigDownloadUrlResolver(
            StringBuilder sb,
            Dictionary<string, List<ServiceImplInfo>> byStateType)
        {
            var distinctConfigTypes = CollectConfigs(byStateType);

            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Generated config download URL resolver.");
            sb.AppendLine("    /// Resolves download URLs via IMetaConfigProvider instances from DI, routed by");
            sb.AppendLine("    /// (stateTypeName, configTypeName).");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public sealed class GeneratedConfigDownloadUrlResolver : SharedMeta.Server.Core.IConfigDownloadUrlResolver");
            sb.AppendLine("    {");

            if (distinctConfigTypes.Count > 0)
            {
                // Fields for each config provider — one per distinct config TYPE (not per
                // state); several states commonly share a config type.
                foreach (var configType in distinctConfigTypes)
                {
                    sb.AppendLine($"        private readonly SharedMeta.Server.Core.IMetaConfigProvider<{configType}>? {ConfigFieldIdent(configType)};");
                }
                sb.AppendLine();

                // Constructor
                sb.AppendLine("        public GeneratedConfigDownloadUrlResolver(System.IServiceProvider sp)");
                sb.AppendLine("        {");
                foreach (var configType in distinctConfigTypes)
                {
                    sb.AppendLine($"            {ConfigFieldIdent(configType)} = sp.GetService<SharedMeta.Server.Core.IMetaConfigProvider<{configType}>>();");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                // GetDownloadUrl
                sb.AppendLine("        public string? GetDownloadUrl(string configTypeName, SharedMeta.Core.MetaConfigVersion version)");
                sb.AppendLine("        {");
                sb.AppendLine("            return configTypeName switch");
                sb.AppendLine("            {");

                foreach (var configTypeFullName in distinctConfigTypes) {
                    var fieldName = ConfigFieldIdent(configTypeFullName);
                    sb.AppendLine($"                \"{configTypeFullName}\" => {fieldName}?.GetDownloadUrl(version),");
                }

                sb.AppendLine("                _ => null");
                sb.AppendLine("            };");
                sb.AppendLine("        }");
            }
            else
            {
                // No config providers — return null always
                sb.AppendLine("        public GeneratedConfigDownloadUrlResolver(System.IServiceProvider sp) { }");
                sb.AppendLine();
                sb.AppendLine("        public string? GetDownloadUrl(string configTypeName, SharedMeta.Core.MetaConfigVersion version) => null;");
            }

            sb.AppendLine("    }");
        }

    }
}
