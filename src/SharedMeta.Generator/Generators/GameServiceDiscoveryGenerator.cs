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

                var signatureString = SignatureHashGenerator.BuildSignatureString(info.InterfaceName, methodAlias, member);
                var signatureHash = SignatureHashGenerator.ComputeFnv1aHash(signatureString);

                info.MethodSignatures.Add(new MethodSignatureInfo
                {
                    ServiceName = info.InterfaceName,
                    MethodAlias = methodAlias,
                    SignatureString = signatureString,
                    SignatureHash = signatureHash
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
            sb.AppendLine("    public abstract class GameServiceDiscoveryBase");
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

            // State type name to type dictionary
            sb.AppendLine("        /// <summary>Maps state type name (simple or full) to Type.</summary>");
            sb.AppendLine("        protected static readonly Dictionary<string, Type> _stateTypeByName = new()");
            sb.AppendLine("        {");
            foreach (var service in serviceList)
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

            // Generate method signatures dictionary
            GenerateMethodSignatures(sb, serviceList);
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
            sb.AppendLine("    public delegate System.Threading.Tasks.Task<DispatchResult> ServerDispatcher(");
            sb.AppendLine("        object service, string methodName, byte[] payload, IMetaSerializer serializer);");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Delegate for dispatching subscriber events to services.</summary>");
            sb.AppendLine("    public delegate System.Collections.Generic.List<(string serviceName, string methodName, byte[]? resultBytes)>? SubscriberDispatcher(");
            sb.AppendLine("        object service, string subscriberInterface, string methodName, byte[] eventData, IMetaSerializer serializer);");

            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generate method signatures dictionary for client-server validation.
        /// </summary>
        private static void GenerateMethodSignatures(StringBuilder sb, List<DiscoveredServiceInfo> services)
        {
            // Collect all method signatures
            var allSignatures = services
                .SelectMany(s => s.MethodSignatures)
                .ToList();

            if (allSignatures.Count == 0) return;

            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Method signature hashes for client-server compatibility validation.");
            sb.AppendLine("        /// Key: \"ServiceName.MethodAlias\", Value: FNV-1a hash of signature.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static readonly Dictionary<string, ulong> MethodSignatures = new()");
            sb.AppendLine("        {");
            foreach (var sig in allSignatures)
            {
                sb.AppendLine($"            {{ \"{sig.ServiceName}.{sig.MethodAlias}\", {SignatureHashGenerator.FormatHashLiteral(sig.SignatureHash)} }}, // {sig.SignatureString}");
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // Generate GetMethodSignatures method for easy access
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Get all method signatures for session connect validation.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        public static Dictionary<string, ulong> GetMethodSignatures() => new(MethodSignatures);");
        }
    }
}
