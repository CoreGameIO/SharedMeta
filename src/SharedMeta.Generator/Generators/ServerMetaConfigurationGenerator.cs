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
        /// <summary>True if [MetaServiceImpl(DeepDesync = true)].</summary>
        public bool DeepDesync { get; set; }

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

                info.MigrationConditions.Add(new MigrationCondition
                {
                    StateVersion  = stateVer,
                    Major         = major,
                    Minor         = minor,
                    ConfigTypeFullName = configTypeFull,
                    ConfigTypeIdent    = configTypeIdent,
                });
            }
            // Sort by ascending StateVersion so the generator emits steps in order
            info.MigrationConditions.Sort((a, b) => a.StateVersion.CompareTo(b.StateVersion));

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
                if (metaMethodAttr != null)
                {
                    // Legacy bool form.
                    var queryArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Query");
                    bool legacyQuery = !queryArg.Value.IsNull && queryArg.Value.Value is true;

                    var signalArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Signal");
                    bool legacySignal = !signalArg.Value.IsNull && signalArg.Value.Value is true;

                    // Canonical Mode form. Mode is an int (enum value); compare against known positions.
                    // ExecutionMode layout: Local=0, Optimistic=1, Server=2, CrossOptimistic=3,
                    // ServerPatch=4, ServerReplace=5, Query=6, Signal=7.
                    bool modeIsQuery = false;
                    bool modeIsSignal = false;
                    var modeArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Mode");
                    if (!modeArg.Value.IsNull && modeArg.Value.Value is int modeVal)
                    {
                        if (modeVal == 6) modeIsQuery = true;
                        else if (modeVal == 7) modeIsSignal = true;
                    }

                    isQuery = legacyQuery || modeIsQuery;
                    isSignal = legacySignal || modeIsSignal;

                    var openAccessArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "OpenAccess");
                    isOpenAccess = !openAccessArg.Value.IsNull && openAccessArg.Value.Value is true;
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
                    SignatureString = signatureString,
                    SignatureHash = signatureHash,
                    IsQuery = isQuery,
                    IsOpenAccess = isOpenAccess,
                    IsSignal = isSignal,
                    SkipMigration = skipMigration,
                    MinStateVersion = minStateVersion
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
        public static string? GenerateForServerProject(string rootNamespace, IEnumerable<ServiceImplInfo> serviceImpls)
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

            // 4. Generate ConfigureMeta extension method
            GenerateConfigureMetaExtension(sb, byStateType, allServerDeps);
            sb.AppendLine();

            // 5. Generate method signature validation
            GenerateSignatureValidation(sb, services);

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Generate all server-side configuration code.
        /// Uses #if SHAREDMETA_SERVER wrapper (legacy mode for Shared project generation).
        /// </summary>
        public static string? Generate(string rootNamespace, IEnumerable<ServiceImplInfo> serviceImpls)
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

            // 4. Generate ConfigureMeta extension method
            GenerateConfigureMetaExtension(sb, byStateType, allServerDeps);
            sb.AppendLine();

            // 5. Generate method signature validation
            GenerateSignatureValidation(sb, services);

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
                sb.AppendLine($"                \"{service.InterfaceName}\" => (svc, method, payload, ser) => {service.InterfaceName}Dispatcher.Dispatch(({service.InterfaceName})svc, method, payload, ser),");
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
                sb.AppendLine($"                \"{service.InterfaceName}\" => (svc, subIface, method, data, ser) => {baseName}SubscriberDispatcher.Dispatch(svc, subIface, method, data, ser),");
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
            sb.AppendLine("            Func<string, string, string, byte[], long, Task<SharedMeta.Server.CrossEntityCallInfo>>? entityCallHandler = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            ServiceResolver = serviceResolver;");
            sb.AppendLine("            EntityCallHandler = entityCallHandler;");
            sb.AppendLine("        }");
            sb.AppendLine();

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

            // Emit OnInitialize when EITHER a primary config exists OR migration conditions
            // reference secondary config types (a state with [MetaStateVersion] but no service
            // declaring a primary ConfigType still needs the migration providers wired up).
            bool hasAnyConfigProvider = configType != null || secondaryProviders.Count > 0;

            if (configType != null)
            {
                sb.AppendLine($"        private SharedMeta.Server.Core.IMetaConfigProvider<{configType}>? _configProvider;");
                sb.AppendLine($"        private SharedMeta.Server.Core.IConfigVersionResolver? _configVersionResolver;");
            }
            foreach (var sec in secondaryProviders)
                sb.AppendLine($"        private SharedMeta.Server.Core.IMetaConfigProvider<{sec.ConfigType}>? _configProvider_{sec.Ident};");

            if (hasAnyConfigProvider)
            {
                sb.AppendLine();
                sb.AppendLine("        protected override void OnInitialize()");
                sb.AppendLine("        {");
                sb.AppendLine("            if (ServiceResolver != null)");
                sb.AppendLine("            {");
                if (configType != null)
                {
                    sb.AppendLine($"                _configProvider = (SharedMeta.Server.Core.IMetaConfigProvider<{configType}>)ServiceResolver(typeof(SharedMeta.Server.Core.IMetaConfigProvider<{configType}>));");
                    sb.AppendLine($"                try {{ _configVersionResolver = ServiceResolver(typeof(SharedMeta.Server.Core.IConfigVersionResolver)) as SharedMeta.Server.Core.IConfigVersionResolver; }} catch {{ }}");
                }
                foreach (var sec in secondaryProviders)
                    sb.AppendLine($"                try {{ _configProvider_{sec.Ident} = ServiceResolver(typeof(SharedMeta.Server.Core.IMetaConfigProvider<{sec.ConfigType}>)) as SharedMeta.Server.Core.IMetaConfigProvider<{sec.ConfigType}>; }} catch {{ }}");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (configType != null)
            {
                sb.AppendLine("        public override void InitializeConfig(SharedMeta.Core.MetaConfigVersion version)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_configProvider == null) { base.InitializeConfig(version); return; }");
                sb.AppendLine();
                sb.AppendLine("            // Resolve version for new entities (persisted version is 0,0)");
                sb.AppendLine("            if (version.Major == 0 && version.Minor == 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                var currentVersion = _configProvider.CurrentVersion;");
                sb.AppendLine("                version = _configVersionResolver != null");
                sb.AppendLine($"                    ? _configVersionResolver.ResolveVersion(\"{stateTypeFullName}\", Context.EntityId, currentVersion)");
                sb.AppendLine("                    : currentVersion;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            base.InitializeConfig(version);");
                sb.AppendLine("            MetaContext!.Config = _configProvider.GetConfig(version);");
                sb.AppendLine("        }");
                sb.AppendLine();

                // ResolveClientConfigVersion — uses [MetaConfigVersion] rules on the config class
                // to pick the config branch appropriate for the connecting client's app version.
                sb.AppendLine($"        private static readonly SharedMeta.Core.MetaConfigVersionResolver? _configVersionPolicyResolver");
                sb.AppendLine($"            = SharedMeta.Core.MetaConfigVersionResolver.ForType(typeof({configType}));");
                sb.AppendLine();
                sb.AppendLine("        public override SharedMeta.Core.MetaConfigVersion ResolveClientConfigVersion(string? clientVersion)");
                sb.AppendLine("            => _configProvider?.ResolveForClient(clientVersion, _configVersionPolicyResolver) ?? ConfigVersion;");
                sb.AppendLine();

                // GetCachedConfigForClient — per-call config cache:
                //   1. clientVersion → resolved MetaConfigVersion (via [MetaConfigVersion] rules)
                //   2. resolved version → TConfig instance (one entry per branch per grain activation)
                // Cache invalidates when the provider's CurrentVersion advances (runtime patch deploy).
                // Both lookups are O(1) dict access on the hot RPC path.
                sb.AppendLine($"        private readonly System.Collections.Generic.Dictionary<string, {configType}> _configCacheByClient = new System.Collections.Generic.Dictionary<string, {configType}>();");
                sb.AppendLine("        private SharedMeta.Core.MetaConfigVersion _lastSeenProviderVersion;");
                sb.AppendLine();
                sb.AppendLine("        protected override object? GetCachedConfigForClient(string? clientVersion)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_configProvider == null) return null;");
                sb.AppendLine("            var providerCurrent = _configProvider.CurrentVersion;");
                sb.AppendLine("            if (providerCurrent != _lastSeenProviderVersion)");
                sb.AppendLine("            {");
                sb.AppendLine("                _configCacheByClient.Clear();");
                sb.AppendLine("                _lastSeenProviderVersion = providerCurrent;");
                sb.AppendLine("            }");
                sb.AppendLine("            var key = clientVersion ?? string.Empty;");
                sb.AppendLine("            if (!_configCacheByClient.TryGetValue(key, out var cached))");
                sb.AppendLine("            {");
                sb.AppendLine("                var resolved = ResolveClientConfigVersion(clientVersion);");
                sb.AppendLine("                cached = _configProvider.GetConfig(resolved);");
                sb.AppendLine("                _configCacheByClient[key] = cached;");
                sb.AppendLine("            }");
                sb.AppendLine("            return cached;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine("        protected override void ClearConfigCache()");
                sb.AppendLine("        {");
                sb.AppendLine("            _configCacheByClient.Clear();");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Implement abstract DispatchCall
            sb.AppendLine("        protected override async Task<DispatchResult> DispatchCall(string serviceName, string methodName, byte[] payload)");
            sb.AppendLine("        {");
            sb.AppendLine("            return serviceName switch");
            sb.AppendLine("            {");
            foreach (var service in services)
            {
                var baseName = GetBaseName(service.InterfaceName);
                if (service.DeepDesync)
                {
                    // Deep desync: use PatchTracked version when PatchWrapper is active
                    sb.AppendLine($"                \"{service.InterfaceName}\" => MetaContext!.PatchWrapper != null");
                    sb.AppendLine($"                    ? await {service.InterfaceName}Dispatcher.Dispatch(Get{baseName}PatchTracked(), methodName, payload, Context.Serializer)");
                    sb.AppendLine($"                    : await {service.InterfaceName}Dispatcher.Dispatch(Get{baseName}(), methodName, payload, Context.Serializer),");
                }
                else
                {
                    sb.AppendLine($"                \"{service.InterfaceName}\" => await {service.InterfaceName}Dispatcher.Dispatch(Get{baseName}(), methodName, payload, Context.Serializer),");
                }
            }
            sb.AppendLine("                _ => throw new InvalidOperationException($\"Unknown service: {serviceName}\")");
            sb.AppendLine("            };");
            sb.AppendLine("        }");

            // Override DispatchEvent if there are subscriber interfaces
            if (servicesWithSubscribers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        protected override Task<DispatchResult> DispatchEvent(string subscriberInterface, string methodName, byte[] eventData)");
                sb.AppendLine("        {");
                sb.AppendLine("            switch (subscriberInterface)");
                sb.AppendLine("            {");
                foreach (var service in servicesWithSubscribers)
                {
                    var baseName = GetBaseName(service.InterfaceName);
                    foreach (var subscriber in service.SubscriberInterfaces)
                    {
                        var shortName = subscriber.Split('.').Last();
                        sb.AppendLine($"                case \"{shortName}\":");
                        sb.AppendLine($"                case \"{subscriber}\":");
                    }
                    sb.AppendLine($"                    {baseName}SubscriberDispatcher.Dispatch(Get{baseName}(), subscriberInterface, methodName, eventData, Context.Serializer);");
                    sb.AppendLine("                    break;");
                }
                sb.AppendLine("                default:");
                sb.AppendLine("                    Console.Error.WriteLine($\"Unknown subscriber: {subscriberInterface}\");");
                sb.AppendLine("                    break;");
                sb.AppendLine("            }");
                sb.AppendLine("            return Task.FromResult(new DispatchResult());");
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

            // IsQueryMethod / IsOpenAccessQuery overrides
            var allQueryMethods = services
                .SelectMany(s => s.MethodSignatures)
                .Where(m => m.IsQuery)
                .ToList();
            if (allQueryMethods.Count > 0)
            {
                sb.AppendLine("        public override bool IsQueryMethod(string serviceName, string methodName)");
                sb.AppendLine("        {");
                sb.AppendLine("            return (serviceName, methodName) switch");
                sb.AppendLine("            {");
                foreach (var m in allQueryMethods)
                    sb.AppendLine($"                (\"{m.ServiceName}\", \"{m.MethodAlias}\") => true,");
                sb.AppendLine("                _ => false");
                sb.AppendLine("            };");
                sb.AppendLine("        }");
                sb.AppendLine();

                var openAccessMethods = allQueryMethods.Where(m => m.IsOpenAccess).ToList();
                if (openAccessMethods.Count > 0)
                {
                    sb.AppendLine("        public override bool IsOpenAccessQuery(string serviceName, string methodName)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            return (serviceName, methodName) switch");
                    sb.AppendLine("            {");
                    foreach (var m in openAccessMethods)
                        sb.AppendLine($"                (\"{m.ServiceName}\", \"{m.MethodAlias}\") => true,");
                    sb.AppendLine("                _ => false");
                    sb.AppendLine("            };");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                }
            }

            // IsSignalMethod + DispatchSignal overrides — only emitted when this state's services
            // contain at least one [MetaMethod(Signal = true)]. The override routes to the generated
            // {Service}SignalDispatcher companion emitted by ServerDispatcherGenerator.
            var allSignalMethods = services
                .SelectMany(s => s.MethodSignatures.Select(m => new { Impl = s, Sig = m }))
                .Where(x => x.Sig.IsSignal)
                .ToList();
            if (allSignalMethods.Count > 0)
            {
                sb.AppendLine("        public override bool IsSignalMethod(string serviceName, string methodName)");
                sb.AppendLine("        {");
                sb.AppendLine("            return (serviceName, methodName) switch");
                sb.AppendLine("            {");
                foreach (var x in allSignalMethods)
                    sb.AppendLine($"                (\"{x.Sig.ServiceName}\", \"{x.Sig.MethodAlias}\") => true,");
                sb.AppendLine("                _ => false");
                sb.AppendLine("            };");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine("        protected override async Task DispatchSignal(string serviceName, string methodName, byte[] payload)");
                sb.AppendLine("        {");
                sb.AppendLine("            switch (serviceName)");
                sb.AppendLine("            {");
                // Group signal methods by interface, one switch arm per service.
                var signalsByService = allSignalMethods
                    .GroupBy(x => x.Impl.InterfaceName)
                    .ToList();
                foreach (var group in signalsByService)
                {
                    var anyImpl = group.First().Impl;
                    var baseName = GetBaseName(anyImpl.InterfaceName);
                    // Signal dispatcher is emitted by ServerDispatcherGenerator in the same namespace
                    // as the main dispatcher — we route by service interface name.
                    sb.AppendLine($"                case \"{anyImpl.InterfaceName}\":");
                    sb.AppendLine($"                    await {anyImpl.InterfaceName}SignalDispatcher.Dispatch(Get{baseName}(), methodName, payload, Context.Serializer);");
                    sb.AppendLine("                    break;");
                }
                sb.AppendLine("                default: throw new MissingMethodException($\"Signal service '{serviceName}' not found\");");
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
                    if (!hasExplicitSchemaOne && primaryServicesWithInit.Count > 0 && configType != null)
                    {
                        sb.AppendLine("            if (currentVersion < 1 && 1 <= _migrationCap && _configProvider != null)");
                        sb.AppendLine("            {");
                        sb.AppendLine("                var _savedConfig0 = MetaContext!.Config;");
                        sb.AppendLine("                try");
                        sb.AppendLine("                {");
                        sb.AppendLine("                    MetaContext!.Config = _configProvider.GetConfig(new SharedMeta.Core.MetaConfigVersion(1, 0, 0));");
                        sb.AppendLine("                    MetaContext!.ConfigVersion = new SharedMeta.Core.MetaConfigVersion(1, 0, 0);");
                        sb.AppendLine("                    MetaContext!.Version = currentVersion;");
                        foreach (var svc in primaryServicesWithInit)
                        {
                            var baseName = GetBaseName(svc.InterfaceName);
                            var initArgs = svc.MetaInitParameterCount == 2 ? "currentVersion, 1" : "currentVersion";
                            sb.AppendLine($"                    maxVersion = System.Math.Max(maxVersion, await (({svc.ImplClassFullName})Get{baseName}()).{svc.MetaInitMethodName}({initArgs}));");
                        }
                        sb.AppendLine("                }");
                        sb.AppendLine("                finally { MetaContext!.Config = _savedConfig0; }");
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

                        // Emit boolean checks for each AND condition.
                        for (int ci = 0; ci < conds.Count; ci++)
                        {
                            var cond = conds[ci];
                            var provField = (cond.ConfigTypeFullName == null || cond.ConfigTypeFullName == configType)
                                ? "_configProvider"
                                : $"_configProvider_{cond.ConfigTypeIdent}";
                            sb.AppendLine($"                var _cv{ci} = {provField}!.CurrentVersion;");
                            sb.AppendLine($"                bool _ok{ci} = _cv{ci}.Major > {cond.Major} || (_cv{ci}.Major == {cond.Major} && _cv{ci}.Minor >= {cond.Minor});");
                        }

                        var allOk = string.Join(" && ", Enumerable.Range(0, conds.Count).Select(i => $"_ok{i}"));
                        sb.AppendLine($"                if ({allOk})");
                        sb.AppendLine("                {");
                        sb.AppendLine("                    var _savedConfig = MetaContext!.Config;");
                        sb.AppendLine("                    try");
                        sb.AppendLine("                    {");

                        // For each condition: find the service whose config type matches, set config, call [MetaInit].
                        foreach (var cond in conds)
                        {
                            var provField = (cond.ConfigTypeFullName == null || cond.ConfigTypeFullName == configType)
                                ? "_configProvider"
                                : $"_configProvider_{cond.ConfigTypeIdent}";
                            // Find matching service by config type
                            var matchingSvc = servicesWithInit.FirstOrDefault(s =>
                                (cond.ConfigTypeFullName == null && (s.ConfigTypeFullName == null || s.ConfigTypeFullName == configType))
                                || s.ConfigTypeFullName == cond.ConfigTypeFullName);
                            if (matchingSvc == null) continue;

                            var baseName = GetBaseName(matchingSvc.InterfaceName);
                            var initArgs = matchingSvc.MetaInitParameterCount == 2
                                ? $"currentVersion, {targetSchema}"
                                : "currentVersion";
                            sb.AppendLine($"                        MetaContext!.Config = {provField}!.GetConfig(new SharedMeta.Core.MetaConfigVersion({cond.Major}, {cond.Minor}, 0));");
                            sb.AppendLine($"                        MetaContext!.ConfigVersion = new SharedMeta.Core.MetaConfigVersion({cond.Major}, {cond.Minor}, 0);");
                            sb.AppendLine($"                        MetaContext!.Version = currentVersion;");
                            sb.AppendLine($"                        maxVersion = System.Math.Max(maxVersion, await (({matchingSvc.ImplClassFullName})Get{baseName}()).{matchingSvc.MetaInitMethodName}({initArgs}));");
                        }

                        sb.AppendLine("                    }");
                        sb.AppendLine("                    finally { MetaContext!.Config = _savedConfig; }");
                        sb.AppendLine("                }");
                        sb.AppendLine("            }");

                        // After successful step, currentVersion tracks progress for next step guard.
                        sb.AppendLine("            currentVersion = maxVersion;");
                    }

                    // Call [MetaInit] for services whose config type doesn't appear in any migration
                    // condition — backward-compatible path (they use version-counter logic internally).
                    var handledConfigTypes = new HashSet<string?>(allMigConds.Select(c => c.ConfigTypeFullName));
                    var uncoveredServices = servicesWithInit
                        .Where(s => !handledConfigTypes.Contains(s.ConfigTypeFullName)
                                 && !handledConfigTypes.Contains(null))
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
                    // ComputeRequiredStateSchema — generated lookup table.
                    sb.AppendLine("        private int ComputeRequiredStateSchema()");
                    sb.AppendLine("        {");
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
                            var provField = (cond.ConfigTypeFullName == null || cond.ConfigTypeFullName == configType)
                                ? "_configProvider"
                                : $"_configProvider_{cond.ConfigTypeIdent}";
                            sb.AppendLine($"                var _rcv{ci} = {provField}!.CurrentVersion;");
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

                    // CheckAndRunLazyMigrationAsync — calls InitializeStateAsync if schema must advance.
                    sb.AppendLine("        protected override async System.Threading.Tasks.Task<bool> CheckAndRunLazyMigrationAsync(int? schemaCap = null)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            var required = ComputeRequiredStateSchema();");
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
                    sb.AppendLine("            _migrationCap = schemaCap ?? int.MaxValue;");
                    sb.AppendLine("            try");
                    sb.AppendLine("            {");
                    sb.AppendLine("                await InitializeStateAsync(CurrentStateSchemaVersion);");
                    sb.AppendLine("            }");
                    sb.AppendLine("            finally { _migrationCap = prevCap; }");
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
                    // Uses the same per-step AND-conditions as ComputeRequiredStateSchema but
                    // tests a SUPPLIED config version rather than the provider's CurrentVersion.
                    sb.AppendLine("        public override bool IsClientConfigCompatible(SharedMeta.Core.MetaConfigVersion clientConfigVersion)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            int schema = CurrentStateSchemaVersion;");
                    sb.AppendLine("            if (schema <= 0) return true;");

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

                        sb.AppendLine($"            // Schema {targetSchema} gate");
                        sb.AppendLine($"            if (schema >= {targetSchema})");
                        sb.AppendLine("            {");
                        // For single-config: compare clientConfigVersion directly
                        for (int ci = 0; ci < conds.Count; ci++)
                        {
                            var cond = conds[ci];
                            // Only check conditions for the primary config (clientConfigVersion IS the primary config version)
                            if (cond.ConfigTypeFullName != null && cond.ConfigTypeFullName != configType)
                                continue; // secondary configs: skip for now (single-config gate)
                            sb.AppendLine($"                bool _gc{ci} = clientConfigVersion.Major > {cond.Major} || (clientConfigVersion.Major == {cond.Major} && clientConfigVersion.Minor >= {cond.Minor});");
                            sb.AppendLine($"                if (!_gc{ci}) return false;");
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
                    // Single-config gate only — secondary configs aren't reflected here.
                    var primaryStepGroupsForCap = migStepGroups
                        .Where(g => g.Any(c => c.ConfigTypeFullName == null || c.ConfigTypeFullName == configType))
                        .OrderBy(g => g.Key)
                        .ToList();
                    // Cap baseline: 1 when schema 1 is implicit base init (no explicit
                    // [MetaStateVersion(1, …)]); 0 when schema 1 is gated, because in that case
                    // running base init below the gate would contradict the user's declaration.
                    bool hasExplicitSchemaOneCap = migStepGroups.Any(g => g.Key == 1);
                    int capBaseline = hasExplicitSchemaOneCap ? 0 : 1;

                    sb.AppendLine("        public override int? ComputeSchemaCapForClient(string? clientVersion)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (_configProvider == null) return null;");
                    sb.AppendLine("            var resolved = ResolveClientConfigVersion(clientVersion);");
                    sb.AppendLine($"            int cap = {capBaseline};");
                    foreach (var grp in primaryStepGroupsForCap)
                    {
                        // Use any one primary condition's threshold (group's target shares it).
                        var primaryCond = grp.First(c => c.ConfigTypeFullName == null || c.ConfigTypeFullName == configType);
                        sb.AppendLine($"            if (resolved.Major > {primaryCond.Major} || (resolved.Major == {primaryCond.Major} && resolved.Minor >= {primaryCond.Minor})) cap = System.Math.Max(cap, {grp.Key});");
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
                    // entities (since OnActivateAsync no longer drives it).
                    sb.AppendLine("        protected override async System.Threading.Tasks.Task<bool> CheckAndRunLazyMigrationAsync(int? schemaCap = null)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (CurrentStateSchemaVersion > 0) return false;");
                    sb.AppendLine("            if (schemaCap.HasValue && schemaCap.Value < 1) return false;");
                    sb.AppendLine("            var before = CurrentStateSchemaVersion;");
                    sb.AppendLine("            await InitializeStateAsync(CurrentStateSchemaVersion);");
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

            if (allSkipMigrationMethods.Count > 0)
            {
                sb.AppendLine("        protected override bool ShouldSkipMigration(string serviceName, string methodName)");
                sb.AppendLine("        {");
                sb.AppendLine("            return (serviceName, methodName) switch");
                sb.AppendLine("            {");
                foreach (var m in allSkipMigrationMethods)
                    sb.AppendLine($"                (\"{m.ServiceName}\", \"{m.MethodAlias}\") => true,");
                sb.AppendLine("                _ => false");
                sb.AppendLine("            };");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            if (allMinStateMethods.Count > 0)
            {
                sb.AppendLine("        protected override int? GetMethodMinStateVersion(string serviceName, string methodName)");
                sb.AppendLine("        {");
                sb.AppendLine("            return (serviceName, methodName) switch");
                sb.AppendLine("            {");
                foreach (var m in allMinStateMethods)
                    sb.AppendLine($"                (\"{m.ServiceName}\", \"{m.MethodAlias}\") => {m.MinStateVersion!.Value},");
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

            // Service getter methods
            sb.AppendLine("        // Service getters");
            foreach (var service in services)
            {
                var baseName = GetBaseName(service.InterfaceName);
                var fieldName = GetFieldName(service.InterfaceName);
                sb.AppendLine($"        private {service.InterfaceName} Get{baseName}() => {fieldName} ??= new {service.ImplClassFullName}();");
                if (service.DeepDesync)
                {
                    var ptFieldName = fieldName + "PT";
                    sb.AppendLine($"        private {service.ImplClassFullName}_PatchTracked? {ptFieldName};");
                    sb.AppendLine($"        private {service.InterfaceName} Get{baseName}PatchTracked() => {ptFieldName} ??= new {service.ImplClassFullName}_PatchTracked();");
                }
            }

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
            sb.AppendLine("        private readonly Func<string, string, string, byte[], long, Task<SharedMeta.Server.CrossEntityCallInfo>>? _entityCallHandler;");
            sb.AppendLine();
            sb.AppendLine($"        public {factoryName}(");
            sb.AppendLine("            Func<Type, object>? serviceResolver = null,");
            sb.AppendLine("            Func<string, string, string, byte[], long, Task<SharedMeta.Server.CrossEntityCallInfo>>? entityCallHandler = null)");
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
            List<string> serverDeps)
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
            sb.AppendLine("            // Register config download URL resolver");
            sb.AppendLine("            services.AddSingleton<SharedMeta.Server.Core.IConfigDownloadUrlResolver>(sp => new GeneratedConfigDownloadUrlResolver(sp));");
            sb.AppendLine();
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
            sb.AppendLine("            // Register MetaConnectionHandlerFactory with signature validator + transport options + serializer + schema registry + version policy");
            sb.AppendLine("            services.AddSingleton<SharedMeta.Server.Core.Transport.IMetaConnectionHandlerFactory>(sp =>");
            sb.AppendLine("                new SharedMeta.Server.Core.Transport.MetaConnectionHandlerFactory(");
            sb.AppendLine("                    sp.GetRequiredService<Orleans.IGrainFactory>(),");
            sb.AppendLine("                    sp.GetRequiredService<SharedMeta.Server.Core.Grains.IEntityGrainResolver>(),");
            sb.AppendLine("                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),");
            sb.AppendLine("                    MetaMethodSignatureValidator.ValidateClientSignatures,");
            sb.AppendLine("                    sp.GetService<SharedMeta.Server.Core.Transport.MetaTransportOptions>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Core.IMetaSerializer>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Core.Patch.IPatchSchemaRegistry>(),");
            sb.AppendLine("                    sp.GetService<SharedMeta.Server.Core.Transport.ClientVersionPolicy>()));");
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
        /// Generate IConfigDownloadUrlResolver implementation.
        /// Resolves config download URLs via IMetaConfigProvider instances from DI.
        /// </summary>
        private static void GenerateConfigDownloadUrlResolver(
            StringBuilder sb,
            Dictionary<string, List<ServiceImplInfo>> byStateType)
        {
            // Find state types that have a config type
            var statesWithConfig = byStateType
                .Where(kvp => kvp.Value.Any(s => s.ConfigTypeFullName != null))
                .Select(kvp => new
                {
                    StateTypeFullName = kvp.Key,
                    StateTypeName = kvp.Value.First().StateTypeName,
                    ConfigTypeFullName = kvp.Value.Select(s => s.ConfigTypeFullName).First(c => c != null)!
                })
                .ToList();

            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Generated config download URL resolver.");
            sb.AppendLine("    /// Resolves download URLs via IMetaConfigProvider instances from DI.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public sealed class GeneratedConfigDownloadUrlResolver : SharedMeta.Server.Core.IConfigDownloadUrlResolver");
            sb.AppendLine("    {");

            if (statesWithConfig.Count > 0)
            {
                // Fields for each config provider
                foreach (var entry in statesWithConfig)
                {
                    var fieldName = $"_{char.ToLower(entry.StateTypeName[0])}{entry.StateTypeName.Substring(1)}ConfigProvider";
                    sb.AppendLine($"        private readonly SharedMeta.Server.Core.IMetaConfigProvider<{entry.ConfigTypeFullName}>? {fieldName};");
                }
                sb.AppendLine();

                // Constructor
                sb.AppendLine("        public GeneratedConfigDownloadUrlResolver(System.IServiceProvider sp)");
                sb.AppendLine("        {");
                foreach (var entry in statesWithConfig)
                {
                    var fieldName = $"_{char.ToLower(entry.StateTypeName[0])}{entry.StateTypeName.Substring(1)}ConfigProvider";
                    sb.AppendLine($"            {fieldName} = sp.GetService<SharedMeta.Server.Core.IMetaConfigProvider<{entry.ConfigTypeFullName}>>();");
                }
                sb.AppendLine("        }");
                sb.AppendLine();

                // GetDownloadUrl
                sb.AppendLine("        public string? GetDownloadUrl(string stateTypeName, SharedMeta.Core.MetaConfigVersion version)");
                sb.AppendLine("        {");
                sb.AppendLine("            return stateTypeName switch");
                sb.AppendLine("            {");

                foreach (var entry in statesWithConfig)
                {
                    var fieldName = $"_{char.ToLower(entry.StateTypeName[0])}{entry.StateTypeName.Substring(1)}ConfigProvider";
                    sb.AppendLine($"                \"{entry.StateTypeName}\" or \"{entry.StateTypeFullName}\"");
                    sb.AppendLine($"                    => {fieldName}?.GetDownloadUrl(version),");
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
                sb.AppendLine("        public string? GetDownloadUrl(string stateTypeName, SharedMeta.Core.MetaConfigVersion version) => null;");
            }

            sb.AppendLine("    }");
        }

        /// <summary>
        /// Generate method signature validation class for server-side.
        /// </summary>
        private static void GenerateSignatureValidation(StringBuilder sb, List<ServiceImplInfo> services)
        {
            // Collect all method signatures
            var allSignatures = services
                .SelectMany(s => s.MethodSignatures)
                .ToList();

            if (allSignatures.Count == 0) return;

            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Server-side method signature validation.");
            sb.AppendLine("    /// Validates that client and server have compatible method signatures.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class MetaMethodSignatureValidator");
            sb.AppendLine("    {");

            // Generate server signatures dictionary
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Server method signature hashes.");
            sb.AppendLine("        /// Key: \"ServiceName.MethodAlias\", Value: FNV-1a hash of signature.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static readonly Dictionary<string, ulong> ServerSignatures = new()");
            sb.AppendLine("        {");
            foreach (var sig in allSignatures)
            {
                sb.AppendLine($"            {{ \"{sig.ServiceName}.{sig.MethodAlias}\", {SignatureHashGenerator.FormatHashLiteral(sig.SignatureHash)} }}, // {sig.SignatureString}");
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // Generate validation method
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Validate client method signatures against server signatures.");
            sb.AppendLine("        /// Returns null if all signatures match, or list of mismatches.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <param name=\"clientSignatures\">Client's method signature hashes.</param>");
            sb.AppendLine("        /// <returns>List of mismatches (null if all match).</returns>");
            sb.AppendLine("        public static List<string>? ValidateClientSignatures(Dictionary<string, ulong> clientSignatures)");
            sb.AppendLine("        {");
            sb.AppendLine("            var mismatches = new List<string>();");
            sb.AppendLine();
            sb.AppendLine("            foreach (var (methodKey, clientHash) in clientSignatures)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (ServerSignatures.TryGetValue(methodKey, out var serverHash))");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (clientHash != serverHash)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        mismatches.Add($\"{methodKey}: signature mismatch (client=0x{clientHash:X16}, server=0x{serverHash:X16})\");");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            sb.AppendLine("                    mismatches.Add($\"{methodKey}: method not found on server\");");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            // Check for server methods not present on client");
            sb.AppendLine("            foreach (var (methodKey, _) in ServerSignatures)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (!clientSignatures.ContainsKey(methodKey))");
            sb.AppendLine("                {");
            sb.AppendLine("                    mismatches.Add($\"{methodKey}: method not found on client\");");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return mismatches.Count > 0 ? mismatches : null;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
    }
}
