using System.Text;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Information about a method signature for hash validation.
    /// </summary>
    public class MethodSignatureInfo
    {
        public string ServiceName { get; set; } = "";
        public string MethodAlias { get; set; } = "";
        public string SignatureString { get; set; } = "";
        public ulong SignatureHash { get; set; }
        public bool IsQuery { get; set; }
        public bool IsOpenAccess { get; set; }

        /// <summary>True if <c>[MetaMethod(Signal = true)]</c> — fire-and-forget void.</summary>
        public bool IsSignal { get; set; }

        /// <summary>True if the method carries <c>[NoMigrate]</c>.</summary>
        public bool SkipMigration { get; set; }

        /// <summary>Schema cap declared via <c>[MinStateVersion(N)]</c>; null when uncapped.</summary>
        public int? MinStateVersion { get; set; }

        /// <summary>
        /// Mirror of <c>[MetaMethod(GenerateClientApi = ...)]</c>. Default true.
        /// When false: client API is not generated AND direct client-originated RPC is rejected
        /// at the EntityGrain entry points (HandleCallAsync / HandleQueryAsync / HandleSignalAsync)
        /// via the generated <c>IsClientCallable</c> override. Cross-entity and sibling calls
        /// remain available because they don't traverse the client-RPC boundary.
        /// </summary>
        public bool GenerateClientApi { get; set; } = true;

        /// <summary>0.22.0+: <c>[MetaMethod(Version = N)]</c>. Default 0 (legacy/unversioned).</summary>
        public int Version { get; set; }

        /// <summary>0.22.0+: <c>[MetaMethod(MinCompatibleVersion = N)]</c>. Default 0 — no
        /// compatibility floor (any client version may call this method body locally).</summary>
        public int MinCompatibleVersion { get; set; }

        /// <summary>0.22.0+: FNV-1a hash over canonical parameter-type sequence ("p1,p2,...->R").
        /// Used by the Stage 6 compute pipeline to detect signature drift even when
        /// <c>(Alias, Version)</c> happens to match. Distinct from <see cref="SignatureHash"/>,
        /// which is per-fully-qualified-method (includes service + method names).</summary>
        public ulong ArgHash { get; set; }

        /// <summary>0.22.0+ Fully-qualified config class this method's service is bound to.
        /// Mirrors <see cref="DiscoveredServiceInfo.ConfigTypeFullName"/>. Used by
        /// <c>EntityGrain</c>'s per-entity boundary compute to find services affected by a
        /// <c>[MetaConfigStructureBoundary]</c> trigger.</summary>
        public string ConfigTypeFullName { get; set; } = "";
    }

    /// <summary>
    /// Information about a discovered service for the GameServiceDiscovery generator.
    /// </summary>
    public class DiscoveredServiceInfo
    {
        public string InterfaceName { get; set; } = "";
        public string InterfaceFullName { get; set; } = "";
        public string? StateTypeName { get; set; }
        public string? StateTypeFullName { get; set; }
        public string Namespace { get; set; } = "";
        public List<string> SubscriberInterfaces { get; set; } = new();
        public List<MethodSignatureInfo> MethodSignatures { get; set; } = new();

        /// <summary>0.22.0+ Fully-qualified config class bound to this service via
        /// <c>[MetaService(ConfigType = X)]</c>. Empty if no config (or default-config
        /// resolution not implemented yet at this point in the pipeline). Used by
        /// <c>ServerMethodEntry.ConfigTypeFullName</c> emit so EntityGrain can map
        /// config-boundary triggers back to affected services.</summary>
        public string ConfigTypeFullName { get; set; } = "";

        /// <summary>0.22.0+ <c>[MetaConfigStructureBoundary]</c> declarations harvested from
        /// the service's bound config class (when one is resolvable from <c>[MetaService]</c>
        /// attribute args). Each entry contributes a row to <c>MetaServerSignature.ConfigBoundaries</c>.</summary>
        public List<ConfigBoundaryInfo> ConfigBoundaries { get; set; } = new();
    }

    /// <summary>
    /// 0.22.0+ generator-side mirror of <c>SharedMeta.Server.Core.Session.ConfigBoundaryEntry</c>.
    /// Lives in the generator project to avoid coupling generator code to the server runtime;
    /// projected into the emitted code via the standard <c>global::</c> path.
    /// </summary>
    public class ConfigBoundaryInfo
    {
        public string ConfigTypeFullName { get; set; } = "";
        public string MinConfigVersion { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Generates GameServiceDiscovery abstract class that provides:
    /// - Service name to state type mapping
    /// - State type name to Type resolution
    /// - Centralized service metadata for SessionManager and EntityGrain
    /// </summary>
    public static class GameServiceDiscoveryGenerator
    {
        /// <summary>
        /// Analyze a [MetaService] interface and extract info for discovery.
        /// </summary>
        public static DiscoveredServiceInfo? Analyze(INamedTypeSymbol symbol)
        {
            // Check for [MetaService] attribute
            var attr = symbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceAttribute");
            if (attr == null) return null;

            var info = new DiscoveredServiceInfo
            {
                InterfaceName = symbol.Name,
                InterfaceFullName = symbol.ToDisplayString(),
                Namespace = symbol.ContainingNamespace.ToDisplayString()
            };

            // Get state type from attribute
            var stateTypeArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "StateType");
            if (stateTypeArg.Value.Value is INamedTypeSymbol stateType)
            {
                info.StateTypeName = stateType.Name;
                info.StateTypeFullName = stateType.ToDisplayString();
            }

            // 0.22.0+: harvest [MetaConfigStructureBoundary] from the service's bound config
            // class. Used for force-ServerPatch decisions when a client's resolved config
            // sits below the declared structural break.
            var configTypeArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "ConfigType");
            if (configTypeArg.Value.Value is INamedTypeSymbol configType)
            {
                info.ConfigTypeFullName = configType.ToDisplayString();
                foreach (var boundary in configType.GetAttributes()
                    .Where(a => a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaConfigStructureBoundaryAttribute"))
                {
                    var minVer = boundary.ConstructorArguments.Length > 0
                        ? boundary.ConstructorArguments[0].Value as string ?? ""
                        : "";
                    var reasonArg = boundary.NamedArguments.FirstOrDefault(a => a.Key == "Reason");
                    var reason = !reasonArg.Value.IsNull && reasonArg.Value.Value is string r ? r : "";
                    info.ConfigBoundaries.Add(new ConfigBoundaryInfo
                    {
                        ConfigTypeFullName = configType.ToDisplayString(),
                        MinConfigVersion = minVer,
                        Reason = reason,
                    });
                }
            }

            // Get subscriber interfaces
            var subscriberInterfacesArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "SubscriberInterfaces");
            if (!subscriberInterfacesArg.Value.IsNull && subscriberInterfacesArg.Value.Values.Length > 0)
            {
                foreach (var val in subscriberInterfacesArg.Value.Values)
                {
                    if (val.Value is INamedTypeSymbol subscriberType)
                    {
                        info.SubscriberInterfaces.Add(subscriberType.ToDisplayString());
                    }
                }
            }

            // Collect method signatures for hash validation
            foreach (var member in symbol.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary) continue;

                // Get method alias + 0.22.0 versioning args from [MetaMethod] attribute
                var metaMethodAttr = member.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaMethodAttribute");

                var methodAlias = member.Name;
                int methodVersion = 0;
                int minCompatibleVersion = 0;
                bool generateClientApi = true;
                if (metaMethodAttr != null)
                {
                    var aliasArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Alias");
                    if (!aliasArg.Value.IsNull && aliasArg.Value.Value is string alias && !string.IsNullOrEmpty(alias))
                    {
                        methodAlias = alias;
                    }
                    var versionArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "Version");
                    if (!versionArg.Value.IsNull && versionArg.Value.Value is int v) methodVersion = v;
                    var minCompatArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "MinCompatibleVersion");
                    if (!minCompatArg.Value.IsNull && minCompatArg.Value.Value is int mcv) minCompatibleVersion = mcv;
                    var genApiArg = metaMethodAttr.NamedArguments.FirstOrDefault(a => a.Key == "GenerateClientApi");
                    if (!genApiArg.Value.IsNull && genApiArg.Value.Value is bool g) generateClientApi = g;
                }

                var signatureString = SignatureHashGenerator.BuildSignatureString(info.InterfaceName, methodAlias, member);
                var signatureHash = SignatureHashGenerator.ComputeFnv1aHash(signatureString);

                // ArgHash — purely the parameter+return shape, no service/method name. Used by
                // Stage 6 compute to detect Case 4 (signature drift) even when alias matches.
                var argShape = string.Join(",", member.Parameters.Select(p =>
                    SignatureHashGenerator.GetCanonicalTypeName(p.Type)))
                    + "->" + SignatureHashGenerator.GetCanonicalTypeName(member.ReturnType);
                var argHash = SignatureHashGenerator.ComputeFnv1aHash(argShape);

                info.MethodSignatures.Add(new MethodSignatureInfo
                {
                    ServiceName = info.InterfaceName,
                    MethodAlias = methodAlias,
                    SignatureString = signatureString,
                    SignatureHash = signatureHash,
                    Version = methodVersion,
                    MinCompatibleVersion = minCompatibleVersion,
                    ArgHash = argHash,
                    GenerateClientApi = generateClientApi,
                    ConfigTypeFullName = info.ConfigTypeFullName,
                });
            }

            return info;
        }

        /// <summary>
        /// Generate the GameServiceDiscovery abstract class and concrete implementations.
        /// </summary>
        public static string Generate(string rootNamespace, IEnumerable<DiscoveredServiceInfo> services)
        {
            var serviceList = services.Where(s => s.StateTypeFullName != null).ToList();
            if (serviceList.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();

            // Collect all unique namespaces
            var namespaces = serviceList
                .Select(s => s.Namespace)
                .Distinct()
                .OrderBy(n => n);
            foreach (var ns in namespaces)
            {
                if (ns != rootNamespace)
                {
                    sb.AppendLine($"using {ns};");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"namespace {rootNamespace}");
            sb.AppendLine("{");

            // Generate abstract base class
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Base class for game service discovery.");
            sb.AppendLine("    /// Provides service name to state type mapping and other metadata.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public abstract partial class GameServiceDiscoveryBase");
            sb.AppendLine("    {");

            // State type dictionary
            sb.AppendLine("        /// <summary>Maps service interface name to state type.</summary>");
            sb.AppendLine("        protected static readonly Dictionary<string, Type> _serviceToStateType = new()");
            sb.AppendLine("        {");
            foreach (var service in serviceList)
            {
                sb.AppendLine($"            {{ \"{service.InterfaceName}\", typeof({service.StateTypeFullName}) }},");
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // State type name to type dictionary — deduplicate by state full name so multiple
            // services on the same state don't emit duplicate dictionary entries (cctor throw).
            sb.AppendLine("        /// <summary>Maps state type name (simple or full) to Type.</summary>");
            sb.AppendLine("        protected static readonly Dictionary<string, Type> _stateTypeByName = new()");
            sb.AppendLine("        {");
            foreach (var service in serviceList.GroupBy(s => s.StateTypeFullName).Select(g => g.First()))
            {
                sb.AppendLine($"            {{ \"{service.StateTypeName}\", typeof({service.StateTypeFullName}) }},");
                if (service.StateTypeName != service.StateTypeFullName)
                {
                    sb.AppendLine($"            {{ \"{service.StateTypeFullName}\", typeof({service.StateTypeFullName}) }},");
                }
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // Service interface type dictionary
            sb.AppendLine("        /// <summary>Maps service interface name to interface type.</summary>");
            sb.AppendLine("        protected static readonly Dictionary<string, Type> _serviceInterfaceTypes = new()");
            sb.AppendLine("        {");
            foreach (var service in serviceList)
            {
                sb.AppendLine($"            {{ \"{service.InterfaceName}\", typeof({service.InterfaceFullName}) }},");
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // All state types list
            sb.AppendLine("        /// <summary>All registered state types.</summary>");
            sb.AppendLine("        public static IReadOnlyList<Type> AllStateTypes { get; } = new Type[]");
            sb.AppendLine("        {");
            var stateTypes = serviceList.Select(s => s.StateTypeFullName).Distinct().ToList();
            foreach (var stateType in stateTypes)
            {
                sb.AppendLine($"            typeof({stateType}),");
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // Methods
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Get the state type for a service interface.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static Type? GetStateTypeForService(string serviceName)");
            sb.AppendLine("        {");
            sb.AppendLine("            return _serviceToStateType.TryGetValue(serviceName, out var type) ? type : null;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Get a state type by its name (simple or fully qualified).");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static Type? GetStateType(string stateTypeName)");
            sb.AppendLine("        {");
            sb.AppendLine("            return _stateTypeByName.TryGetValue(stateTypeName, out var type) ? type : null;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Get the service interface type by name.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static Type? GetServiceInterfaceType(string serviceName)");
            sb.AppendLine("        {");
            sb.AppendLine("            return _serviceInterfaceTypes.TryGetValue(serviceName, out var type) ? type : null;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Check if a state type is registered.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static bool IsKnownStateType(string stateTypeName)");
            sb.AppendLine("        {");
            sb.AppendLine("            return _stateTypeByName.ContainsKey(stateTypeName);");
            sb.AppendLine("        }");
            sb.AppendLine();

            // 0.24.0+ Emit the per-service signatures consumed by the 0.22+ negotiation flow.
            // Legacy "MethodSignatures dictionary + GetMethodSignatures()" emit was removed —
            // SessionConnectRequest no longer carries that field; the validator path was dead.
            EmitClientSignature(sb, serviceList.SelectMany(s => s.MethodSignatures).ToList(), serviceList, rootNamespace);
            sb.AppendLine();

            // Abstract method for getting dispatcher (to be implemented in server project)
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Get the server dispatcher for a service.");
            sb.AppendLine("        /// Override in server implementation.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public abstract ServerDispatcher? GetDispatcher(string serviceName);");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Get the subscriber dispatcher for a service.");
            sb.AppendLine("        /// Override in server implementation.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public abstract SubscriberDispatcher? GetSubscriberDispatcher(string serviceName);");
            sb.AppendLine();

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Create a new instance of the service implementation.");
            sb.AppendLine("        /// Override in server implementation.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public abstract object CreateService(string serviceName);");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Generate delegate types
            sb.AppendLine("    /// <summary>Delegate for dispatching RPC calls to services.</summary>");
            sb.AppendLine("    /// <remarks>0.24.0+: <c>methodId</c> is the server-side global index from");
            sb.AppendLine("    /// <see cref=\"global::SharedMeta.Generated.GameMethodIds\"/>. Encoding (alias, version)");
            sb.AppendLine("    /// into a single ushort eliminates the per-call string switch and the legacy");
            sb.AppendLine("    /// nested methodVersion subroute.</remarks>");
            sb.AppendLine("    public delegate System.Threading.Tasks.ValueTask<DispatchResult> ServerDispatcher(");
            sb.AppendLine("        object service, ushort methodId, byte[] payload, IMetaSerializer serializer);");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Delegate for dispatching subscriber events to services.</summary>");
            sb.AppendLine("    /// <remarks>0.24.0+: <c>methodId</c> is the framework subscriber method id from");
            sb.AppendLine("    /// <see cref=\"global::SharedMeta.Core.Framework.FrameworkMethodIds\"/>.</remarks>");
            sb.AppendLine("    public delegate System.Collections.Generic.List<(string serviceName, string methodName, byte[]? resultBytes)>? SubscriberDispatcher(");
            sb.AppendLine("        object service, ushort methodId, byte[] eventData, IMetaSerializer serializer);");

            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// 0.22.0: Emits a <c>public static readonly MetaClientSignature ClientSignature</c>
        /// on <c>GameServiceDiscoveryBase</c>. The constant is what the client transmits in
        /// <c>SessionConnectRequest.ClientSignatureHash</c> (phase-1) and inside
        /// <c>RegisterClientSignatureRequest.Signature</c> (phase-2). Identical bits across
        /// every client build that ships the same set of <c>[MetaMethod]</c> declarations.
        /// </summary>
        private static void EmitClientSignature(StringBuilder sb, List<MethodSignatureInfo> allSignatures, List<DiscoveredServiceInfo> services, string rootNamespace)
        {
            // Sort by (ServiceName, Alias, Version) for canonical form. Signal/query methods are
            // included — they're still part of the client's protocol surface and the server may
            // reject them too (e.g. structural break to a signal arg shape).
            var sorted = allSignatures
                .OrderBy(s => s.ServiceName, System.StringComparer.Ordinal)
                .ThenBy(s => s.MethodAlias, System.StringComparer.Ordinal)
                .ThenBy(s => s.Version)
                .ToList();

            // Compute the aggregate signature hash from the sorted canonical string. The string
            // includes service.alias@version#argHash for each method, joined by '|'. FNV-1a over
            // the whole thing keeps the hash deterministic but tiny (8 bytes on the wire).
            var canonicalSb = new StringBuilder();
            foreach (var s in sorted)
            {
                if (canonicalSb.Length > 0) canonicalSb.Append('|');
                canonicalSb.Append(s.ServiceName).Append('.').Append(s.MethodAlias)
                    .Append('@').Append(s.Version)
                    .Append('#').Append(s.ArgHash.ToString("X16"));
            }
            var signatureHash = SignatureHashGenerator.ComputeFnv1aHash(canonicalSb.ToString());

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// 0.22.0+: Compile-time client signature. Stable across builds that ship the");
            sb.AppendLine("        /// same set of <c>[MetaMethod]</c> declarations under identical (Alias, Version)");
            sb.AppendLine("        /// tuples and parameter shapes. The hash drives the SessionConnect compatibility");
            sb.AppendLine("        /// handshake — see <c>SessionConnectRequest.ClientSignatureHash</c>.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static readonly global::SharedMeta.Core.Transport.MetaClientSignature ClientSignature =");
            sb.AppendLine("            new global::SharedMeta.Core.Transport.MetaClientSignature");
            sb.AppendLine("            {");
            sb.AppendLine($"                SignatureHash = {SignatureHashGenerator.FormatHashLiteral(signatureHash)},");
            sb.AppendLine("                KnownMethods = new System.Collections.Generic.List<global::SharedMeta.Core.Transport.KnownMethodEntry>");
            sb.AppendLine("                {");
            // Client-side global index — stable per client build, canonical sort order.
            // Used as wire identifier in RpcCall.MethodId; server translates to its own
            // server-side index via the per-signature clientToServer map.
            ushort cIdx = 0;
            foreach (var s in sorted)
            {
                sb.AppendLine("                    new global::SharedMeta.Core.Transport.KnownMethodEntry");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        ServiceName = \"{s.ServiceName}\",");
                sb.AppendLine($"                        Alias = \"{s.MethodAlias}\",");
                sb.AppendLine($"                        Version = {s.Version},");
                sb.AppendLine($"                        ArgHash = {SignatureHashGenerator.FormatHashLiteral(s.ArgHash)},");
                sb.AppendLine($"                        GlobalIndex = {cIdx},");
                sb.AppendLine("                    },");
                cIdx++;
            }
            sb.AppendLine("                },");
            sb.AppendLine("            };");
            sb.AppendLine();

            // 0.24.0+ Emit flat constants in {rootNamespace}.Generated.GameMethodIds. The
            // per-assembly namespace is required because each assembly that runs this generator
            // produces its own table (ids assigned in canonical sort order over THAT assembly's
            // [MetaMethod] declarations) — a shared SharedMeta.Generated namespace would clash
            // whenever a project references two such assemblies. Cross-assembly consumers
            // qualify the reference by the owning assembly's root namespace; same-assembly
            // generated code (signature emit + per-service dispatcher / apiclient / recorder)
            // computes the same namespace from the service interface's containing namespace.
            sb.AppendLine("    }   // close GameServiceDiscoveryBase");
            sb.AppendLine("}   // close rootNamespace");
            sb.AppendLine();
            sb.AppendLine($"namespace {rootNamespace}.Generated");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Compile-time table of client-side method ids. Each constant equals the");
            sb.AppendLine("    /// method's <c>GlobalIndex</c> in the client signature's KnownMethods.");
            sb.AppendLine("    /// Generated client code passes these into <c>RpcCall.MethodId</c> on the wire.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class GameMethodIds");
            sb.AppendLine("    {");
            ushort kIdx = 0;
            foreach (var s in sorted)
            {
                var safeName = SignatureHashGenerator.MakeMethodIdConstName(s.ServiceName, s.MethodAlias, s.Version);
                sb.AppendLine($"        public const ushort {safeName} = {kIdx};");
                kIdx++;
            }
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            // Re-open rootNamespace + GameServiceDiscoveryBase so the server signature emit
            // below continues to live inside GameServiceDiscoveryBase as before.
            sb.AppendLine($"namespace {rootNamespace}");
            sb.AppendLine("{");
            sb.AppendLine("    public abstract partial class GameServiceDiscoveryBase");
            sb.AppendLine("    {");

            // Server-side mirror — same data plus MinCompatibleVersion + GenerateClientApi.
            // Lives in SharedMeta.Core.Transport (shared namespace) so the same generated
            // code compiles on client, server, and Unity — no preprocessor gating needed.
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// 0.22.0+: Server-side mirror of the same protocol surface, enriched with");
            sb.AppendLine("        /// <c>MinCompatibleVersion</c> and <c>GenerateClientApi</c> per method, plus");
            sb.AppendLine("        /// <c>[MetaConfigStructureBoundary]</c> declarations harvested from every bound");
            sb.AppendLine("        /// config class. Consumed by the Stage 6 capabilities compute pipeline.");
            sb.AppendLine("        /// </summary>");
            // 0.24.0+ Server signature hash: canonical string includes per-method server-only
            // fields (MinCompatibleVersion, GenerateClientApi, ConfigTypeFullName) plus the
            // config-boundary tuples. Any of these changing across server builds invalidates the
            // client-side ClientSignatureAnnotated cache via SessionConnectResponse.ServerSignatureHash.
            var serverCanonicalSb = new StringBuilder();
            foreach (var s in sorted)
            {
                if (serverCanonicalSb.Length > 0) serverCanonicalSb.Append('|');
                serverCanonicalSb.Append(s.ServiceName).Append('.').Append(s.MethodAlias)
                    .Append('@').Append(s.Version)
                    .Append('#').Append(s.ArgHash.ToString("X16"))
                    .Append('!').Append(s.MinCompatibleVersion)
                    .Append('?').Append(s.GenerateClientApi ? '1' : '0')
                    .Append('~').Append(s.ConfigTypeFullName ?? "");
            }
            // Boundary tuples folded in after method list, sorted same as we emit them below.
            var serverBoundariesForHash = services
                .SelectMany(s => s.ConfigBoundaries)
                .GroupBy(b => (b.ConfigTypeFullName, b.MinConfigVersion))
                .Select(g => g.First())
                .OrderBy(b => b.ConfigTypeFullName, System.StringComparer.Ordinal)
                .ThenBy(b => b.MinConfigVersion, System.StringComparer.Ordinal)
                .ToList();
            serverCanonicalSb.Append("||boundaries=");
            foreach (var b in serverBoundariesForHash)
                serverCanonicalSb.Append(b.ConfigTypeFullName).Append('@').Append(b.MinConfigVersion).Append(';');
            var serverSignatureHash = SignatureHashGenerator.ComputeFnv1aHash(serverCanonicalSb.ToString());

            sb.AppendLine("        public static readonly global::SharedMeta.Core.Transport.MetaServerSignature ServerSignature =");
            sb.AppendLine("            new global::SharedMeta.Core.Transport.MetaServerSignature");
            sb.AppendLine("            {");
            sb.AppendLine($"                SignatureHash = {SignatureHashGenerator.FormatHashLiteral(serverSignatureHash)},");
            sb.AppendLine("                Methods = new System.Collections.Generic.List<global::SharedMeta.Core.Transport.ServerMethodEntry>");
            sb.AppendLine("                {");
            // Global index assigned in canonical sort order — stable per server build.
            // Used as the server-side dispatch key (jump table on ushort) and as the
            // wire identifier after capability negotiation translates client local ids.
            ushort gIdx = 0;
            foreach (var s in sorted)
            {
                sb.AppendLine("                    new global::SharedMeta.Core.Transport.ServerMethodEntry");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        ServiceName = \"{s.ServiceName}\",");
                sb.AppendLine($"                        Alias = \"{s.MethodAlias}\",");
                sb.AppendLine($"                        Version = {s.Version},");
                sb.AppendLine($"                        MinCompatibleVersion = {s.MinCompatibleVersion},");
                sb.AppendLine($"                        ArgHash = {SignatureHashGenerator.FormatHashLiteral(s.ArgHash)},");
                sb.AppendLine($"                        GenerateClientApi = {(s.GenerateClientApi ? "true" : "false")},");
                sb.AppendLine($"                        ConfigTypeFullName = \"{s.ConfigTypeFullName}\",");
                sb.AppendLine($"                        GlobalIndex = {gIdx},");
                sb.AppendLine("                    },");
                gIdx++;
            }
            sb.AppendLine("                },");

            // Collapse boundary entries to (ConfigTypeFullName, MinConfigVersion, Reason) tuples.
            // Same config may appear in multiple services — emit each unique tuple once.
            var uniqueBoundaries = services
                .SelectMany(s => s.ConfigBoundaries)
                .GroupBy(b => (b.ConfigTypeFullName, b.MinConfigVersion))
                .Select(g => g.First())
                .OrderBy(b => b.ConfigTypeFullName, System.StringComparer.Ordinal)
                .ThenBy(b => b.MinConfigVersion, System.StringComparer.Ordinal)
                .ToList();
            sb.AppendLine("                ConfigBoundaries = new System.Collections.Generic.List<global::SharedMeta.Core.Transport.ConfigBoundaryEntry>");
            sb.AppendLine("                {");
            foreach (var b in uniqueBoundaries)
            {
                sb.AppendLine("                    new global::SharedMeta.Core.Transport.ConfigBoundaryEntry");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        ConfigTypeFullName = \"{b.ConfigTypeFullName}\",");
                sb.AppendLine($"                        MinConfigVersion = \"{b.MinConfigVersion}\",");
                sb.AppendLine($"                        Reason = {EscapeLiteral(b.Reason)},");
                sb.AppendLine("                    },");
            }
            sb.AppendLine("                },");
            sb.AppendLine("            };");
        }

        /// <summary>Escape a string for safe embedding as a C# string literal.</summary>
        private static string EscapeLiteral(string s)
        {
            if (s == null) return "\"\"";
            var escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
            return "\"" + escaped + "\"";
        }

        // Const-name builder lives on SignatureHashGenerator so other generators can produce
        // matching identifiers against GameMethodIds. See SignatureHashGenerator.MakeMethodIdConstName.
        private static string MakeSafeConstName(string serviceName, string methodAlias, int version)
            => SignatureHashGenerator.MakeMethodIdConstName(serviceName, methodAlias, version);
    }
}
