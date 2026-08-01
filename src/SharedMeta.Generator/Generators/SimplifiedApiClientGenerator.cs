using System.Text;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharedMeta.Generator.Utilities;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Generates simplified ApiClient with:
    /// - INetwork for transport (not IEntityTransport)
    /// - IExecutionModeProvider for runtime mode switching
    /// - Dual-mode methods (_Server and _Optimistic variants)
    /// - SetContext/ClearContext pattern
    /// - Validation for return values
    /// </summary>
    public static class SimplifiedApiClientGenerator
    {
        public static string? Generate(InterfaceDeclarationSyntax node, INamedTypeSymbol symbol, Compilation? compilation = null)
        {
            // Check for [MetaService] attribute
            var attr = symbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceAttribute");
            if (attr == null) return null;

            // 0.22.0 opt-out: assembly-level [SharedMetaCompatibilityOptions(Enabled = false)]
            // suppresses the capabilities gate emit. Default = enabled (no attribute) preserves
            // the negotiation surface; the runtime stays no-op until the consumer wires the registry.
            bool capabilitiesEnabled = compilation == null
                || IsCompatibilityNegotiationEnabled(compilation);

            var interfaceName = symbol.Name;
            var namespaceName = symbol.ContainingNamespace.ToDisplayString();

            // Get state type from attribute
            var stateTypeArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "StateType");
            string? stateTypeName = null;
            string? stateTypeShortName = null;
            if (!stateTypeArg.Value.IsNull && stateTypeArg.Value.Value is INamedTypeSymbol stateType)
            {
                stateTypeName = stateType.ToDisplayString();
                stateTypeShortName = stateType.Name;
            }

            if (stateTypeName == null)
            {
                // Try to infer from interface name (IProfileService -> ProfileState)
                var baseName = interfaceName.StartsWith("I") ? interfaceName.Substring(1) : interfaceName;
                if (baseName.EndsWith("Service"))
                {
                    stateTypeName = namespaceName + "." + baseName.Substring(0, baseName.Length - 7) + "State";
                }
            }

            // 0.33.0+ Resolve the legacy ConfigType (explicit or [MetaService(DefaultConfig=true)])
            // and the declared [ServiceConfig] entries so the generated ApiClient can expose typed
            // Config / named accessors directly on the instance — sidesteps
            // IMetaServiceResolver.GetEntityConfig<TConfig>(entityId)'s ambiguity question entirely
            // for the common "I already have my api, give me its config" case.
            string? apiConfigTypeName = null;
            var apiConfigTypeArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "ConfigType");
            if (!apiConfigTypeArg.Value.IsNull && apiConfigTypeArg.Value.Value is INamedTypeSymbol apiConfigTypeSymbol)
            {
                apiConfigTypeName = apiConfigTypeSymbol.ToDisplayString();
            }
            else
            {
                var apiDefaultConfigArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "DefaultConfig");
                if (!apiDefaultConfigArg.Value.IsNull && apiDefaultConfigArg.Value.Value is true && compilation != null)
                {
                    apiConfigTypeName = FindDefaultConfigType(compilation);
                }
            }

            var apiServiceConfigs = symbol.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.ServiceConfigAttribute")
                .Select(a => (
                    Type: a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as INamedTypeSymbol : null,
                    Name: a.ConstructorArguments.Length > 1 ? a.ConstructorArguments[1].Value as string : null))
                .Where(e => e.Type != null && !string.IsNullOrEmpty(e.Name))
                .ToList();

            // Remove leading 'I' for class names
            var baseName2 = interfaceName;
            if (baseName2.StartsWith("I") && baseName2.Length > 1 && char.IsUpper(baseName2[1]))
            {
                baseName2 = baseName2.Substring(1);
            }

            var implClassName = baseName2;
            var patchTrackedClassName = implClassName + "_PatchTracked";

            // Check if implementation has DeepDesync = true
            bool hasDeepDesync = false;
            if (compilation != null)
            {
                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    var root = syntaxTree.GetRoot();
                    foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        var model = compilation.GetSemanticModel(syntaxTree);
                        var classSymbol = model.GetDeclaredSymbol(classDecl);
                        if (classSymbol == null) continue;
                        var implAttr = classSymbol.GetAttributes().FirstOrDefault(a =>
                            a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceImplAttribute");
                        if (implAttr == null) continue;
                        // Check interface match
                        if (implAttr.ConstructorArguments.Length >= 1 &&
                            implAttr.ConstructorArguments[0].Value is INamedTypeSymbol ifaceType &&
                            ifaceType.Name == interfaceName)
                        {
                            var ddArg = implAttr.NamedArguments.FirstOrDefault(a => a.Key == "DeepDesync");
                            hasDeepDesync = !ddArg.Value.IsNull && ddArg.Value.Value is true;
                            break;
                        }
                    }
                    if (hasDeepDesync) break;
                }
            }

            var methods = node.Members.OfType<MethodDeclarationSyntax>().ToList();

            // Methods whose contract is inherited from a base interface have no declaration on this
            // node; their [MetaMethod] sits on the implementing class. The client still needs them —
            // it replays their broadcasts to keep local state in sync.
            if (compilation != null)
                methods.AddRange(ImplDeclaredMethods.SyntaxForService(symbol, compilation));

            // Detect serializer type
            var serializer = compilation != null ? SerializerDetector.Detect(compilation) : DetectedSerializer.Generic;

            // Scan compilation for IMetaResultComparer<T> implementations. Build a per-method
            // resolution map and a set of ambiguity diagnostics. The ambiguity diagnostics are
            // emitted as `#error` directives at the top of the generated file so the compile fails
            // with the affected types named, instead of silently picking one.
            var methodComparers = new Dictionary<MethodDeclarationSyntax, ResultComparerInfo?>();
            var usedComparers = new Dictionary<string, ResultComparerInfo>();
            var ambiguityErrors = new List<string>();
            if (compilation != null)
            {
                var allComparers = ResultComparerScanner.Scan(compilation);
                foreach (var method in methods)
                {
                    var returnTypeFullName = ResolveReturnTypeFullName(method, compilation);
                    if (returnTypeFullName == null) continue;
                    if (!allComparers.TryGetValue(returnTypeFullName, out var candidates) || candidates.Count == 0)
                        continue;

                    var winner = ResultComparerScanner.ResolveWinner(candidates);
                    if (winner == null)
                    {
                        var names = string.Join(", ", candidates.Select(c => $"{c.ComparerFullName} (Priority={c.Priority})"));
                        ambiguityErrors.Add(
                            $"#error SharedMeta: multiple IMetaResultComparer<{returnTypeFullName}> implementations found with the same priority — set [ResultComparer(Priority = N)] on one of them, or [ResultComparer(NoAutoRegister = true)] to opt out. Candidates: {names}.");
                        continue;
                    }
                    methodComparers[method] = winner;
                    usedComparers[returnTypeFullName] = winner;
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#pragma warning disable CS1998, CS1522");
            sb.AppendLine("#nullable enable");
            foreach (var err in ambiguityErrors)
                sb.AppendLine(err);
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Packets;");
            sb.AppendLine("using SharedMeta.Core.Network;");
            sb.AppendLine("using SharedMeta.Core.Diagnostics;");
            sb.AppendLine("using SharedMeta.Core.Diagnostics.Formatting;");
            sb.AppendLine("using SharedMeta.Core.Random;");
            sb.AppendLine("using SharedMeta.Core.Patch;");
            sb.AppendLine("using SharedMeta.Client;");
            sb.AppendLine("using SharedMeta.Core.Logging;");
            sb.AppendLine("using ExecutionMode = SharedMeta.Core.ExecutionMode;");
            if (serializer == DetectedSerializer.MemoryPack)
            {
                sb.AppendLine("using MemoryPack;");
            }
            EmitImplDeclaredUsings(sb, symbol, compilation, alreadyEmitted: new[]
            {
                "System", "System.Collections.Generic", "System.Threading.Tasks",
                "SharedMeta.Core", "SharedMeta.Core.Packets", "SharedMeta.Core.Network",
                "SharedMeta.Core.Diagnostics", "SharedMeta.Core.Diagnostics.Formatting",
                "SharedMeta.Core.Random", "SharedMeta.Core.Patch",
                "SharedMeta.Client", "SharedMeta.Core.Logging", namespaceName,
            });
            sb.AppendLine($"namespace {namespaceName}.Client");
            sb.AppendLine("{");

            // Generate ApiClient class
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// API client for {interfaceName}.");
            sb.AppendLine($"    /// Supports runtime mode switching between Server and Optimistic.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public class {baseName2}ApiClient : IDisposable");
            sb.AppendLine("    {");

            // Fields
            sb.AppendLine("        private readonly INetwork _network;");
            sb.AppendLine("        private readonly IMetaSerializer _serializer;");
            sb.AppendLine($"        private readonly EntityStateContainer<{stateTypeName}> _stateContainer;");
            sb.AppendLine($"        // _state delegates to the shared container so all API clients on the same entity");
            sb.AppendLine($"        // observe the same instance — including after a wholesale ServerReplace.");
            sb.AppendLine($"        private {stateTypeName} _state => _stateContainer.State;");
            sb.AppendLine($"        private readonly {namespaceName}.{implClassName} _service;");
            if (hasDeepDesync)
                sb.AppendLine($"        private readonly {namespaceName}.{patchTrackedClassName} _patchTrackedService;");
            sb.AppendLine("        private readonly IExecutionModeProvider _modeProvider;");
            sb.AppendLine("        private readonly IDesyncDiagnostics? _diagnostics;");
            sb.AppendLine("        private readonly ICrossEntityResolver? _crossEntityResolver;");
            sb.AppendLine("        private MetaRandom? _optimisticRandom;");
            sb.AppendLine("        private IReadOnlyList<MetaRandom>? _namedRandoms;");
            sb.AppendLine("        private readonly object? _config;");
            // 0.21.0: optional version-aware config resolver from EntityConnection. When the
            // server's ExecutedConfigVersions[0] (carried on each RpcResponse/EntityBroadcast)
            // differs from this session's pin, the replay path passes the version into
            // SetContext, which calls this resolver to materialize the right config branch.
            // Null when the entity has no config system — _config alone is used.
            sb.AppendLine("        private readonly System.Func<SharedMeta.Core.MetaConfigVersion, object?>? _configResolver;");
            // 0.33.0+ Resolved [ServiceConfig] values, positional (parallel to
            // MetaServiceConfig.ServiceConfigTypes). Null/empty when the service declares none.
            sb.AppendLine("        private readonly System.Collections.Generic.IReadOnlyList<object>? _serviceConfigs;");

            // 0.33.0+ Typed config accessors directly on the instance — sidesteps
            // IMetaServiceResolver.GetEntityConfig<TConfig>(entityId)'s cross-entity ambiguity
            // question entirely for the common "I already have my api, give me its config" case.
            if (apiConfigTypeName != null)
            {
                sb.AppendLine();
                sb.AppendLine($"        /// <summary>Resolved config for this entity (from [MetaService(ConfigType=...)] or DefaultConfig).</summary>");
                sb.AppendLine($"        public {apiConfigTypeName}? Config => ({apiConfigTypeName}?)_config;");
            }
            for (int i = 0; i < apiServiceConfigs.Count; i++)
            {
                var (scType, scName) = apiServiceConfigs[i];
                var scTypeName = scType!.ToDisplayString();
                sb.AppendLine();
                sb.AppendLine($"        /// <summary>Config '{scName}' declared via [ServiceConfig] on {interfaceName}.</summary>");
                // By type, not by index — _serviceConfigs is the entity's union across services,
                // so this service's declaration order is not that list's order.
                sb.AppendLine($"        public {scTypeName} {scName} => global::SharedMeta.Core.ServiceConfigLookup.Find<{scTypeName}>(_serviceConfigs)!;");
            }

            sb.AppendLine($"        private const string ServiceName = \"{interfaceName}\";");
            sb.AppendLine($"        private Exception? _errorException;");

            // Result-comparer fields — emitted only for return types that have a registered
            // IMetaResultComparer<T> implementation. The generated Optimistic / CrossOptimistic /
            // Server method dispatchers call AreEqual instead of comparing serialized bytes.
            // See <see cref="ResultComparerScanner"/> for discovery.
            if (usedComparers.Count > 0)
            {
                sb.AppendLine();
                foreach (var kv in usedComparers)
                {
                    var fieldName = "_resultComparer_" + SanitizeTypeNameForIdentifier(kv.Key);
                    sb.AppendLine($"        private static readonly IMetaResultComparer<{kv.Key}> {fieldName} = new {kv.Value.ComparerFullName}();");
                }
            }
            sb.AppendLine();

            // Events
            sb.AppendLine("        // Events fired when methods are replayed from broadcasts");
            foreach (var method in methods)
            {
                // Skip query / local-query methods — they don't produce broadcasts or replays
                if (IsQueryMethod(method) || IsLocalQueryMethod(method)) continue;

                var methodName = method.Identifier.Text;
                var eventName = GetEventName(methodName);
                var paramCount = method.ParameterList.Parameters.Count;

                if (paramCount == 0)
                {
                    sb.AppendLine($"        public event Action? {eventName};");
                }
                else
                {
                    var argTypes = method.ParameterList.Parameters.Select(p => p.Type!.ToString());
                    var argTypesStr = string.Join(", ", argTypes);
                    if (paramCount == 1)
                    {
                        sb.AppendLine($"        public event Action<{argTypesStr}>? {eventName};");
                    }
                    else
                    {
                        sb.AppendLine($"        public event Action<({argTypesStr})>? {eventName};");
                    }
                }
            }

            sb.AppendLine();

            // State refresh event
            sb.AppendLine($"        /// <summary>Fired after state is replaced on reconnect. Use to update Views.</summary>");
            sb.AppendLine($"        public event Action<{stateTypeName}>? OnStateRefreshed;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Fired after any state mutation on this entity, including foreign-service broadcasts. Sourced from the shared <see cref=\"EntityStateContainer{{TState}}.OnMutated\"/> — every API client on the entity fires in lock-step. Polling alternative: <see cref=\"MutationCount\"/>.</summary>");
            sb.AppendLine($"        public event Action? OnStateMutated;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Shared per-entity mutation counter — bumped every time the state is touched on this");
            sb.AppendLine($"        /// client, regardless of which service triggered the change. Increments on Optimistic /");
            sb.AppendLine($"        /// CrossOptimistic local execution, Server / ServerPatch / ServerReplace result application,");
            sb.AppendLine($"        /// incoming broadcasts (own service AND foreign-service broadcasts on the same entity),");
            sb.AppendLine($"        /// and reconnect refresh. Backed by the entity's <see cref=\"EntityStateContainer{{TState}}\"/>");
            sb.AppendLine($"        /// — every API client subscribed to this entity returns the same value.");
            sb.AppendLine($"        /// Local-only: not synchronized across clients, not persisted, not coordinated with the");
            sb.AppendLine($"        /// network sequence number.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public int MutationCount => _stateContainer.MutationCount;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Fired when a service method throws. Parameters: serviceName, exception.</summary>");
            sb.AppendLine($"        public event Action<string, Exception>? OnServiceError;");
            sb.AppendLine();

            // Properties
            sb.AppendLine($"        /// <summary>Current state.</summary>");
            sb.AppendLine($"        public {stateTypeName} State => _state;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Client ID.</summary>");
            sb.AppendLine($"        public string ClientId => _network.ClientId;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>True if the service is in error state due to a previous exception.</summary>");
            sb.AppendLine($"        public bool HasError => _errorException != null;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>The exception that put the service in error state, or null.</summary>");
            sb.AppendLine($"        public Exception? ErrorException => _errorException;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Clear the error state, allowing further method calls.</summary>");
            sb.AppendLine($"        public void ClearError() => _errorException = null;");
            sb.AppendLine();

            // Constructor
            sb.AppendLine($"        public {baseName2}ApiClient(");
            sb.AppendLine($"            INetwork network,");
            sb.AppendLine($"            IMetaSerializer serializer,");
            sb.AppendLine($"            EntityStateContainer<{stateTypeName}> stateContainer,");
            sb.AppendLine($"            IExecutionModeProvider modeProvider,");
            sb.AppendLine($"            IDesyncDiagnostics? diagnostics = null,");
            sb.AppendLine($"            ICrossEntityResolver? crossEntityResolver = null,");
            sb.AppendLine($"            MetaRandom? optimisticRandom = null,");
            sb.AppendLine($"            object? config = null,");
            sb.AppendLine($"            IReadOnlyList<MetaRandom>? namedRandoms = null,");
            sb.AppendLine($"            System.Func<SharedMeta.Core.MetaConfigVersion, object?>? configResolver = null,");
            sb.AppendLine($"            System.Collections.Generic.IReadOnlyList<object>? serviceConfigs = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            _network = network;");
            sb.AppendLine("            _serializer = serializer;");
            sb.AppendLine("            _stateContainer = stateContainer;");
            sb.AppendLine("            _modeProvider = modeProvider;");
            sb.AppendLine("            _diagnostics = diagnostics;");
            sb.AppendLine("            _crossEntityResolver = crossEntityResolver;");
            sb.AppendLine("            _optimisticRandom = optimisticRandom;");
            sb.AppendLine("            _namedRandoms = namedRandoms;");
            sb.AppendLine("            _config = config;");
            sb.AppendLine("            _configResolver = configResolver;");
            sb.AppendLine("            _serviceConfigs = serviceConfigs;");
            sb.AppendLine($"            _service = new {namespaceName}.{implClassName}();");
            if (hasDeepDesync)
                sb.AppendLine($"            _patchTrackedService = new {namespaceName}.{patchTrackedClassName}();");
            sb.AppendLine();
            sb.AppendLine("            _network.OnBroadcast += HandleBroadcast;");
            sb.AppendLine("            // Container fires OnMutated whenever any source (entity-level handler, this");
            sb.AppendLine("            // ApiClient's own methods, or another ApiClient on the same entity) mutates state.");
            sb.AppendLine("            _stateContainer.OnMutated += FireOnStateMutated;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void FireOnStateMutated() => OnStateMutated?.Invoke();");
            sb.AppendLine();

            // Named-random scroll helpers — mirror MetaProviderBase captures on the server side.
            // When _namedRandoms is null (state has no [NamedRandom]), all of these are zero-cost no-ops.
            sb.AppendLine("        private long[]? CaptureNamedScrollSnapshot()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_namedRandoms == null || _namedRandoms.Count == 0) return null;");
            sb.AppendLine("            var snap = new long[_namedRandoms.Count];");
            sb.AppendLine("            for (int _i = 0; _i < _namedRandoms.Count; _i++) snap[_i] = _namedRandoms[_i].ScrollId;");
            sb.AppendLine("            return snap;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private long[]? ComputeLocalNamedScrollDeltas(long[]? before)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (before == null || _namedRandoms == null) return null;");
            sb.AppendLine("            long[]? deltas = null;");
            sb.AppendLine("            var limit = System.Math.Min(before.Length, _namedRandoms.Count);");
            sb.AppendLine("            for (int _i = 0; _i < limit; _i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                var _d = _namedRandoms[_i].ScrollId - before[_i];");
            sb.AppendLine("                if (_d == 0) continue;");
            sb.AppendLine("                deltas ??= new long[before.Length];");
            sb.AppendLine("                deltas[_i] = _d;");
            sb.AppendLine("            }");
            sb.AppendLine("            return deltas;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void ApplyNamedScrollSkips(long[]? serverDeltas)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (serverDeltas == null || _namedRandoms == null) return;");
            sb.AppendLine("            var limit = System.Math.Min(serverDeltas.Length, _namedRandoms.Count);");
            sb.AppendLine("            for (int _i = 0; _i < limit; _i++)");
            sb.AppendLine("                if (serverDeltas[_i] > 0) _namedRandoms[_i].Skip(serverDeltas[_i]);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void CompareAndReportNamedScrollDesync(string methodAlias, long[]? serverDeltas, long[]? localDeltas)");
            sb.AppendLine("        {");
            sb.AppendLine("            var serverLen = serverDeltas?.Length ?? 0;");
            sb.AppendLine("            var localLen = localDeltas?.Length ?? 0;");
            sb.AppendLine("            var max = System.Math.Max(serverLen, localLen);");
            sb.AppendLine("            for (int _i = 0; _i < max; _i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                var _s = _i < serverLen ? serverDeltas![_i] : 0L;");
            sb.AppendLine("                var _l = _i < localLen ? localDeltas![_i] : 0L;");
            sb.AppendLine("                if (_s != _l)");
            sb.AppendLine("                    _diagnostics?.OnRandomDesync(ServiceName, methodAlias + \"[NamedRandom:\" + _i + \"]\", _s, _l);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        private void SetError(Exception ex, ushort methodId, string methodName = \"\")");
            sb.AppendLine("        {");
            sb.AppendLine("            var label = string.IsNullOrEmpty(methodName) ? $\"methodId={methodId}\" : $\"{methodName} (methodId={methodId})\";");
            sb.AppendLine("            MetaLog.Error($\"[{ServiceName}.{label}] Service error\", ex);");
            sb.AppendLine("            _errorException = ex;");
            sb.AppendLine("            OnServiceError?.Invoke(ServiceName, ex);");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Generate methods.
            //
            // [MetaMethod(GenerateClientApi = false)] suppresses the public callable here —
            // user code on the client cannot reach these methods through the typed API. They
            // remain reachable via cross-entity (the EntityCaller surface emitted by
            // ContextInjectionGenerator) and via sibling-bypass on the server. Broadcasts are
            // still received and replayed: the events declaration (above) and the broadcast/
            // replay handlers (below) intentionally do NOT filter on the flag — when another
            // entity invokes the protected method cross-entity, our subscribed client still
            // applies the resulting state changes and fires the event for any UI that wants
            // to observe them.
            foreach (var method in methods)
            {
                if (IsGenerateClientApiFalse(method)) continue;
                methodComparers.TryGetValue(method, out var comparer);
                GenerateMethod(sb, method, interfaceName, namespaceName, implClassName, stateTypeName, serializer, hasDeepDesync, comparer, capabilitiesEnabled, compilation);
            }

            // Context management
            GenerateContextMethods(sb, stateTypeName, hasDeepDesync);

            // Broadcast handling
            GenerateHandleBroadcast(sb, methods, interfaceName, namespaceName, implClassName, stateTypeName, serializer, compilation);

            // Trigger replay
            GenerateTriggerReplayMethods(sb, methods, stateTypeName, interfaceName, namespaceName);

            // RefreshState — called by MetaServiceResolver on reconnect
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Replace internal state and random after reconnect.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public void RefreshState({stateTypeName} newState, MetaRandom? newRandom, IReadOnlyList<MetaRandom>? newNamedRandoms = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            // Replace through the container — bumps MutationCount and fires OnMutated,");
            sb.AppendLine("            // which our subscription forwards to OnStateMutated for every API client on this entity.");
            sb.AppendLine("            _stateContainer.Replace(newState);");
            sb.AppendLine("            _optimisticRandom = newRandom;");
            sb.AppendLine("            if (newNamedRandoms != null) _namedRandoms = newNamedRandoms;");
            sb.AppendLine("            _errorException = null;");
            sb.AppendLine("            OnStateRefreshed?.Invoke(newState);");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Dispose
            sb.AppendLine("        public void Dispose()");
            sb.AppendLine("        {");
            sb.AppendLine("            _network.OnBroadcast -= HandleBroadcast;");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static void GenerateMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string interfaceName, string namespaceName, string implClassName, string? stateTypeName,
            DetectedSerializer serializer, bool hasDeepDesync = false, ResultComparerInfo? resultComparer = null,
            bool capabilitiesEnabled = true, Compilation? compilation = null)
        {
            // Which arguments get boxed is decided here, once, from the compilation — the server
            // dispatcher derives the same answer from the same source, so both ends of the wire
            // agree without any runtime handshake.
            var transforms = TransformerAnalysis.Analyze(method.ParameterList.Parameters, compilation);
            var methodName = method.Identifier.Text;
            var returnType = method.ReturnType.ToString();
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            var paramCount = method.ParameterList.Parameters.Count;

            // Parse MetaMethod attribute for default mode
            var methodAlias = GetMethodAlias(method, methodName);
            // 0.22.0: [MetaMethod(Version = N)] stamped onto RpcCall.MethodVersion so the
            // server dispatcher routes (Alias, Version) to the matching impl. Default 0 =
            // legacy/unversioned — server treats it as the lowest-versioned method under the alias.
            var methodVersion = GetMethodVersion(method);
            var defaultMode = "Server";
            bool isQueryMethod = false;
            bool isSignalMethod = false;
            bool modeExplicit = false;
            bool legacyQueryBool = false;
            bool legacySignalBool = false;
            string syncApi = "None";
            bool syncExplicit = false;
            string syncPolicy = "Throw";
            bool skipServerOnFalse = false;
            // 0.26.6+ [MetaMethod(DeepStateCheck = SnapshotTiming.X)]. 0 = None, 1 = Before,
            // 2 = After, 3 = Both. When non-zero, the generated client body snapshots
            // _state at the matching moment(s), computes FNV-1a CRC, and ships them on
            // RpcCall.Debug (PayloadDebug). Server compares and stamps op.Debug.DesyncStateBytes /
            // DesyncTiming on mismatch.
            int deepStateCheck = 0;

            var attributes = method.AttributeLists.SelectMany(a => a.Attributes);
            var metaMethod = attributes.FirstOrDefault(a => a.Name.ToString().Contains("MetaMethod"));
            if (metaMethod != null)
            {
                foreach (var arg in metaMethod.ArgumentList?.Arguments ?? Enumerable.Empty<AttributeArgumentSyntax>())
                {
                    if (arg.NameEquals != null)
                    {
                        var name = arg.NameEquals.Name.Identifier.Text;
                        if (name == "Mode" && arg.Expression is MemberAccessExpressionSyntax modeAccess)
                        {
                            defaultMode = modeAccess.Name.Identifier.Text;
                            modeExplicit = true;
                        }
                        if (name == "Query" && arg.Expression is LiteralExpressionSyntax queryLit
                            && queryLit.Token.Text == "true")
                            legacyQueryBool = true;
                        if (name == "Signal" && arg.Expression is LiteralExpressionSyntax signalLit
                            && signalLit.Token.Text == "true")
                            legacySignalBool = true;
                        if (name == "Sync" && arg.Expression is MemberAccessExpressionSyntax syncAccess)
                        {
                            syncApi = syncAccess.Name.Identifier.Text;
                            syncExplicit = true;
                        }
                        if (name == "SyncPolicy" && arg.Expression is MemberAccessExpressionSyntax policyAccess)
                            syncPolicy = policyAccess.Name.Identifier.Text;
                        if (name == "SkipServerOnFalse" && arg.Expression is LiteralExpressionSyntax skipLit
                            && skipLit.Token.Text == "true")
                            skipServerOnFalse = true;
                        // 0.26.6+ DeepStateCheck = SnapshotTiming.Before|After|Both — accept either
                        // a member access expression (DeepStateCheck = SnapshotTiming.X) or the
                        // legacy integer-literal form.
                        if (name == "DeepStateCheck")
                        {
                            if (arg.Expression is MemberAccessExpressionSyntax dscAccess)
                            {
                                deepStateCheck = dscAccess.Name.Identifier.Text switch
                                {
                                    "Before" => 1,
                                    "After" => 2,
                                    "Both" => 3,
                                    _ => 0,
                                };
                            }
                            else if (arg.Expression is LiteralExpressionSyntax dscLit
                                && int.TryParse(dscLit.Token.ValueText, out var dscInt))
                            {
                                deepStateCheck = dscInt & 3;
                            }
                        }
                    }
                }
            }

            // LocalQuery is a synchronous, no-RPC read over locally replicated State. Its natural
            // client API is the sync overload, so when the author leaves Sync unspecified we default
            // to OnlySync (sync only). An explicit Sync is honoured verbatim: None → async wrapper
            // only, Generate → both, OnlySync → sync only. Both forms run the impl over local State;
            // the async wrapper completes synchronously (no RPC) and exists for forward-compat — a
            // caller that already `await`s {Method}Async keeps compiling if the method later moves to
            // a server-backed execution mode.
            if (defaultMode == "LocalQuery" && !syncExplicit)
                syncApi = "OnlySync";

            // Unified Kind detection: new canonical form is Mode = ExecutionMode.Query | Signal,
            // legacy form is Query = true / Signal = true (bool, [Obsolete]). Accept either; reject
            // the clash where a method sets both the legacy bool AND an explicit non-matching Mode.
            bool modeIsQuery = modeExplicit && defaultMode == "Query";
            bool modeIsSignal = modeExplicit && defaultMode == "Signal";
            isQueryMethod = modeIsQuery || legacyQueryBool;
            isSignalMethod = modeIsSignal || legacySignalBool;

            if (legacyQueryBool && modeExplicit && !modeIsQuery)
                sb.AppendLine($"#error SharedMeta: '{interfaceName}.{methodName}' sets Query = true (deprecated) and Mode = ExecutionMode.{defaultMode} at the same time. Remove the bool and rely on Mode = ExecutionMode.Query.");
            if (legacySignalBool && modeExplicit && !modeIsSignal)
                sb.AppendLine($"#error SharedMeta: '{interfaceName}.{methodName}' sets Signal = true (deprecated) and Mode = ExecutionMode.{defaultMode} at the same time. Remove the bool and rely on Mode = ExecutionMode.Signal.");
            if (isQueryMethod && isSignalMethod)
                sb.AppendLine($"#error SharedMeta: '{interfaceName}.{methodName}' resolves to both Query and Signal mode. These are mutually exclusive — Query returns a value, Signal is void fire-and-forget.");

            // For the downstream emission paths (which previously branched on bool isQueryMethod /
            // isSignalMethod), the "explicit mode" flag modeExplicit should NOT be true when the
            // canonical mode is Query/Signal — otherwise Sync-validation would misfire saying
            // "Signal with explicit Mode is invalid". Clear the flag for those paths.
            if (modeIsQuery || modeIsSignal) modeExplicit = false;

            // Query methods — execute locally on client state, no network call
            if (isQueryMethod)
            {
                GenerateLocalQueryMethod(sb, method);
                return;
            }

            // Signal methods — fire-and-forget, no response, no RequestId tracking.
            // Client emits a void {Method}Signal(params) that delegates to INetwork.SendSignalAsync.
            // Validation: must return void, must not combine with Query/Sync/explicit Mode.
            if (isSignalMethod)
            {
                GenerateSignalMethod(sb, method, methodAlias, isQueryMethod, modeExplicit, syncApi, interfaceName, namespaceName, serializer, transforms);
                return;
            }

            // Check if the method is async (returns Task or Task<T>)
            bool isAsync = returnType.StartsWith("Task") || returnType.StartsWith("System.Threading.Tasks.Task");

            // Extract inner type from Task<T> for async methods
            bool isVoidReturn = returnType == "void" || returnType == "Task";
            string innerReturnType = ExtractInnerType(returnType);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{innerReturnType}>";
            var argNames = method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToList();
            var callArgs = string.Join(", ", argNames);

            bool wantsSync = syncApi == "Generate" || syncApi == "OnlySync";
            bool onlySync = syncApi == "OnlySync";

            // LocalQuery: synchronous client-side read over local State. Emit sync and/or async
            // wrappers per Sync (default OnlySync) and return — none of the RPC-mode generation below
            // applies (no server round-trip, no replay, no per-mode private bodies). The impl must
            // return a non-Task value; void / Task / Task<T> are rejected.
            if (defaultMode == "LocalQuery")
            {
                if (isVoidReturn || isAsync)
                {
                    sb.AppendLine($"#error SharedMeta: [MetaMethod] on '{interfaceName}.{methodName}' has Mode = ExecutionMode.LocalQuery but its signature is '{returnType}'. LocalQuery is a synchronous local-state read and must return a non-Task value (T). Use Optimistic / Server for writes or async/server results.");
                    return;
                }
                GenerateLocalQueryApiMethods(sb, method, methodName, parameters, callArgs,
                    innerReturnType, wantsSync, onlySync, interfaceName, namespaceName);
                return;
            }

            // Compile-time validation for sync generation.
            // Emit #error lines into the generated output — Roslyn surfaces them as CS1029
            // with our message, which is clearer than silently skipping misconfigured methods.
            if (wantsSync)
            {
                if (defaultMode != "Optimistic" && defaultMode != "LocalQuery")
                {
                    sb.AppendLine($"#error SharedMeta: [MetaMethod] on '{interfaceName}.{methodName}' has Sync = SyncApi.{syncApi} but Mode = ExecutionMode.{defaultMode}. Sync API generation is only supported for Optimistic or LocalQuery methods.");
                }
                if (isAsync)
                {
                    sb.AppendLine($"#error SharedMeta: [MetaMethod] on '{interfaceName}.{methodName}' has Sync = SyncApi.{syncApi} but the service signature is async (return type '{returnType}'). Change the return type to a non-Task type, or remove Sync.");
                }
            }

            // SkipServerOnFalse validation. The flag has no meaning without a return value to
            // compare against `default`, and the only execution mode that runs the impl locally
            // first (so the return value is available before the RPC) is Optimistic. Silently
            // ignoring — as pre-0.17.0 did for every Optimistic method too — is the original
            // footgun; surface misuse loudly. Mode validation only fires when Mode is set
            // EXPLICITLY to a non-Optimistic value: methods without an explicit Mode default to
            // Optimistic at runtime (per MetaMethodAttribute.Mode default), so the flag is valid
            // there even though our generator's local `defaultMode` initial value is "Server".
            if (skipServerOnFalse)
            {
                if (isVoidReturn)
                {
                    sb.AppendLine($"#error SharedMeta: [MetaMethod] on '{interfaceName}.{methodName}' has SkipServerOnFalse = true but the method is void. The flag compares the local return value against default(T) — there is no return value to compare. Remove SkipServerOnFalse, or change the return type to bool/int/enum/etc.");
                }
                if (modeExplicit && defaultMode != "Optimistic")
                {
                    sb.AppendLine($"#error SharedMeta: [MetaMethod] on '{interfaceName}.{methodName}' has SkipServerOnFalse = true but Mode = ExecutionMode.{defaultMode}. The flag is meaningful only for Optimistic methods, where the impl runs locally first and the result decides whether the server RPC fires. Other modes don't have a local-first phase.");
                }
            }

            // Public async method with mode switch — skipped when Sync = OnlySync
            if (!onlySync)
            {
                sb.AppendLine($"        /// <summary>");
                sb.AppendLine($"        /// {methodName} - Default mode: {defaultMode}");
                sb.AppendLine($"        /// </summary>");
                sb.AppendLine($"        [global::SharedMeta.Core.GeneratedFromMetaMethod(typeof(global::{namespaceName}.{interfaceName}), \"{methodName}\")]");
                sb.AppendLine($"        public {asyncReturnType} {ContextInjectionGenerator.AsyncMethodName(methodName)}({parameters})");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_errorException != null) throw new ServiceErrorStateException(ServiceName, _errorException);");
                GenerateTransformNormalization(sb, transforms);
                if (capabilitiesEnabled)
                {
                    // 0.24.0 capabilities gate. Two layers checked:
                    //   * session-level _network.Annotated.Statuses[methodId] (O(1) array index)
                    //   * per-entity _network.EntityCapabilities (config-boundary overlay,
                    //     still by ServiceName — per-entity granularity isn't folded yet).
                    // Allocation-free on the common path (null annotation, empty entity caps).
                    var methodIdConst = $"global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}";
                    sb.AppendLine($"            if (global::SharedMeta.Core.Transport.CapabilitiesGate.IsRejected(_network.Annotated, {methodIdConst})");
                    sb.AppendLine($"                || global::SharedMeta.Core.Transport.CapabilitiesGate.IsServiceRejectedByEntity(_network.EntityCapabilities, ServiceName))");
                    sb.AppendLine($"                throw global::SharedMeta.Core.Transport.CapabilitiesGate.RejectedException(ServiceName, \"{methodAlias}\", {methodVersion});");
                    // 0.24.0 force-ServerPatch downgrade — server folded service-level rules into
                    // per-method Statuses, so the session-level check is also a single array index.
                    sb.AppendLine($"            if (global::SharedMeta.Core.Transport.CapabilitiesGate.IsForcedServerPatch(_network.Annotated, {methodIdConst})");
                    sb.AppendLine($"                || global::SharedMeta.Core.Transport.CapabilitiesGate.IsServiceForcedServerPatchByEntity(_network.EntityCapabilities, ServiceName))");
                    sb.AppendLine($"                return {ContextInjectionGenerator.AsyncMethodName(methodName)}_ServerPatch({callArgs});");
                }
                sb.AppendLine($"            var mode = _modeProvider.GetMode(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, ExecutionMode.{defaultMode});");
                sb.AppendLine($"            if (mode == ExecutionMode.ServerPatch)");
                sb.AppendLine($"                return {ContextInjectionGenerator.AsyncMethodName(methodName)}_ServerPatch({callArgs});");
                sb.AppendLine($"            if (mode == ExecutionMode.ServerReplace)");
                sb.AppendLine($"                return {ContextInjectionGenerator.AsyncMethodName(methodName)}_ServerReplace({callArgs});");
                sb.AppendLine($"            if (mode == ExecutionMode.Server)");
                sb.AppendLine($"                return {ContextInjectionGenerator.AsyncMethodName(methodName)}_Server({callArgs});");
                sb.AppendLine($"            if (mode == ExecutionMode.CrossOptimistic)");
                sb.AppendLine($"                return {ContextInjectionGenerator.AsyncMethodName(methodName)}_CrossOptimistic({callArgs});");
                sb.AppendLine($"            return {ContextInjectionGenerator.AsyncMethodName(methodName)}_Optimistic({callArgs});");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Public sync method (Optimistic) — emitted when Sync is requested on a valid sync signature.
            // Runtime guard: if IExecutionModeProvider has overridden the mode away from Optimistic
            // (e.g. loaded config promoted this method to Server), apply SyncPolicy (Throw/Warn/Silent).
            // TODO(sync-mode-override): today we still run the local body on Warn/Silent — consider an
            // opt-in that schedules a server round-trip instead (fire-and-discard local result) for callers
            // that want correctness over immediacy when a config override downgrades the mode.
            // (LocalQuery is handled earlier by GenerateLocalQueryApiMethods and never reaches here.)
            if (wantsSync && !isAsync && defaultMode == "Optimistic")
            {
                string syncRet = isVoidReturn ? "void" : innerReturnType;
                sb.AppendLine($"        /// <summary>");
                sb.AppendLine($"        /// Synchronous overload of {methodName}. Executes locally and fires the server round-trip in the background.");
                sb.AppendLine($"        /// SyncPolicy on runtime mode override: {syncPolicy}.");
                sb.AppendLine($"        /// </summary>");
                sb.AppendLine($"        [global::SharedMeta.Core.GeneratedFromMetaMethod(typeof(global::{namespaceName}.{interfaceName}), \"{methodName}\")]");
                sb.AppendLine($"        public {syncRet} {methodName}Sync({parameters})");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_errorException != null) throw new ServiceErrorStateException(ServiceName, _errorException);");
                GenerateTransformNormalization(sb, transforms);
                if (capabilitiesEnabled)
                {
                    // 0.24.0 capabilities gate — same contract as the async overload (session + per-entity).
                    var methodIdConst = $"global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}";
                    sb.AppendLine($"            if (global::SharedMeta.Core.Transport.CapabilitiesGate.IsRejected(_network.Annotated, {methodIdConst})");
                    sb.AppendLine($"                || global::SharedMeta.Core.Transport.CapabilitiesGate.IsServiceRejectedByEntity(_network.EntityCapabilities, ServiceName))");
                    sb.AppendLine($"                throw global::SharedMeta.Core.Transport.CapabilitiesGate.RejectedException(ServiceName, \"{methodAlias}\", {methodVersion});");
                }
                sb.AppendLine($"            var mode = _modeProvider.GetMode(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, ExecutionMode.{defaultMode});");
                sb.AppendLine("            if (mode != ExecutionMode.Optimistic && mode != ExecutionMode.LocalQuery)");
                sb.AppendLine("            {");
                if (syncPolicy == "Warn")
                {
                    sb.AppendLine($"                MetaLog.Warning($\"[{{ServiceName}}.{methodAlias}] Sync called but effective mode is {{mode}} — executing locally without a server round-trip.\");");
                    sb.AppendLine($"                _diagnostics?.OnSyncPolicyViolation(ServiceName, \"{methodAlias}\", mode);");
                }
                else if (syncPolicy == "Silent")
                {
                    sb.AppendLine($"                _diagnostics?.OnSyncPolicyViolation(ServiceName, \"{methodAlias}\", mode);");
                }
                else // Throw (default)
                {
                    sb.AppendLine($"                throw new InvalidOperationException($\"[{{ServiceName}}.{methodAlias}] Sync invocation blocked: effective mode is {{mode}} (overridden by IExecutionModeProvider). Use {ContextInjectionGenerator.AsyncMethodName(methodName)}, or set SyncPolicy = SyncPolicy.Warn/Silent on [MetaMethod].\");");
                }
                sb.AppendLine("            }");
                if (isVoidReturn)
                    sb.AppendLine($"            {methodName}Sync_Optimistic({callArgs});");
                else
                    sb.AppendLine($"            return {methodName}Sync_Optimistic({callArgs});");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Per-mode private implementations. Emitted only when the corresponding public dispatcher
            // can actually reach them — skipped when Sync = OnlySync since no async public dispatcher exists.
            if (!onlySync)
            {
                GenerateServerMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, transforms, serializer, interfaceName, namespaceName, stateTypeName, hasDeepDesync, resultComparer);
                GenerateOptimisticMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, transforms, serializer, interfaceName, namespaceName, stateTypeName, hasDeepDesync, skipServerOnFalse, resultComparer, deepStateCheck);
                GenerateCrossOptimisticMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, transforms, serializer, interfaceName, namespaceName, stateTypeName, hasDeepDesync, resultComparer);
                GenerateServerPatchMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, transforms, serializer, interfaceName, namespaceName, stateTypeName);
                GenerateServerReplaceMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, transforms, serializer, stateTypeName, interfaceName, namespaceName);
            }

            // Private sync-optimistic body — only emitted for the Optimistic sync overload above.
            // LocalQuery's sync overload calls the impl directly (no _Optimistic round-trip helper).
            if (wantsSync && !isAsync && defaultMode == "Optimistic")
            {
                GenerateOptimisticMethodSync(sb, method, methodAlias, innerReturnType, isVoidReturn, paramCount, callArgs, transforms, serializer, interfaceName, namespaceName, stateTypeName, hasDeepDesync, skipServerOnFalse, resultComparer, deepStateCheck);
            }
        }

        /// <summary>
        /// Extract inner type from Task&lt;T&gt; or return the type as-is.
        /// Task&lt;bool&gt; -> bool
        /// Task -> void
        /// bool -> bool
        /// void -> void
        /// </summary>
        private static string ExtractInnerType(string returnType)
        {
            if (returnType == "void" || returnType == "Task")
                return "void";

            // Handle Task<T>
            if (returnType.StartsWith("Task<") && returnType.EndsWith(">"))
            {
                return returnType.Substring(5, returnType.Length - 6);
            }

            // Handle System.Threading.Tasks.Task<T>
            if (returnType.StartsWith("System.Threading.Tasks.Task<") && returnType.EndsWith(">"))
            {
                return returnType.Substring(28, returnType.Length - 29);
            }

            return returnType;
        }

        /// <summary>
        /// Resolve a method's return type to its fully-qualified display string, unwrapping
        /// <see cref="System.Threading.Tasks.Task"/>/<c>Task&lt;T&gt;</c>. Returns <c>null</c>
        /// for void / Task / unresolvable types — none of which can have a comparer.
        /// </summary>
        private static string? ResolveReturnTypeFullName(MethodDeclarationSyntax method, Compilation compilation)
        {
            var model = compilation.GetSemanticModel(method.SyntaxTree);
            var typeInfo = model.GetTypeInfo(method.ReturnType);
            if (typeInfo.Type is not INamedTypeSymbol symbol) return null;

            // Task / Task<T>
            if (symbol.Name == "Task" &&
                symbol.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
            {
                if (!symbol.IsGenericType || symbol.TypeArguments.Length == 0) return null;
                if (symbol.TypeArguments[0] is not INamedTypeSymbol arg) return null;
                if (arg.SpecialType == SpecialType.System_Void) return null;
                return arg.ToDisplayString();
            }

            if (symbol.SpecialType == SpecialType.System_Void) return null;
            return symbol.ToDisplayString();
        }

        /// <summary>
        /// Convert a fully-qualified type name to a valid C# identifier suffix for use in
        /// generated static field names (<c>_resultComparer_{suffix}</c>). Replaces any
        /// non-letter/digit char with <c>_</c>.
        /// </summary>
        internal static string SanitizeTypeNameForIdentifier(string fullName)
        {
            var sb = new System.Text.StringBuilder(fullName.Length);
            foreach (var c in fullName)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private static void GenerateServerMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs, List<ParameterTransform> transforms,
            DetectedSerializer serializer, string interfaceName, string namespaceName, string? stateTypeName = null, bool hasDeepDesync = false, ResultComparerInfo? resultComparer = null)
        {
            var methodName = method.Identifier.Text;
            var methodVersion = GetMethodVersion(method);
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";

            // Use await for service call if the service method is async
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {ContextInjectionGenerator.AsyncMethodName(methodName)}_Server({parameters})");
            sb.AppendLine("        {");

            // Capture server time before the call
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Serialize arguments before suppressing broadcasts (no network involved)
            GenerateArgumentSerialization(sb, method, transforms, paramCount, serializer);
            sb.AppendLine();

            // Suppress broadcast processing for the entire RPC + local replay window.
            // Without this, broadcasts arriving between MarkDirectResponse and the local replay
            // can modify state, causing desyncs.
            sb.AppendLine("            _network.SuppressBroadcasts();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");

            // Call server
            if (isVoidReturn)
            {
                sb.AppendLine($"                var response = await _network.CallVoidAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            else
            {
                sb.AppendLine($"                var response = await _network.CallBytesAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks);");
                sb.AppendLine();
                // Deserialize result based on serializer
                GenerateResultDeserializationIndented(sb, returnType, serializer);
            }
            sb.AppendLine();

            // Set context and execute locally (we are the caller)
            // Use the server time from the response (may differ from captured time)
            sb.AppendLine("                var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine("                SetContext(response.ReplayContext, _network.PlayerId, response.ServerTimeTicks);");
            // Deep desync: activate PatchNode tracking before method execution
            if (hasDeepDesync)
            {
                sb.AppendLine("                var _ddRoot = new SharedMeta.Core.Patch.PatchNode(-1);");
                sb.AppendLine($"                MetaContextAccessor.Current!.PatchWrapper = new {stateTypeName}PatchWrapper(_state, _ddRoot, _serializer);");
            }
            var serviceRef = hasDeepDesync ? "_patchTrackedService" : "_service";
            if (isVoidReturn)
            {
                sb.AppendLine($"                {awaitPrefix}{serviceRef}.{methodName}({callArgs});");
            }
            else
            {
                sb.AppendLine($"                var localResult = {awaitPrefix}{serviceRef}.{methodName}({callArgs});");
            }
            if (hasDeepDesync)
            {
                // Capture local patch bytes before clearing PatchWrapper
                sb.AppendLine("                byte[] _ddLocalPatchBytes = System.Array.Empty<byte>();");
                sb.AppendLine("                uint _ddLocalCrc = 0;");
                sb.AppendLine("                _ddRoot.Prune();");
                sb.AppendLine("                if (_ddRoot.HasChanges)");
                sb.AppendLine("                {");
                sb.AppendLine("                    _ddLocalPatchBytes = _serializer.Pack(_ddRoot).ToArray();");
                sb.AppendLine("                    _ddLocalCrc = SharedMeta.Core.Patch.PatchCrc.Compute(_ddLocalPatchBytes);");
                sb.AppendLine("                }");
                sb.AppendLine("                MetaContextAccessor.Current!.PatchWrapper = null;");
            }
            sb.AppendLine("                ClearContext();");
            sb.AppendLine();
            sb.AppendLine("                ReplayTriggerOperations(response.TriggerOperations, _network.PlayerId, response.ServerTimeTicks);");

            // Validate and return (inside tracker scope so localResult is accessible)
            if (!isVoidReturn)
            {
                sb.AppendLine();
                if (resultComparer != null)
                {
                    // Comparer-based: structural equality. serverResult is already deserialized
                    // earlier in the Server method body; localResult is the just-replayed return
                    // value. The byte form of localResult is only needed for the desync report,
                    // so we serialize it lazily inside the mismatch branch.
                    var fieldName = "_resultComparer_" + SanitizeTypeNameForIdentifier(resultComparer.TargetTypeFullName);
                    sb.AppendLine($"                if (!{fieldName}.AreEqual(serverResult, localResult))");
                    sb.AppendLine("                {");
                    sb.AppendLine($"                    _diagnostics?.OnResultMismatch(ServiceName, \"{methodAlias}\", serverResult, localResult);");
                    sb.AppendLine($"                    SharedMeta.Core.Logging.MetaLog.Error($\"[Desync] {{ServiceName}}.{methodAlias} entity={{_network.EntityId}} server={{serverResult.MetaDescribe()}} local={{localResult.MetaDescribe()}} serverSeq={{response.Debug?.Info ?? \"<none>\"}} clientSeq={{_network.LastKnownEntitySequence}}\");");
                    if (serializer == DetectedSerializer.MemoryPack)
                        sb.AppendLine($"                    var localResultBytes = MemoryPackSerializer.Serialize(localResult);");
                    else
                        sb.AppendLine($"                    var localResultBytes = _serializer.Pack(localResult).ToArray();");
                    GenerateResultMismatchReport(sb, methodAlias, "response.ResultBytes", "localResultBytes", "                    ");
                    sb.AppendLine($"                    throw new DesyncException(ServiceName, \"{methodAlias}\", serverResult, localResult, serverResult.MetaDescribe(), localResult.MetaDescribe());");
                    sb.AppendLine("                }");
                }
                else
                {
                    // Use byte-level comparison to avoid reference equality issues with class types
                    GenerateResultByteComparison(sb, returnType, serializer, "response.ResultBytes", "                ");
                    sb.AppendLine("                {");
                    sb.AppendLine($"                    _diagnostics?.OnResultMismatch(ServiceName, \"{methodAlias}\", serverResult, localResult);");
                    sb.AppendLine($"                    SharedMeta.Core.Logging.MetaLog.Error($\"[Desync] {{ServiceName}}.{methodAlias} entity={{_network.EntityId}} server={{serverResult.MetaDescribe()}} local={{localResult.MetaDescribe()}} serverSeq={{response.Debug?.Info ?? \"<none>\"}} clientSeq={{_network.LastKnownEntitySequence}}\");");
                    GenerateResultMismatchReport(sb, methodAlias, "response.ResultBytes", "localResultBytes", "                    ");
                    sb.AppendLine($"                    throw new DesyncException(ServiceName, \"{methodAlias}\", serverResult, localResult, serverResult.MetaDescribe(), localResult.MetaDescribe());");
                    sb.AppendLine("                }");
                }
            }

            // Deep desync: compare patch CRC after execution (uses precomputed _ddLocalCrc/_ddLocalPatchBytes)
            GenerateDeepDesyncCheck(sb, methodAlias, "response", "                ", hasDeepDesync);

            if (!isVoidReturn)
            {
                sb.AppendLine();
                sb.AppendLine("                _tracker.FlushAndNotify();");
            sb.AppendLine("                _stateContainer.NotifyMutated();");
                sb.AppendLine("                return localResult;");
            }
            else
            {
                sb.AppendLine("                _tracker.FlushAndNotify();");
            sb.AppendLine("                _stateContainer.NotifyMutated();");
            }
            sb.AppendLine("                }");
            sb.AppendLine($"                catch (Exception ex) {{ _tracker.Discard(); SetError(ex, global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, \"{methodAlias}\"); throw; }}");

            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                _network.ResumeBroadcasts();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// Runs each transformed argument through Box then Unbox before the local body sees it.
        /// The server only ever receives the boxed form, so skipping this leaves the two sides
        /// executing the same method against different objects — every transformer that is not a
        /// perfect identity would surface as a desync on the very first call.
        /// </summary>
        private static void GenerateTransformNormalization(StringBuilder sb, List<ParameterTransform> transforms)
        {
            foreach (var t in transforms.Where(t => t.Transformed))
            {
                var boxed = TransformerAnalysis.BoxExpr(t, t.Name, "_state");
                sb.AppendLine($"            {t.Name} = {TransformerAnalysis.UnboxExpr(t, boxed, "_state")};");
            }
        }

        private static void GenerateArgumentSerialization(StringBuilder sb, MethodDeclarationSyntax method,
            List<ParameterTransform> transforms, int paramCount, DetectedSerializer serializer)
        {
            if (paramCount == 0)
            {
                sb.AppendLine("            var argsBytes = Array.Empty<byte>();");
                return;
            }

            // A transformed argument goes on the wire as its boxed type and occupies exactly one
            // member, same as any other — the framing is untouched, which is what lets the
            // MemoryPack fast path stay usable for methods that use transformers.
            foreach (var t in transforms.Where(t => t.Transformed))
                sb.AppendLine($"            var {t.WireLocal} = {TransformerAnalysis.BoxExpr(t, t.Name, "_state")};");

            string Arg(ParameterTransform t) => t.Transformed ? t.WireLocal : t.Name;

            if (serializer == DetectedSerializer.MemoryPack)
            {
                if (paramCount == 1)
                {
                    sb.AppendLine($"            var argsBytes = MemoryPackSerializer.Serialize({Arg(transforms[0])});");
                }
                else
                {
                    sb.AppendLine("            var buffer = new System.Buffers.ArrayBufferWriter<byte>();");
                    foreach (var t in transforms)
                    {
                        sb.AppendLine($"            MemoryPackSerializer.Serialize(buffer, {Arg(t)});");
                    }
                    sb.AppendLine("            var argsBytes = buffer.WrittenSpan.ToArray();");
                }
            }
            else
            {
                // Generic serializer — always use writer for consistent length-prefixed format.
                // Server dispatcher reads with CreateReader() which expects length-prefixed data.
                // No `using` — writer is pool-owned, Reset() between uses. Materialize to byte[]
                // here so argsBytes has the same type across both serializer branches: downstream
                // sites (DesyncReportRequest.ArgsBytes — byte[] field) avoid an extra .ToArray() copy,
                // and INetwork.CallAsync takes ROM which accepts byte[] via implicit conversion.
                sb.AppendLine("            var writer = _serializer.CreateWriter();");
                sb.AppendLine("            writer.Reset();");
                foreach (var t in transforms)
                {
                    sb.AppendLine($"            writer.Write({Arg(t)});");
                }
                sb.AppendLine("            var argsBytes = writer.Complete().ToArray();");
            }
        }

        private static void GenerateResultDeserialization(StringBuilder sb, string returnType, DetectedSerializer serializer)
        {
            if (serializer == DetectedSerializer.MemoryPack)
            {
                sb.AppendLine($"            var serverResult = MemoryPackSerializer.Deserialize<{returnType}>(response.ResultBytes)!;");
            }
            else
            {
                sb.AppendLine($"            var serverResult = _serializer.Unpack<{returnType}>(response.ResultBytes)!;");
            }
        }

        private static void GenerateResultDeserializationIndented(StringBuilder sb, string returnType, DetectedSerializer serializer)
        {
            if (serializer == DetectedSerializer.MemoryPack)
            {
                sb.AppendLine($"                var serverResult = MemoryPackSerializer.Deserialize<{returnType}>(response.ResultBytes)!;");
            }
            else
            {
                sb.AppendLine($"                var serverResult = _serializer.Unpack<{returnType}>(response.ResultBytes)!;");
            }
        }

        /// <summary>
        /// Generates byte-level result comparison: serializes localResult and compares with server bytes.
        /// This avoids reference equality issues with class return types that don't override Equals.
        /// </summary>
        private static void GenerateResultByteComparison(StringBuilder sb, string returnType,
            DetectedSerializer serializer, string serverBytesExpr, string indent)
        {
            if (serializer == DetectedSerializer.MemoryPack)
            {
                sb.AppendLine($"{indent}var localResultBytes = MemoryPackSerializer.Serialize(localResult);");
            }
            else
            {
                sb.AppendLine($"{indent}var localResultBytes = _serializer.Pack(localResult).ToArray();");
            }
            sb.AppendLine($"{indent}if (!{serverBytesExpr}.AsSpan().SequenceEqual(localResultBytes))");
        }

        /// <summary>
        /// 0.26.6+ Emit the OnDeepStateDesync callback wiring inside an Optimistic/CrossOptimistic
        /// continuation. No-op when the method isn't annotated DeepStateCheck. Picks the matching
        /// pre/post client bytes by the server-reported timing and invokes the diagnostics callback.
        /// </summary>
        private static void EmitDeepStateDesyncCallback(StringBuilder sb, string methodAlias, string? stateTypeName, int deepStateCheck, string indent)
        {
            if (deepStateCheck == 0) return;
            sb.AppendLine($"{indent}if (t.Result.Debug != null && t.Result.Debug.DesyncTiming != SharedMeta.Core.SnapshotTiming.None)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var _dsTiming = t.Result.Debug.DesyncTiming;");
            sb.AppendLine($"{indent}    var _dsClientBytesForCallback = _dsTiming == SharedMeta.Core.SnapshotTiming.Before ? _dsClientPreBytes : _dsClientPostBytes;");
            // 0.26.6+ Generic OnDeepStateDesync<TState> — caller's diagnostic gets TState typed
            // (no runtime switch on Type stateType). The generator binds {stateTypeName} from
            // [MetaService(StateType = typeof(TState))].
            sb.AppendLine($"{indent}    _diagnostics?.OnDeepStateDesync<{stateTypeName}>(_network.EntityId ?? string.Empty, _dsClientBytesForCallback ?? System.Array.Empty<byte>(), t.Result.Debug.DesyncStateBytes.ToArray(), _dsTiming, _network.ServerTimeTicks);");
            sb.AppendLine($"{indent}}}");
        }

        private static void GenerateOptimisticMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs, List<ParameterTransform> transforms,
            DetectedSerializer serializer, string interfaceName, string namespaceName, string? stateTypeName, bool hasDeepDesync = false, bool skipServerOnFalse = false, ResultComparerInfo? resultComparer = null, int deepStateCheck = 0)
        {
            var methodName = method.Identifier.Text;
            var methodVersion = GetMethodVersion(method);
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";

            // Optimistic always needs async for context cleanup
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {ContextInjectionGenerator.AsyncMethodName(methodName)}_Optimistic({parameters})");
            sb.AppendLine("        {");

            // Capture server time before local execution
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Set context before local execution (MetaContextAccessor must be set for Context.State access)
            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine($"            ctx.EntityId = _network.EntityId ?? string.Empty;");
            sb.AppendLine("            ctx.CallerId = _network.PlayerId;");
            sb.AppendLine("            ctx.ServerTimeTicks = serverTimeTicks;");
            sb.AppendLine("            ctx.Random = _optimisticRandom;");
            sb.AppendLine("            ctx.Config = _config;");
            sb.AppendLine("            ctx.Configs = _serviceConfigs;");
            sb.AppendLine("            ctx.NamedRandoms = _namedRandoms;");
            // 0.20.0: set instance Context on impl(s) directly + wire the client-side sibling
            // resolver so user code can call Get{Iface}SiblingAsync() / GetI{Iface}(self) and
            // get back a transient impl bound to ctx — same as server's sibling-bypass flow.
            sb.AppendLine("            ctx.SiblingServiceResolver = type => _crossEntityResolver?.ResolveSibling(type, ctx);");
            sb.AppendLine("            _service.Context = ctx;");
            if (hasDeepDesync)
                sb.AppendLine("            _patchTrackedService.Context = ctx;");
            sb.AppendLine("            MetaContextAccessor.Current = ctx;");

            if (!isVoidReturn)
            {
                sb.AppendLine($"            {returnType} localResult;");
            }

            // Capture scrollId before local execution for desync detection
            sb.AppendLine("            var scrollIdBefore = _optimisticRandom?.ScrollId ?? 0;");
            sb.AppendLine("            var namedScrollsBefore = CaptureNamedScrollSnapshot();");
            sb.AppendLine("            var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");

            // 0.26.6+ [MetaMethod(DeepStateCheck = X)] — pre snapshot of client state.
            if (deepStateCheck != 0)
            {
                sb.AppendLine("            byte[]? _dsClientPreBytes = null; uint _dsClientPreCrc = 0;");
                sb.AppendLine("            byte[]? _dsClientPostBytes = null; uint _dsClientPostCrc = 0;");
                if ((deepStateCheck & 1) != 0)
                {
                    sb.AppendLine("            _dsClientPreBytes = _serializer.Pack(_state).ToArray();");
                    sb.AppendLine("            _dsClientPreCrc = SharedMeta.Core.Diagnostics.DeepStateHashing.Fnv1a32(_dsClientPreBytes);");
                }
            }

            sb.AppendLine("            try");
            sb.AppendLine("            {");

            // Execute locally first
            if (hasDeepDesync)
            {
                sb.AppendLine("                var _ddRoot = new SharedMeta.Core.Patch.PatchNode(-1);");
                sb.AppendLine($"                ctx.PatchWrapper = new {stateTypeName}PatchWrapper(_state, _ddRoot, _serializer);");
            }
            var optServiceRef = hasDeepDesync ? "_patchTrackedService" : "_service";
            if (isVoidReturn)
            {
                sb.AppendLine($"                {awaitPrefix}{optServiceRef}.{methodName}({callArgs});");
            }
            else
            {
                sb.AppendLine($"                localResult = {awaitPrefix}{optServiceRef}.{methodName}({callArgs});");
            }

            sb.AppendLine("            }");
            sb.AppendLine($"            catch (Exception ex) {{ MetaContextAccessor.Current = null; _tracker.Discard(); SetError(ex, global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, \"{methodAlias}\"); throw; }}");
            sb.AppendLine("            MetaContextAccessor.Current = null;");
            sb.AppendLine("            _tracker.FlushAndNotify();");
            sb.AppendLine("            _stateContainer.NotifyMutated();");
            // 0.26.6+ Post snapshot of client state + build PayloadDebug for the upcoming RPC.
            if (deepStateCheck != 0)
            {
                if ((deepStateCheck & 2) != 0)
                {
                    sb.AppendLine("            _dsClientPostBytes = _serializer.Pack(_state).ToArray();");
                    sb.AppendLine("            _dsClientPostCrc = SharedMeta.Core.Diagnostics.DeepStateHashing.Fnv1a32(_dsClientPostBytes);");
                }
                sb.AppendLine("            var _dsDebug = new SharedMeta.Core.PayloadDebug { PreStateCrc = _dsClientPreCrc, PostStateCrc = _dsClientPostCrc };");
            }
            // Capture local deltas synchronously — MUST happen before the fire-and-forget ContinueWith
            // or subsequent Optimistic calls on the same ApiClient will race and advance the random
            // state further by the time this continuation runs, producing a phantom desync.
            sb.AppendLine("            var localScrollDelta = (_optimisticRandom?.ScrollId ?? 0) - scrollIdBefore;");
            sb.AppendLine("            var localNamedScrollDeltas = ComputeLocalNamedScrollDeltas(namedScrollsBefore);");
            // Deep desync: compute local CRC before fire-and-forget (captures _ddRoot from try scope)
            if (hasDeepDesync)
            {
                sb.AppendLine("            uint _ddLocalCrc = 0;");
                sb.AppendLine("            byte[] _ddLocalPatchBytes = System.Array.Empty<byte>();");
                sb.AppendLine("            if (ctx.PatchWrapper is SharedMeta.Core.Patch.IPatchWrapper _ddPw && _ddPw.Node != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                _ddPw.Node.Prune();");
                sb.AppendLine("                if (_ddPw.Node.HasChanges)");
                sb.AppendLine("                {");
                sb.AppendLine("                    _ddLocalPatchBytes = _serializer.Pack(_ddPw.Node).ToArray();");
                sb.AppendLine("                    _ddLocalCrc = SharedMeta.Core.Patch.PatchCrc.Compute(_ddLocalPatchBytes);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
            sb.AppendLine();

            // Serialize arguments based on serializer
            GenerateArgumentSerialization(sb, method, transforms, paramCount, serializer);
            sb.AppendLine();

            var dsDebugArg = deepStateCheck != 0 ? ", debug: _dsDebug" : "";

            // Fire-and-forget to server with background validation
            if (isVoidReturn)
            {
                sb.AppendLine($"            _ = _network.CallVoidAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks{dsDebugArg})");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                sb.AppendLine($"                        CompareAndReportNamedScrollDesync(\"{methodAlias}\", t.Result.NamedRandomScrollDeltas, localNamedScrollDeltas);");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                EmitDeepStateDesyncCallback(sb, methodAlias, stateTypeName, deepStateCheck, "                        ");
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[Optimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
            }
            else
            {
                if (skipServerOnFalse)
                {
                    // [MetaMethod(SkipServerOnFalse = true)] — skip the server round-trip when the
                    // local impl returned default(T) (e.g. a validation method that returned false
                    // without mutating state). The server would replay the same logic and return
                    // the same default; saving the trip preserves semantics and cuts traffic for
                    // no-op calls. The contract is "if you return default, you must not have
                    // mutated state" — same client/server replay assumption Optimistic always made.
                    sb.AppendLine($"            if (!System.Collections.Generic.EqualityComparer<{returnType}>.Default.Equals(localResult, default!))");
                    sb.AppendLine("            {");
                }
                sb.AppendLine($"            _ = _network.CallBytesAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks{dsDebugArg})");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                GenerateOptimisticResultDeserialization(sb, returnType, methodAlias, serializer, resultComparer);
                sb.AppendLine();
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                sb.AppendLine($"                        CompareAndReportNamedScrollDesync(\"{methodAlias}\", t.Result.NamedRandomScrollDeltas, localNamedScrollDeltas);");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                EmitDeepStateDesyncCallback(sb, methodAlias, stateTypeName, deepStateCheck, "                        ");
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[Optimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
                if (skipServerOnFalse)
                {
                    sb.AppendLine("            }");
                }
                sb.AppendLine();
                sb.AppendLine("            return localResult;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // Sync mirror of GenerateOptimisticMethod. Preconditions (validated in GenerateMethod):
        //   - Mode is Optimistic or Local
        //   - Service method has a non-Task return type (no await needed on the impl call)
        // Body parity with the async version — identical mutations, context, tracker, fire-and-forget
        // continuation. Only differences: no `async`/`Task<>` wrapper, no `await` on impl call.
        private static void GenerateOptimisticMethodSync(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, int paramCount, string callArgs, List<ParameterTransform> transforms,
            DetectedSerializer serializer, string interfaceName, string namespaceName, string? stateTypeName, bool hasDeepDesync = false, bool skipServerOnFalse = false, ResultComparerInfo? resultComparer = null, int deepStateCheck = 0)
        {
            var methodName = method.Identifier.Text;
            var methodVersion = GetMethodVersion(method);
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string syncReturnType = isVoidReturn ? "void" : returnType;

            sb.AppendLine($"        private {syncReturnType} {methodName}Sync_Optimistic({parameters})");
            sb.AppendLine("        {");

            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine($"            ctx.EntityId = _network.EntityId ?? string.Empty;");
            sb.AppendLine("            ctx.CallerId = _network.PlayerId;");
            sb.AppendLine("            ctx.ServerTimeTicks = serverTimeTicks;");
            sb.AppendLine("            ctx.Random = _optimisticRandom;");
            sb.AppendLine("            ctx.Config = _config;");
            sb.AppendLine("            ctx.Configs = _serviceConfigs;");
            sb.AppendLine("            ctx.NamedRandoms = _namedRandoms;");
            // 0.20.0: set instance Context on impl(s) directly + wire the client-side sibling
            // resolver so user code can call Get{Iface}SiblingAsync() / GetI{Iface}(self) and
            // get back a transient impl bound to ctx — same as server's sibling-bypass flow.
            sb.AppendLine("            ctx.SiblingServiceResolver = type => _crossEntityResolver?.ResolveSibling(type, ctx);");
            sb.AppendLine("            _service.Context = ctx;");
            if (hasDeepDesync)
                sb.AppendLine("            _patchTrackedService.Context = ctx;");
            sb.AppendLine("            MetaContextAccessor.Current = ctx;");

            if (!isVoidReturn)
            {
                sb.AppendLine($"            {returnType} localResult;");
            }

            sb.AppendLine("            var scrollIdBefore = _optimisticRandom?.ScrollId ?? 0;");
            sb.AppendLine("            var namedScrollsBefore = CaptureNamedScrollSnapshot();");
            sb.AppendLine("            var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");

            // 0.26.6+ [MetaMethod(DeepStateCheck = X)] — pre snapshot of client state.
            if (deepStateCheck != 0)
            {
                sb.AppendLine("            byte[]? _dsClientPreBytes = null; uint _dsClientPreCrc = 0;");
                sb.AppendLine("            byte[]? _dsClientPostBytes = null; uint _dsClientPostCrc = 0;");
                if ((deepStateCheck & 1) != 0)
                {
                    sb.AppendLine("            _dsClientPreBytes = _serializer.Pack(_state).ToArray();");
                    sb.AppendLine("            _dsClientPreCrc = SharedMeta.Core.Diagnostics.DeepStateHashing.Fnv1a32(_dsClientPreBytes);");
                }
            }

            sb.AppendLine("            try");
            sb.AppendLine("            {");

            if (hasDeepDesync)
            {
                sb.AppendLine("                var _ddRoot = new SharedMeta.Core.Patch.PatchNode(-1);");
                sb.AppendLine($"                ctx.PatchWrapper = new {stateTypeName}PatchWrapper(_state, _ddRoot, _serializer);");
            }
            var optServiceRef = hasDeepDesync ? "_patchTrackedService" : "_service";
            if (isVoidReturn)
            {
                sb.AppendLine($"                {optServiceRef}.{methodName}({callArgs});");
            }
            else
            {
                sb.AppendLine($"                localResult = {optServiceRef}.{methodName}({callArgs});");
            }

            sb.AppendLine("            }");
            sb.AppendLine($"            catch (Exception ex) {{ MetaContextAccessor.Current = null; _tracker.Discard(); SetError(ex, global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, \"{methodAlias}\"); throw; }}");
            sb.AppendLine("            MetaContextAccessor.Current = null;");
            sb.AppendLine("            _tracker.FlushAndNotify();");
            sb.AppendLine("            _stateContainer.NotifyMutated();");
            // 0.26.6+ Post snapshot of client state + build PayloadDebug for the upcoming RPC.
            if (deepStateCheck != 0)
            {
                if ((deepStateCheck & 2) != 0)
                {
                    sb.AppendLine("            _dsClientPostBytes = _serializer.Pack(_state).ToArray();");
                    sb.AppendLine("            _dsClientPostCrc = SharedMeta.Core.Diagnostics.DeepStateHashing.Fnv1a32(_dsClientPostBytes);");
                }
                sb.AppendLine("            var _dsDebug = new SharedMeta.Core.PayloadDebug { PreStateCrc = _dsClientPreCrc, PostStateCrc = _dsClientPostCrc };");
            }
            // Capture local deltas synchronously — MUST happen before the fire-and-forget ContinueWith
            // or subsequent Optimistic calls on the same ApiClient will race and advance the random
            // state further by the time this continuation runs, producing a phantom desync.
            sb.AppendLine("            var localScrollDelta = (_optimisticRandom?.ScrollId ?? 0) - scrollIdBefore;");
            sb.AppendLine("            var localNamedScrollDeltas = ComputeLocalNamedScrollDeltas(namedScrollsBefore);");

            if (hasDeepDesync)
            {
                sb.AppendLine("            uint _ddLocalCrc = 0;");
                sb.AppendLine("            byte[] _ddLocalPatchBytes = System.Array.Empty<byte>();");
                sb.AppendLine("            if (ctx.PatchWrapper is SharedMeta.Core.Patch.IPatchWrapper _ddPw && _ddPw.Node != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                _ddPw.Node.Prune();");
                sb.AppendLine("                if (_ddPw.Node.HasChanges)");
                sb.AppendLine("                {");
                sb.AppendLine("                    _ddLocalPatchBytes = _serializer.Pack(_ddPw.Node).ToArray();");
                sb.AppendLine("                    _ddLocalCrc = SharedMeta.Core.Patch.PatchCrc.Compute(_ddLocalPatchBytes);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
            sb.AppendLine();

            GenerateArgumentSerialization(sb, method, transforms, paramCount, serializer);
            sb.AppendLine();

            var dsDebugArg = deepStateCheck != 0 ? ", debug: _dsDebug" : "";

            // Fire-and-forget to server. Identical to the async variant — the Task is discarded
            // and never awaited, so the server round-trip happens in the background while the
            // sync method returns immediately.
            if (isVoidReturn)
            {
                sb.AppendLine($"            _ = _network.CallVoidAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks{dsDebugArg})");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                sb.AppendLine($"                        CompareAndReportNamedScrollDesync(\"{methodAlias}\", t.Result.NamedRandomScrollDeltas, localNamedScrollDeltas);");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                EmitDeepStateDesyncCallback(sb, methodAlias, stateTypeName, deepStateCheck, "                        ");
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[Optimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
            }
            else
            {
                if (skipServerOnFalse)
                {
                    // [MetaMethod(SkipServerOnFalse = true)] — same semantics as the async path,
                    // mirrored here for the Sync API. Skip the server round-trip when local
                    // returned default(T); contract: no state mutation in the default-return branch.
                    sb.AppendLine($"            if (!System.Collections.Generic.EqualityComparer<{returnType}>.Default.Equals(localResult, default!))");
                    sb.AppendLine("            {");
                }
                sb.AppendLine($"            _ = _network.CallBytesAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks{dsDebugArg})");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                GenerateOptimisticResultDeserialization(sb, returnType, methodAlias, serializer, resultComparer);
                sb.AppendLine();
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                sb.AppendLine($"                        CompareAndReportNamedScrollDesync(\"{methodAlias}\", t.Result.NamedRandomScrollDeltas, localNamedScrollDeltas);");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                EmitDeepStateDesyncCallback(sb, methodAlias, stateTypeName, deepStateCheck, "                        ");
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[Optimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
                if (skipServerOnFalse)
                {
                    sb.AppendLine("            }");
                }
                sb.AppendLine();
                sb.AppendLine("            return localResult;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateCrossOptimisticMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs, List<ParameterTransform> transforms,
            DetectedSerializer serializer, string interfaceName, string namespaceName, string? stateTypeName, bool hasDeepDesync = false, ResultComparerInfo? resultComparer = null)
        {
            var methodName = method.Identifier.Text;
            var methodVersion = GetMethodVersion(method);
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {ContextInjectionGenerator.AsyncMethodName(methodName)}_CrossOptimistic({parameters})");
            sb.AppendLine("        {");

            // Capture server time before local execution
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Set context with CrossEntityResolver
            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine($"            ctx.EntityId = _network.EntityId ?? string.Empty;");
            sb.AppendLine("            ctx.CallerId = _network.PlayerId;");
            sb.AppendLine("            ctx.ServerTimeTicks = serverTimeTicks;");
            sb.AppendLine("            ctx.CrossEntityResolver = _crossEntityResolver;");
            sb.AppendLine("            ctx.Random = _optimisticRandom;");
            sb.AppendLine("            ctx.Config = _config;");
            sb.AppendLine("            ctx.Configs = _serviceConfigs;");
            sb.AppendLine("            ctx.NamedRandoms = _namedRandoms;");
            // 0.20.0: set instance Context on impl(s) directly + wire the client-side sibling
            // resolver so user code can call Get{Iface}SiblingAsync() / GetI{Iface}(self) and
            // get back a transient impl bound to ctx — same as server's sibling-bypass flow.
            sb.AppendLine("            ctx.SiblingServiceResolver = type => _crossEntityResolver?.ResolveSibling(type, ctx);");
            sb.AppendLine("            _service.Context = ctx;");
            if (hasDeepDesync)
                sb.AppendLine("            _patchTrackedService.Context = ctx;");
            sb.AppendLine("            MetaContextAccessor.Current = ctx;");

            if (!isVoidReturn)
            {
                sb.AppendLine($"            {returnType} localResult;");
            }
            sb.AppendLine("            List<CrossEntityLocalResult> localCrossResults;");

            // Capture scrollId before local execution for desync detection
            sb.AppendLine("            var scrollIdBefore = _optimisticRandom?.ScrollId ?? 0;");
            sb.AppendLine("            var namedScrollsBefore = CaptureNamedScrollSnapshot();");
            sb.AppendLine("            var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");

            sb.AppendLine("            try");
            sb.AppendLine("            {");
            if (hasDeepDesync)
            {
                sb.AppendLine("                var _ddRoot = new SharedMeta.Core.Patch.PatchNode(-1);");
                sb.AppendLine($"                ctx.PatchWrapper = new {stateTypeName}PatchWrapper(_state, _ddRoot, _serializer);");
            }
            var coServiceRef = hasDeepDesync ? "_patchTrackedService" : "_service";
            if (isVoidReturn)
            {
                sb.AppendLine($"                {awaitPrefix}{coServiceRef}.{methodName}({callArgs});");
            }
            else
            {
                sb.AppendLine($"                localResult = {awaitPrefix}{coServiceRef}.{methodName}({callArgs});");
            }
            sb.AppendLine("            }");
            sb.AppendLine($"            catch (Exception ex) {{ MetaContextAccessor.Current = null; _tracker.Discard(); SetError(ex, global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, \"{methodAlias}\"); throw; }}");
            sb.AppendLine("            MetaContextAccessor.Current = null;");
            sb.AppendLine("            _tracker.FlushAndNotify();");
            sb.AppendLine("            _stateContainer.NotifyMutated();");
            // Capture local deltas synchronously — MUST happen before the fire-and-forget ContinueWith
            // or subsequent Optimistic calls on the same ApiClient will race and advance the random
            // state further by the time this continuation runs, producing a phantom desync.
            sb.AppendLine("            var localScrollDelta = (_optimisticRandom?.ScrollId ?? 0) - scrollIdBefore;");
            sb.AppendLine("            var localNamedScrollDeltas = ComputeLocalNamedScrollDeltas(namedScrollsBefore);");
            sb.AppendLine("            localCrossResults = _crossEntityResolver?.TakeRecordedResults() ?? new();");
            // Deep desync: capture local CRC + patch bytes before fire-and-forget
            if (hasDeepDesync)
            {
                sb.AppendLine("            uint _ddLocalCrc = 0;");
                sb.AppendLine("            byte[] _ddLocalPatchBytes = System.Array.Empty<byte>();");
                sb.AppendLine("            if (ctx.PatchWrapper is SharedMeta.Core.Patch.IPatchWrapper _ddPw && _ddPw.Node != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                _ddPw.Node.Prune();");
                sb.AppendLine("                if (_ddPw.Node.HasChanges)");
                sb.AppendLine("                {");
                sb.AppendLine("                    _ddLocalPatchBytes = _serializer.Pack(_ddPw.Node).ToArray();");
                sb.AppendLine("                    _ddLocalCrc = SharedMeta.Core.Patch.PatchCrc.Compute(_ddLocalPatchBytes);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
            sb.AppendLine();

            // Serialize arguments
            GenerateArgumentSerialization(sb, method, transforms, paramCount, serializer);
            sb.AppendLine();

            // Fire-and-forget to server with IsCrossOptimistic flag + validation
            if (isVoidReturn)
            {
                sb.AppendLine($"            _ = _network.CallVoidAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, isCrossOptimistic: true, serverTimeTicks: serverTimeTicks)");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                sb.AppendLine($"                        CompareAndReportNamedScrollDesync(\"{methodAlias}\", t.Result.NamedRandomScrollDeltas, localNamedScrollDeltas);");
                sb.AppendLine("                        if (t.Result.CrossEntityOperations is { Count: > 0 } serverCrossOps)");
                sb.AppendLine("                        {");
                sb.AppendLine("                            for (int i = 0; i < serverCrossOps.Count && i < localCrossResults.Count; i++)");
                sb.AppendLine("                            {");
                sb.AppendLine("                                // Cross-entity desync comparison is logged but not thrown for void methods");
                sb.AppendLine("                            }");
                sb.AppendLine("                        }");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[CrossOptimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
            }
            else
            {
                sb.AppendLine($"            _ = _network.CallBytesAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, isCrossOptimistic: true, serverTimeTicks: serverTimeTicks)");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                // Compare main result
                GenerateOptimisticResultDeserialization(sb, returnType, methodAlias, serializer, resultComparer);
                sb.AppendLine();
                sb.AppendLine("                        // Compare cross-entity results");
                sb.AppendLine("                        if (t.Result.CrossEntityOperations is { Count: > 0 } serverCrossOps)");
                sb.AppendLine("                        {");
                sb.AppendLine("                            for (int i = 0; i < serverCrossOps.Count && i < localCrossResults.Count; i++)");
                sb.AppendLine("                            {");
                sb.AppendLine("                                // Object-level comparison; typed comparison requires generated per-method code");
                sb.AppendLine("                                _diagnostics?.OnCrossEntityResult(serverCrossOps[i].EntityId, serverCrossOps[i].MethodId, serverCrossOps[i].ResultBytes.IsEmpty ? null : serverCrossOps[i].ResultBytes.ToArray());");
                sb.AppendLine("                            }");
                sb.AppendLine("                        }");
                sb.AppendLine();
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                sb.AppendLine($"                        CompareAndReportNamedScrollDesync(\"{methodAlias}\", t.Result.NamedRandomScrollDeltas, localNamedScrollDeltas);");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[CrossOptimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
                sb.AppendLine();
                sb.AppendLine("            return localResult;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateServerPatchMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs, List<ParameterTransform> transforms,
            DetectedSerializer serializer, string interfaceName, string namespaceName, string? stateTypeName)
        {
            var methodName = method.Identifier.Text;
            var methodVersion = GetMethodVersion(method);
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            // Compute PatchApplier full name: stateTypeName + "PatchApplier"
            var applierName = stateTypeName + "PatchApplier";

            sb.AppendLine($"        private async {asyncReturnType} {ContextInjectionGenerator.AsyncMethodName(methodName)}_ServerPatch({parameters})");
            sb.AppendLine("        {");

            // Capture server time before the call
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Serialize arguments
            GenerateArgumentSerialization(sb, method, transforms, paramCount, serializer);
            sb.AppendLine();

            // Suppress broadcasts during RPC + patch application
            sb.AppendLine("            _network.SuppressBroadcasts();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");

            // Call server — always use CallBytesAsync (we need ResultBytes + PatchBytes)
            if (isVoidReturn)
            {
                sb.AppendLine($"                var response = await _network.CallVoidAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            else
            {
                sb.AppendLine($"                var response = await _network.CallBytesAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            sb.AppendLine();

            // Apply patch or fallback to replay
            sb.AppendLine("                var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine("                if (response.PatchBytes is { Length: > 0 } patchData)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    var patch = _serializer.Unpack<PatchNode>(patchData);");
            sb.AppendLine($"                    {applierName}.Apply(_state, patch, _serializer);");
            sb.AppendLine($"                    _stateContainer.NotifyMutated();");
            sb.AppendLine("                    _optimisticRandom?.Skip(response.RandomScrollDelta);");
            sb.AppendLine("                    ApplyNamedScrollSkips(response.NamedRandomScrollDeltas);");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            sb.AppendLine("                    // Fallback: server didn't generate patch, replay normally");
            sb.AppendLine("                    SetContext(response.ReplayContext, _network.PlayerId, response.ServerTimeTicks);");
            if (isVoidReturn)
            {
                sb.AppendLine($"                    {awaitPrefix}_service.{methodName}({callArgs});");
            }
            else
            {
                sb.AppendLine($"                    {awaitPrefix}_service.{methodName}({callArgs});");
            }
            sb.AppendLine("                    ClearContext();");
            sb.AppendLine("                }");
            sb.AppendLine();

            // Replay trigger operations (may also have patches)
            sb.AppendLine($"                ReplayTriggerOperations(response.TriggerOperations, _network.PlayerId, response.ServerTimeTicks);");
            sb.AppendLine("                _tracker.FlushAndNotify();");
            sb.AppendLine("                _stateContainer.NotifyMutated();");
            sb.AppendLine("                }");
            sb.AppendLine($"                catch (Exception ex) {{ _tracker.Discard(); SetError(ex, global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, \"{methodAlias}\"); throw; }}");

            // Return server result
            if (!isVoidReturn)
            {
                sb.AppendLine();
                GenerateResultDeserializationIndented(sb, returnType, serializer);
                sb.AppendLine("                return serverResult;");
            }

            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                _network.ResumeBroadcasts();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateServerReplaceMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs, List<ParameterTransform> transforms,
            DetectedSerializer serializer, string? stateTypeName, string interfaceName, string namespaceName)
        {
            var methodName = method.Identifier.Text;
            var methodVersion = GetMethodVersion(method);
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {ContextInjectionGenerator.AsyncMethodName(methodName)}_ServerReplace({parameters})");
            sb.AppendLine("        {");

            // Capture server time before the call
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Serialize arguments
            GenerateArgumentSerialization(sb, method, transforms, paramCount, serializer);
            sb.AppendLine();

            // Suppress broadcasts during RPC + state replacement
            sb.AppendLine("            _network.SuppressBroadcasts();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");

            // Call server
            if (isVoidReturn)
            {
                sb.AppendLine($"                var response = await _network.CallVoidAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            else
            {
                sb.AppendLine($"                var response = await _network.CallBytesAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            sb.AppendLine();

            // Replace state or fallback to replay
            sb.AppendLine("                var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine("                if (response.StateBytes is { Length: > 0 } stateData)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    // Replace through the container — bumps MutationCount, fires OnMutated.");
            sb.AppendLine($"                    _stateContainer.Replace(_serializer.Unpack<{stateTypeName}>(stateData)!);");
            sb.AppendLine("                    _optimisticRandom?.Skip(response.RandomScrollDelta);");
            sb.AppendLine("                    ApplyNamedScrollSkips(response.NamedRandomScrollDeltas);");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            sb.AppendLine("                    // Fallback: server didn't send StateBytes, replay normally");
            sb.AppendLine("                    SetContext(response.ReplayContext, _network.PlayerId, response.ServerTimeTicks);");
            sb.AppendLine($"                    {awaitPrefix}_service.{methodName}({callArgs});");
            sb.AppendLine("                    ClearContext();");
            sb.AppendLine($"                    ReplayTriggerOperations(response.TriggerOperations, _network.PlayerId, response.ServerTimeTicks);");
            sb.AppendLine($"                    _stateContainer.NotifyMutated();");
            sb.AppendLine("                }");
            sb.AppendLine();

            sb.AppendLine("                _tracker.FlushAndNotify();");
            sb.AppendLine($"                OnStateRefreshed?.Invoke(_state);");
            sb.AppendLine("                }");
            sb.AppendLine($"                catch (Exception ex) {{ _tracker.Discard(); SetError(ex, global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, \"{methodAlias}\"); throw; }}");

            // Return server result
            if (!isVoidReturn)
            {
                sb.AppendLine();
                GenerateResultDeserializationIndented(sb, returnType, serializer);
                sb.AppendLine("                return serverResult;");
            }

            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                _network.ResumeBroadcasts();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateOptimisticResultDeserialization(StringBuilder sb, string returnType,
            string methodAlias, DetectedSerializer serializer, ResultComparerInfo? resultComparer = null)
        {
            if (resultComparer != null)
            {
                // Comparer-based mismatch detection. Deserialize the server result up front
                // (wrapped in try/catch — a deserialization failure is itself a desync). Then
                // call the structural comparer; only serialize localResult on the mismatch path
                // since bytes are needed only for the desync report.
                var fieldName = "_resultComparer_" + SanitizeTypeNameForIdentifier(resultComparer.TargetTypeFullName);
                sb.AppendLine($"                        {returnType} serverResult = default!;");
                sb.AppendLine("                        bool serverDeserializedOk = false;");
                sb.AppendLine("                        try");
                sb.AppendLine("                        {");
                if (serializer == DetectedSerializer.MemoryPack)
                    sb.AppendLine($"                            serverResult = MemoryPackSerializer.Deserialize<{returnType}>(t.Result.ResultBytes)!;");
                else
                    sb.AppendLine($"                            serverResult = _serializer.Unpack<{returnType}>(t.Result.ResultBytes)!;");
                sb.AppendLine("                            serverDeserializedOk = true;");
                sb.AppendLine("                        }");
                sb.AppendLine("                        catch (Exception) { }");
                sb.AppendLine($"                        if (!serverDeserializedOk || !{fieldName}.AreEqual(serverResult, localResult))");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnResultMismatch(ServiceName, \"{methodAlias}\", serverResult, localResult);");
                if (serializer == DetectedSerializer.MemoryPack)
                    sb.AppendLine($"                            var localResultBytes = MemoryPackSerializer.Serialize(localResult);");
                else
                    sb.AppendLine($"                            var localResultBytes = _serializer.Pack(localResult).ToArray();");
                GenerateResultMismatchReport(sb, methodAlias, "t.Result.ResultBytes", "localResultBytes", "                            ");
                sb.AppendLine("                        }");
                return;
            }

            // Use byte-level comparison to avoid reference equality issues with class types
            GenerateResultByteComparison(sb, returnType, serializer, "t.Result.ResultBytes", "                        ");
            sb.AppendLine("                        {");
            // Deserialize server result only on mismatch (for diagnostics)
            // Wrapped in try-catch: if deserialization fails (e.g., type mismatch during reconnection),
            // we still log the mismatch without throwing an unobserved exception
            sb.AppendLine("                            try");
            sb.AppendLine("                            {");
            if (serializer == DetectedSerializer.MemoryPack)
            {
                sb.AppendLine($"                                var serverResult = MemoryPackSerializer.Deserialize<{returnType}>(t.Result.ResultBytes)!;");
            }
            else
            {
                sb.AppendLine($"                                var serverResult = _serializer.Unpack<{returnType}>(t.Result.ResultBytes)!;");
            }
            sb.AppendLine($"                                _diagnostics?.OnResultMismatch(ServiceName, \"{methodAlias}\", serverResult, localResult);");
            sb.AppendLine("                            }");
            sb.AppendLine("                            catch (Exception)");
            sb.AppendLine("                            {");
            sb.AppendLine($"                                _diagnostics?.OnResultMismatch(ServiceName, \"{methodAlias}\", default({returnType})!, localResult);");
            sb.AppendLine("                            }");
            // Fire-and-forget desync follow-up report (server gates by DesyncReportingEnabled)
            GenerateResultMismatchReport(sb, methodAlias, "t.Result.ResultBytes", "localResultBytes", "                            ");
            sb.AppendLine("                        }");
        }

        private static void GenerateContextMethods(StringBuilder sb, string? stateTypeName, bool hasDeepDesync = false)
        {
            sb.AppendLine("        private void SetContext(byte[] replayContext)");
            sb.AppendLine("        {");
            sb.AppendLine("            SetContext(replayContext, null, 0, default);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void SetContext(byte[] replayContext, string? callerId, long serverTimeTicks = 0, SharedMeta.Core.MetaConfigVersion executedConfigVersion = default)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine($"            ctx.EntityId = _network.EntityId ?? string.Empty;");
            sb.AppendLine("            ctx.CallerId = callerId;");
            sb.AppendLine("            ctx.ServerTimeTicks = serverTimeTicks;");
            sb.AppendLine("            ctx.BeginReplay(replayContext);");
            sb.AppendLine("            ctx.Random = _optimisticRandom;");
            sb.AppendLine("            // 0.21.0: replay under the server's actually-executed config version when it");
            sb.AppendLine("            // differs from this session's pin (e.g. mid-session admin rollout, Global entity");
            sb.AppendLine("            // observer at a different version). _configResolver is wired by");
            sb.AppendLine("            // MetaServiceResolver to EntityConnection.ResolveConfigForBroadcast, which lazily");
            sb.AppendLine("            // fetches and caches per-version configs. default(MetaConfigVersion) (no config");
            sb.AppendLine("            // system) and own-session matches skip resolver and use _config directly.");
            sb.AppendLine("            ctx.Config = (executedConfigVersion.Major == 0 && executedConfigVersion.Minor == 0 && executedConfigVersion.Patch == 0)");
            sb.AppendLine("                ? _config");
            sb.AppendLine("                : (_configResolver?.Invoke(executedConfigVersion) ?? _config);");
            // [ServiceConfig] entries don't participate in per-broadcast drift resolution
            // (only the legacy primary config does, via _configResolver above) — always the
            // session's resolved set, same simplification the legacy config's own
            // EntityReplayDispatcher path already takes for foreign-service replay.
            sb.AppendLine("            ctx.Configs = _serviceConfigs;");
            sb.AppendLine("            ctx.ServerRandom = new MetaRandomReplayer(ctx);");
            sb.AppendLine("            ctx.NamedRandoms = _namedRandoms;");
            // 0.20.0: set instance Context on impl(s) directly + wire the client-side sibling
            // resolver so user code can call Get{Iface}SiblingAsync() / GetI{Iface}(self) and
            // get back a transient impl bound to ctx — same as server's sibling-bypass flow.
            sb.AppendLine("            ctx.SiblingServiceResolver = type => _crossEntityResolver?.ResolveSibling(type, ctx);");
            sb.AppendLine("            _service.Context = ctx;");
            if (hasDeepDesync)
                sb.AppendLine("            _patchTrackedService.Context = ctx;");
            sb.AppendLine("            MetaContextAccessor.Current = ctx;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void ClearContext()");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (MetaContextAccessor.Current is ClientMetaContext<{stateTypeName}> ctx)");
            sb.AppendLine("            {");
            sb.AppendLine("                ctx.EndReplay();");
            sb.AppendLine("            }");
            sb.AppendLine("            MetaContextAccessor.Current = null;");
            sb.AppendLine("        }");
            sb.AppendLine();

            // 0.22.0: SetupQueryContext / RestoreQueryContext — read-only context bracket for
            // [MetaMethod(Mode = ExecutionMode.Query)] local sync wrappers. The local query
            // path calls _service.{method} synchronously and the impl reads Context.State /
            // Context.EntityId etc., which require MetaContextAccessor.Current to be set.
            // Async methods set context inline (SetContext/ClearContext); the local query path
            // needed its own bracket because BeginReplay/EndReplay aren't applicable (no replay
            // payload). The previous-context save/restore avoids clobbering an in-flight context
            // when a Query is invoked from inside an async method body.
            sb.AppendLine("        private SharedMeta.Core.MetaContext? SetupQueryContext()");
            sb.AppendLine("        {");
            sb.AppendLine("            var prev = MetaContextAccessor.Current;");
            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine("            ctx.EntityId = _network.EntityId ?? string.Empty;");
            sb.AppendLine("            ctx.Config = _config;");
            sb.AppendLine("            ctx.Configs = _serviceConfigs;");
            sb.AppendLine("            ctx.NamedRandoms = _namedRandoms;");
            sb.AppendLine("            ctx.SiblingServiceResolver = type => _crossEntityResolver?.ResolveSibling(type, ctx);");
            sb.AppendLine("            _service.Context = ctx;");
            if (hasDeepDesync)
                sb.AppendLine("            _patchTrackedService.Context = ctx;");
            sb.AppendLine("            MetaContextAccessor.Current = ctx;");
            sb.AppendLine("            return prev;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void RestoreQueryContext(SharedMeta.Core.MetaContext? prev)");
            sb.AppendLine("        {");
            sb.AppendLine("            MetaContextAccessor.Current = prev;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// Impl-declared methods keep the type names their own file wrote. Nothing guarantees
        /// those resolve here, so bring in the namespaces their signatures actually reference.
        /// </summary>
        private static void EmitImplDeclaredUsings(
            StringBuilder sb, INamedTypeSymbol symbol, Compilation? compilation, string[] alreadyEmitted)
        {
            if (compilation == null) return;
            foreach (var ns in ImplDeclaredMethods.SignatureNamespacesForService(symbol, compilation))
            {
                if (System.Array.IndexOf(alreadyEmitted, ns) >= 0) continue;
                sb.AppendLine($"using {ns};");
            }
        }

        private static void GenerateHandleBroadcast(StringBuilder sb,
            List<MethodDeclarationSyntax> methods,
            string interfaceName, string namespaceName, string implClassName, string? stateTypeName,
            DetectedSerializer serializer, Compilation? compilation)
        {
            sb.AppendLine("        private void HandleBroadcast(NetworkBroadcast broadcast)");
            sb.AppendLine("        {");
            sb.AppendLine("            // No own-RPC echo filter: the server excludes the originator from fan-out when");
            sb.AppendLine("            // it already applied the effect locally (DistributeBroadcasts excludePlayerId),");
            sb.AppendLine("            // so a received broadcast is never a duplicate of a local application.");
            // 0.24.0+ Route by client-local MethodId — the wire no longer carries ServiceName.
            // A jump table on ushort is cheap, and DispatchServiceBroadcast's inner switch is the
            // existing failure boundary (default branch logs unknown id).
            // Service may consist entirely of Query/Signal methods (no broadcasts) — in that case
            // emit no switch at all rather than an empty body whose dispatch branches would be
            // unreachable / malformed.
            var broadcastingMethods = methods.Where(m => !IsQueryMethod(m) && !IsLocalQueryMethod(m) && !IsSignalMethod(m)).ToList();
            if (broadcastingMethods.Count > 0)
            {
                sb.AppendLine("            switch (broadcast.MethodId)");
                sb.AppendLine("            {");
                foreach (var method in broadcastingMethods)
                {
                    var alias = GetMethodAlias(method, method.Identifier.Text);
                    var version = GetMethodVersion(method);
                    var idConst = "global::" + namespaceName + ".Generated.GameMethodIds." +
                        SignatureHashGenerator.MakeMethodIdConstName(interfaceName, alias, version);
                    sb.AppendLine($"                case {idConst}:");
                }
                sb.AppendLine("                    DispatchServiceBroadcast(broadcast);");
                sb.AppendLine("                    return;");
                sb.AppendLine("            }");
            }
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        private void DispatchServiceBroadcast(NetworkBroadcast broadcast)");
            sb.AppendLine("        {");
            sb.AppendLine("            var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            // 0.24.0+ Switch on client's local method id (translated from server's id by
            // DispatcherNetworkAdapter via ServerToClientMethodIds). Each (alias, version)
            // tuple is a distinct case from GameMethodIds — no nested version switch needed.
            sb.AppendLine("            switch (broadcast.MethodId)");
            sb.AppendLine("            {");

            // Compute PatchApplier full name
            var applierName = stateTypeName + "PatchApplier";

            foreach (var method in methods.Where(m => !IsQueryMethod(m) && !IsLocalQueryMethod(m)))
            {
                var alias = GetMethodAlias(method, method.Identifier.Text);
                var version = GetMethodVersion(method);
                var idConst = "global::" + namespaceName + ".Generated.GameMethodIds." + SignatureHashGenerator.MakeMethodIdConstName(interfaceName, alias, version);
                sb.AppendLine($"                case {idConst}:");
                sb.AppendLine("                {");
                EmitBroadcastReplayBody(sb, method, alias, serializer, compilation, indent: "                    ");
                sb.AppendLine("                    break;");
                sb.AppendLine("                }");
            }

            // Sentinel / unknown: server emitted a method this client doesn't know (server-only,
            // newer-version, removed-in-this-build). Server-side tailoring already includes
            // PatchBytes/StateBytes for these legacy subscribers; apply them and consume the
            // random scroll skips so the optimistic stream stays aligned. No body replay possible.
            sb.AppendLine("                default:");
            sb.AppendLine("                {");
            sb.AppendLine("                    if (broadcast.StateBytes is { Length: > 0 } || broadcast.PatchBytes is { Length: > 0 })");
            sb.AppendLine("                    {");
            sb.AppendLine("                        _optimisticRandom?.Skip(broadcast.RandomScrollDelta);");
            sb.AppendLine("                        ApplyNamedScrollSkips(broadcast.NamedRandomScrollDeltas);");
            sb.AppendLine("                    }");
            sb.AppendLine("                    else SharedMeta.Core.Logging.MetaLog.Warning($\"[{ServiceName}] broadcast for unknown MethodId=\" + broadcast.MethodId + \" arrived without patch/state bytes; ignoring.\");");
            sb.AppendLine("                    ReplayTriggerOperations(broadcast.TriggerOperations, broadcast.CallerId, broadcast.ServerTimeTicks);");
            sb.AppendLine("                    break;");
            sb.AppendLine("                }");

            sb.AppendLine("            }");
            sb.AppendLine("            _tracker.FlushAndNotify();");
            sb.AppendLine("            }");
            sb.AppendLine("            catch (Exception ex) { _tracker.Discard(); SetError(ex, broadcast.MethodId); throw; }");
            sb.AppendLine("        }");
            sb.AppendLine();

        }

        private static void GenerateTriggerReplayMethods(StringBuilder sb, List<MethodDeclarationSyntax> methods, string? stateTypeName,
            string interfaceName, string namespaceName)
        {
            var applierName = stateTypeName + "PatchApplier";

            // ReplayTriggerOperations helper. After the 0.24 unification, trigger ops are
            // canonical MetaOperation instances nested under the main MetaOperation.Triggers,
            // so we read service/method/payload directly off the trigger op (no .Call/.Response
            // split anymore).
            sb.AppendLine("        private void ReplayTriggerOperations(List<MetaOperation>? triggerOperations, string? callerId, long serverTimeTicks = 0)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (triggerOperations == null) return;");
            sb.AppendLine("            foreach (var triggerOp in triggerOperations)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (triggerOp.StateBytes is { Length: > 0 } stateData)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    _stateContainer.Replace(_serializer.Unpack<{stateTypeName}>(stateData)!);");
            sb.AppendLine("                    _optimisticRandom?.Skip(triggerOp.RandomScrollDelta);");
            sb.AppendLine("                    ApplyNamedScrollSkips(triggerOp.NamedRandomScrollDeltas);");
            sb.AppendLine("                }");
            sb.AppendLine("                else if (triggerOp.PatchBytes is { Length: > 0 } patchData)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    var patch = _serializer.Unpack<PatchNode>(patchData);");
            sb.AppendLine($"                    {applierName}.Apply(_state, patch, _serializer);");
            sb.AppendLine($"                    _stateContainer.NotifyMutated();");
            sb.AppendLine("                    _optimisticRandom?.Skip(triggerOp.RandomScrollDelta);");
            sb.AppendLine("                    ApplyNamedScrollSkips(triggerOp.NamedRandomScrollDeltas);");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            // triggerOp.ReplayPayload is ReadOnlyMemory<byte> on the wire DTO; SetContext takes
            // byte[] on the client side. Materialise at the boundary (cold-ish path: only fires
            // when a trigger has no patch/state and needs body replay).
            sb.AppendLine("                    SetContext(triggerOp.ReplayPayload.IsEmpty ? Array.Empty<byte>() : triggerOp.ReplayPayload.ToArray(), callerId, serverTimeTicks);");
            sb.AppendLine("                    DispatchTrigger(triggerOp.MethodId);");
            sb.AppendLine("                    ClearContext();");
            sb.AppendLine($"                    _stateContainer.NotifyMutated();");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();

            // DispatchTrigger switch — 0.24.0+ keyed by MethodId (server's global index
            // translated to client's local index on the wire). String name is no longer
            // available on MetaOperation.
            sb.AppendLine("        private void DispatchTrigger(ushort methodId)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (methodId)");
            sb.AppendLine("            {");

            foreach (var method in methods)
            {
                var methodName = method.Identifier.Text;
                var methodAlias = GetMethodAlias(method, methodName);
                var methodVersion = GetMethodVersion(method);
                var paramCount = method.ParameterList.Parameters.Count;
                var returnTypeStr = method.ReturnType.ToString();
                bool isAsyncMethod = returnTypeStr.StartsWith("Task") || returnTypeStr.StartsWith("System.Threading.Tasks.Task");

                // Triggers are always parameterless (void or Task)
                if (paramCount == 0)
                {
                    var idConst = "global::" + namespaceName + ".Generated.GameMethodIds." +
                        SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion);
                    sb.AppendLine($"                case {idConst}:");
                    if (isAsyncMethod)
                    {
                        sb.AppendLine($"                    BroadcastValidator.EnsureSyncCompletion(_service.{methodName}(), ServiceName, \"{methodAlias}\");");
                    }
                    else
                    {
                        sb.AppendLine($"                    _service.{methodName}();");
                    }
                    sb.AppendLine("                    break;");
                }
            }

            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateBroadcastArgumentDeserialization(StringBuilder sb, MethodDeclarationSyntax method,
            List<ParameterTransform> transforms, int paramCount, DetectedSerializer serializer)
        {
            // A broadcast carries the originating client's argument payload verbatim, so this reads
            // the boxed shape the caller wrote and unboxes it exactly as the server dispatcher did.
            string Target(ParameterTransform t) => t.Transformed ? t.WireLocal : t.Name;

            if (serializer == DetectedSerializer.MemoryPack)
            {
                if (paramCount == 1)
                {
                    var t = transforms[0];
                    sb.AppendLine($"                    var {Target(t)} = MemoryPackSerializer.Deserialize<{t.WireType}>(broadcast.ArgsBytes)!;");
                }
                else
                {
                    // Multiple parameters - use MemoryPackReader
                    sb.AppendLine("                    var mpState = MemoryPackReaderOptionalStatePool.Rent(null);");
                    sb.AppendLine("                    var mpReader = new MemoryPackReader(broadcast.ArgsBytes, mpState);");
                    foreach (var t in transforms)
                    {
                        sb.AppendLine($"                    var {Target(t)} = mpReader.ReadValue<{t.WireType}>()!;");
                    }
                    sb.AppendLine("                    mpReader.Dispose();");
                }
            }
            else
            {
                // Generic serializer — always use CreateReader for correct length-prefixed format
                {
                    sb.AppendLine("                    using var reader = _serializer.CreateReader(broadcast.ArgsBytes);");
                    foreach (var t in transforms)
                    {
                        sb.AppendLine($"                    var {Target(t)} = reader.Read<{t.WireType}>()!;");
                    }
                }
            }

            foreach (var t in transforms.Where(t => t.Transformed))
            {
                sb.AppendLine($"                    var {t.Name} = {TransformerAnalysis.UnboxExpr(t, t.WireLocal, "_state")};");
            }
        }

        private static string GetMethodAlias(MethodDeclarationSyntax method, string defaultName)
        {
            var attributes = method.AttributeLists.SelectMany(a => a.Attributes);
            var metaMethod = attributes.FirstOrDefault(a => a.Name.ToString().Contains("MetaMethod"));
            if (metaMethod != null)
            {
                var aliasArg = metaMethod.ArgumentList?.Arguments
                    .FirstOrDefault(arg => arg.NameEquals != null && arg.NameEquals.Name.Identifier.Text == "Alias");
                if (aliasArg != null && aliasArg.Expression is LiteralExpressionSyntax literal)
                {
                    return literal.Token.ValueText;
                }
            }
            return defaultName;
        }

        /// <summary>
        /// Internal helper for sibling generators (e.g. <c>ServiceRegistrationGenerator</c>) that
        /// need the same <c>[MetaMethod(Alias)]</c> resolution rule without re-implementing it.
        /// Defaults to the method's identifier text when no alias is declared.
        /// </summary>
        internal static string GetMethodAliasInternal(MethodDeclarationSyntax method)
            => GetMethodAlias(method, method.Identifier.Text);

        /// <summary>
        /// 0.22.0 helper: emit the per-version body of a service broadcast case. Identical
        /// shape to the pre-0.22 single-version case body (arg deserialization, state-data /
        /// patch / pure-replay branches, trigger replay, event fire), parametrized on the
        /// method so multi-version aliases can inline one body per declared version.
        /// <para>
        /// <paramref name="indent"/> threads the C# indentation into the right depth for
        /// either a top-level case (single-version) or a nested switch case (multi-version).
        /// </para>
        /// </summary>
        private static void EmitBroadcastReplayBody(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, DetectedSerializer serializer, Compilation? compilation, string indent)
        {
            var methodName = method.Identifier.Text;
            var eventName = GetEventName(methodName);
            var paramCount = method.ParameterList.Parameters.Count;
            var returnTypeStr = method.ReturnType.ToString();
            bool isAsyncMethod = returnTypeStr.StartsWith("Task") || returnTypeStr.StartsWith("System.Threading.Tasks.Task");

            if (paramCount > 0)
            {
                // Per-version arg deserialization: the underlying serializer reads from the
                // broadcast's ArgsBytes (which the server tailored for this version's parameter
                // shape). The output local variables are scoped to this body's case block.
                var transforms = TransformerAnalysis.Analyze(method.ParameterList.Parameters, compilation);
                GenerateBroadcastArgumentDeserialization(sb, method, transforms, paramCount, serializer);
            }

            var argNames = paramCount > 0
                ? method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToList()
                : new List<string>();
            var callArgsStr = string.Join(", ", argNames);

            sb.AppendLine($"{indent}if (broadcast.StateBytes is {{ Length: > 0 }})");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    _optimisticRandom?.Skip(broadcast.RandomScrollDelta);");
            sb.AppendLine($"{indent}    ApplyNamedScrollSkips(broadcast.NamedRandomScrollDeltas);");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}else if (broadcast.PatchBytes is {{ Length: > 0 }})");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    _optimisticRandom?.Skip(broadcast.RandomScrollDelta);");
            sb.AppendLine($"{indent}    ApplyNamedScrollSkips(broadcast.NamedRandomScrollDeltas);");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    SetContext(broadcast.ReplayContext, broadcast.CallerId, broadcast.ServerTimeTicks, broadcast.ExecutedConfigVersions is {{ Count: > 0 }} ? broadcast.ExecutedConfigVersions[0] : default);");
            if (paramCount == 0)
            {
                if (isAsyncMethod)
                    sb.AppendLine($"{indent}    BroadcastValidator.EnsureSyncCompletion(_service.{methodName}(), ServiceName, \"{methodAlias}\");");
                else
                    sb.AppendLine($"{indent}    _service.{methodName}();");
            }
            else
            {
                if (isAsyncMethod)
                    sb.AppendLine($"{indent}    BroadcastValidator.EnsureSyncCompletion(_service.{methodName}({callArgsStr}), ServiceName, \"{methodAlias}\");");
                else
                    sb.AppendLine($"{indent}    _service.{methodName}({callArgsStr});");
            }
            sb.AppendLine($"{indent}    ClearContext();");
            sb.AppendLine($"{indent}    _stateContainer.NotifyMutated();");
            sb.AppendLine($"{indent}}}");

            sb.AppendLine($"{indent}ReplayTriggerOperations(broadcast.TriggerOperations, broadcast.CallerId, broadcast.ServerTimeTicks);");

            if (paramCount == 0)
                sb.AppendLine($"{indent}{eventName}?.Invoke();");
            else if (paramCount == 1)
                sb.AppendLine($"{indent}{eventName}?.Invoke({argNames[0]});");
            else
                sb.AppendLine($"{indent}{eventName}?.Invoke(({callArgsStr}));");
        }

        /// <summary>
        /// 0.22.0 opt-out reader. Looks for an assembly-level
        /// <c>[SharedMetaCompatibilityOptions(Enabled = false)]</c> declaration and returns
        /// <c>false</c> when found. Default <c>true</c> — absence of the attribute means the
        /// negotiation generator features stay on.
        /// </summary>
        internal static bool IsCompatibilityNegotiationEnabled(Compilation compilation)
        {
            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() != "SharedMeta.Core.SharedMetaCompatibilityOptionsAttribute")
                    continue;
                var enabledArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "Enabled");
                if (!enabledArg.Value.IsNull && enabledArg.Value.Value is bool b)
                    return b;
            }
            return true;
        }

        /// <summary>
        /// Reads <c>[MetaMethod(Version = N)]</c> from a service-interface method declaration.
        /// Defaults to 0 (legacy / unversioned). Stamped onto the wire as <c>RpcCall.MethodVersion</c>
        /// so the server dispatcher routes <c>(Alias, Version)</c> to the correct impl.
        /// </summary>
        internal static int GetMethodVersion(MethodDeclarationSyntax method)
        {
            var attributes = method.AttributeLists.SelectMany(a => a.Attributes);
            var metaMethod = attributes.FirstOrDefault(a => a.Name.ToString().Contains("MetaMethod"));
            if (metaMethod == null) return 0;
            var versionArg = metaMethod.ArgumentList?.Arguments
                .FirstOrDefault(arg => arg.NameEquals != null && arg.NameEquals.Name.Identifier.Text == "Version");
            if (versionArg?.Expression is LiteralExpressionSyntax lit && int.TryParse(lit.Token.ValueText, out var v))
                return v;
            return 0;
        }

        /// <summary>
        /// Emits the client API for a <c>[MetaMethod(Mode = ExecutionMode.LocalQuery)]</c> method:
        /// a synchronous <c>{Method}Sync(...)</c> and/or an asynchronous <c>{Method}Async(...)</c>,
        /// chosen by <c>Sync</c> (default <see cref="SyncApi.OnlySync"/> for LocalQuery, so sync-only
        /// unless the author opts into async). Both run the impl over the local <c>State</c> snapshot
        /// with no RPC; the async form completes synchronously (<c>Task.FromResult</c>) and exists for
        /// forward-compat — a caller that already <c>await</c>s <c>{Method}Async</c> keeps compiling if
        /// the method later moves to a server-backed execution mode. Caller guarantees a non-Task,
        /// non-void return type.
        /// </summary>
        private static void GenerateLocalQueryApiMethods(StringBuilder sb, MethodDeclarationSyntax method,
            string methodName, string parameters, string callArgs, string innerReturnType,
            bool wantsSync, bool onlySync, string interfaceName, string namespaceName)
        {
            // Async wrapper — emitted unless Sync = OnlySync. Runs the impl over local State and returns
            // a completed Task; no server round-trip. !onlySync covers Sync = None (async only) and
            // Sync = Generate (both); when Sync = OnlySync this block is skipped.
            if (!onlySync)
            {
                sb.AppendLine($"        /// <summary>");
                sb.AppendLine($"        /// {methodName} (LocalQuery) — async wrapper over the local-State read. Completes synchronously, no server round-trip.");
                sb.AppendLine($"        /// </summary>");
                sb.AppendLine($"        [global::SharedMeta.Core.GeneratedFromMetaMethod(typeof(global::{namespaceName}.{interfaceName}), \"{methodName}\")]");
                sb.AppendLine($"        public Task<{innerReturnType}> {ContextInjectionGenerator.AsyncMethodName(methodName)}({parameters})");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_errorException != null) throw new ServiceErrorStateException(ServiceName, _errorException);");
                sb.AppendLine("            var __prev = SetupQueryContext();");
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                sb.AppendLine($"                return Task.FromResult(_service.{methodName}({callArgs}));");
                sb.AppendLine("            }");
                sb.AppendLine("            finally");
                sb.AppendLine("            {");
                sb.AppendLine("                RestoreQueryContext(__prev);");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Sync method — emitted when Sync is OnlySync (LocalQuery default) or Generate. Pure local
            // read, no RPC, no mode-override guard (there is no server counterpart to defer to).
            if (wantsSync)
            {
                sb.AppendLine($"        /// <summary>");
                sb.AppendLine($"        /// Synchronous LocalQuery read of {methodName} over locally replicated State. No server round-trip.");
                sb.AppendLine($"        /// </summary>");
                sb.AppendLine($"        [global::SharedMeta.Core.GeneratedFromMetaMethod(typeof(global::{namespaceName}.{interfaceName}), \"{methodName}\")]");
                sb.AppendLine($"        public {innerReturnType} {methodName}Sync({parameters})");
                sb.AppendLine("        {");
                sb.AppendLine("            if (_errorException != null) throw new ServiceErrorStateException(ServiceName, _errorException);");
                sb.AppendLine("            var __prev = SetupQueryContext();");
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                sb.AppendLine($"                return _service.{methodName}({callArgs});");
                sb.AppendLine("            }");
                sb.AppendLine("            finally");
                sb.AppendLine("            {");
                sb.AppendLine("                RestoreQueryContext(__prev);");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Generates a local-only query method that executes on the client's local state.
        /// No network call — just a direct call to the service instance.
        /// </summary>
        private static void GenerateLocalQueryMethod(StringBuilder sb, MethodDeclarationSyntax method)
        {
            var methodName = method.Identifier.Text;
            var returnType = method.ReturnType.ToString();
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            var argNames = method.ParameterList.Parameters.Select(p => p.Identifier.Text);
            var callArgs = string.Join(", ", argNames);

            // Unwrap Task<T> -> T if needed
            bool isAsync = returnType.StartsWith("Task");
            string syncReturnType;
            if (returnType == "void" || returnType == "Task")
                syncReturnType = "void";
            else if (returnType.StartsWith("Task<"))
                syncReturnType = returnType.Substring("Task<".Length, returnType.Length - "Task<".Length - 1);
            else
                syncReturnType = returnType;

            bool isVoid = syncReturnType == "void";

            sb.AppendLine();
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Query: executes locally on client state, no network call.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public {syncReturnType} {methodName}({parameters})");
            sb.AppendLine("        {");
            // 0.22.0: bracket the synchronous body with Setup/Restore so the impl can read
            // Context.State / Context.EntityId via MetaContextAccessor.Current. Without this,
            // the call only worked when an async method had left a context set in AsyncLocal.
            sb.AppendLine("            var __prev = SetupQueryContext();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            if (isVoid)
                sb.AppendLine($"                _service.{methodName}({callArgs});");
            else
                sb.AppendLine($"                return _service.{methodName}({callArgs});");
            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                RestoreQueryContext(__prev);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }

        /// <summary>
        /// Emit the client-side <c>{Method}Signal(params)</c> for a <c>[MetaMethod(Signal = true)]</c>.
        /// Fire-and-forget: public method returns <c>void</c>, delegates to
        /// <see cref="SharedMeta.Core.Network.INetwork.SendSignalAsync"/>. No RequestId, no
        /// response handling, no broadcast suppression. Invalid attribute combinations and
        /// non-void return types produce <c>#error</c> at this site.
        /// </summary>
        private static void GenerateSignalMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, bool isQueryCombo, bool modeExplicit, string syncApi, string interfaceName,
            string namespaceName, DetectedSerializer serializer, List<ParameterTransform> transforms)
        {
            var methodName = method.Identifier.Text;
            var returnType = method.ReturnType.ToString();
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            var paramCount = method.ParameterList.Parameters.Count;

            // ---- Compile-time validations ----
            if (returnType != "void")
            {
                sb.AppendLine($"#error SharedMeta: [MetaMethod(Signal = true)] on '{interfaceName}.{methodName}' must return void (got '{returnType}'). Signals are fire-and-forget — no value can flow back to the caller.");
            }
            if (isQueryCombo)
            {
                sb.AppendLine($"#error SharedMeta: [MetaMethod] on '{interfaceName}.{methodName}' sets both Query = true and Signal = true. These are mutually exclusive — Query returns a value (client awaits), Signal returns nothing (client does not await).");
            }
            if (modeExplicit)
            {
                sb.AppendLine($"#error SharedMeta: [MetaMethod(Signal = true)] on '{interfaceName}.{methodName}' also specifies Mode. Signal methods ignore execution mode (they always run server-side, read-only). Remove the Mode argument.");
            }
            if (syncApi != "None")
            {
                sb.AppendLine($"#error SharedMeta: [MetaMethod(Signal = true)] on '{interfaceName}.{methodName}' also specifies Sync = SyncApi.{syncApi}. Signal methods already return synchronously (void) and cannot also use the sync-overload mechanism.");
            }

            // Use the same serializer selection as the rest of the ApiClient — this is what the
            // server-side generated SignalDispatcher will mirror for argument deserialization.
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Signal: fire-and-forget server call. Returns immediately; server-side errors are logged only.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        [global::SharedMeta.Core.GeneratedFromMetaMethod(typeof(global::{namespaceName}.{interfaceName}), \"{methodName}\")]");
            sb.AppendLine($"        public void {methodName}Signal({parameters})");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_errorException != null) throw new ServiceErrorStateException(ServiceName, _errorException);");

            // Serialize arguments using the detected serializer (same pattern as Optimistic).
            GenerateArgumentSerialization(sb, method, transforms, paramCount, serializer);

            // Fire-and-forget: discard the ValueTask returned by SendSignalAsync.
            // Note: _ = (ValueTask) is allowed in C# 7.3+. GetAwaiter().GetResult() not used
            // because we never want to block or observe completion.
            var methodVersion = GetMethodVersion(method);
            sb.AppendLine($"            _ = _network.SendSignalAsync(global::{namespaceName}.Generated.GameMethodIds.{SignatureHashGenerator.MakeMethodIdConstName(interfaceName, methodAlias, methodVersion)}, argsBytes);");
            sb.AppendLine("        }");
        }

        private static bool IsQueryMethod(MethodDeclarationSyntax method)
        {
            var attributes = method.AttributeLists.SelectMany(a => a.Attributes);
            var metaMethod = attributes.FirstOrDefault(a => a.Name.ToString().Contains("MetaMethod"));
            if (metaMethod != null)
            {
                foreach (var arg in metaMethod.ArgumentList?.Arguments ?? Enumerable.Empty<AttributeArgumentSyntax>())
                {
                    if (arg.NameEquals == null) continue;
                    var name = arg.NameEquals.Name.Identifier.Text;
                    // Legacy bool form
                    if (name == "Query"
                        && arg.Expression is LiteralExpressionSyntax lit
                        && lit.Token.Text == "true")
                        return true;
                    // Canonical Mode = ExecutionMode.Query form
                    if (name == "Mode"
                        && arg.Expression is MemberAccessExpressionSyntax modeAccess
                        && modeAccess.Name.Identifier.Text == "Query")
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True for <c>[MetaMethod(Mode = ExecutionMode.LocalQuery)]</c> — a synchronous, no-RPC read
        /// over locally replicated State. Like Query methods, LocalQuery produces no broadcast, no
        /// replay, and no replay event; the broadcast/replay/event loops exclude it so the client
        /// doesn't reference per-mode private bodies that aren't generated for a sync-only method.
        /// </summary>
        private static bool IsLocalQueryMethod(MethodDeclarationSyntax method)
        {
            var metaMethod = method.AttributeLists.SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString().Contains("MetaMethod"));
            if (metaMethod == null) return false;
            foreach (var arg in metaMethod.ArgumentList?.Arguments ?? Enumerable.Empty<AttributeArgumentSyntax>())
            {
                if (arg.NameEquals?.Name.Identifier.Text == "Mode"
                    && arg.Expression is MemberAccessExpressionSyntax modeAccess
                    && modeAccess.Name.Identifier.Text == "LocalQuery")
                    return true;
            }
            return false;
        }

        private static bool IsSignalMethod(MethodDeclarationSyntax method)
        {
            var attributes = method.AttributeLists.SelectMany(a => a.Attributes);
            var metaMethod = attributes.FirstOrDefault(a => a.Name.ToString().Contains("MetaMethod"));
            if (metaMethod == null) return false;
            foreach (var arg in metaMethod.ArgumentList?.Arguments ?? Enumerable.Empty<AttributeArgumentSyntax>())
            {
                if (arg.NameEquals == null) continue;
                var name = arg.NameEquals.Name.Identifier.Text;
                if (name == "Signal"
                    && arg.Expression is LiteralExpressionSyntax lit
                    && lit.Token.Text == "true")
                    return true;
                if (name == "Mode"
                    && arg.Expression is MemberAccessExpressionSyntax modeAccess
                    && modeAccess.Name.Identifier.Text == "Signal")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True when the method carries <c>[MetaMethod(GenerateClientApi = false)]</c> —
        /// i.e. the method is reserved for sibling/cross-entity use and must not be exposed
        /// on the public client API. The corresponding events and broadcast/replay handlers
        /// are still emitted so subscribed clients can react to state changes when other
        /// entities invoke the method cross-entity.
        /// </summary>
        private static bool IsGenerateClientApiFalse(MethodDeclarationSyntax method)
            => MetaMethodFacts.IsClientApiSuppressed(method);

        /// <summary>
        /// Generates deep desync CRC comparison using PatchNode from the service's PatchWrapper.
        /// The PatchWrapper (via Context.PatchWrapper) records all state mutations.
        /// When DeepDesyncCrc is present in server response, client serializes its local PatchNode
        /// and compares CRC to detect state-level divergence.
        /// </summary>
        /// <summary>
        /// Generate deep desync CRC check (only when service has DeepDesync = true).
        /// Uses precomputed _ddLocalCrc and _ddLocalPatchBytes from the calling context.
        /// On mismatch: fires OnPatchDesync diagnostic + sends DesyncReport to server.
        /// </summary>
        private static void GenerateDeepDesyncCheck(StringBuilder sb, string methodAlias, string responseVar, string indent, bool hasDeepDesync)
        {
            if (!hasDeepDesync) return; // No-op for services without DeepDesync = true

            sb.AppendLine($"{indent}if ({responseVar}.DeepDesyncCrc.HasValue && {responseVar}.DeepDesyncCrc.Value != _ddLocalCrc)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    _diagnostics?.OnPatchDesync(ServiceName, \"{methodAlias}\", {responseVar}.DeepDesyncCrc.Value, _ddLocalCrc);");
            sb.AppendLine($"{indent}    // Fire-and-forget desync follow-up report to server");
            sb.AppendLine($"{indent}    _ = _network.SendDesyncReportAsync(new SharedMeta.Core.Transport.DesyncReportRequest");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        EntityId = _network.EntityId ?? string.Empty,");
            sb.AppendLine($"{indent}        ServiceName = ServiceName,");
            sb.AppendLine($"{indent}        MethodName = \"{methodAlias}\",");
            sb.AppendLine($"{indent}        ArgsBytes = argsBytes,");
            sb.AppendLine($"{indent}        ClientPatchBytes = _ddLocalPatchBytes,");
            sb.AppendLine($"{indent}        MismatchKind = (int)SharedMeta.Core.Transport.DesyncMismatchKind.Patch,");
            sb.AppendLine($"{indent}    }});");
            sb.AppendLine($"{indent}}}");
        }

        /// <summary>
        /// Emit a fire-and-forget desync follow-up report for a Result mismatch.
        /// Server gates by DesyncReportingEnabled and returns "disabled" cheaply when off.
        /// Both server and local result bytes are included so no server cache is needed.
        /// </summary>
        private static void GenerateResultMismatchReport(StringBuilder sb, string methodAlias, string serverBytesExpr, string localBytesExpr, string indent)
        {
            sb.AppendLine($"{indent}_ = _network.SendDesyncReportAsync(new SharedMeta.Core.Transport.DesyncReportRequest");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    EntityId = _network.EntityId ?? string.Empty,");
            sb.AppendLine($"{indent}    ServiceName = ServiceName,");
            sb.AppendLine($"{indent}    MethodName = \"{methodAlias}\",");
            sb.AppendLine($"{indent}    ArgsBytes = argsBytes,");
            sb.AppendLine($"{indent}    ServerResultBytes = {serverBytesExpr} ?? System.Array.Empty<byte>(),");
            sb.AppendLine($"{indent}    LocalResultBytes = {localBytesExpr} ?? System.Array.Empty<byte>(),");
            sb.AppendLine($"{indent}    MismatchKind = (int)SharedMeta.Core.Transport.DesyncMismatchKind.Result,");
            sb.AppendLine($"{indent}}});");
        }

        /// <summary>
        /// Emit a fire-and-forget desync follow-up report for a Random mismatch.
        /// </summary>
        private static void GenerateRandomMismatchReport(StringBuilder sb, string methodAlias, string serverDeltaExpr, string localDeltaExpr, string indent)
        {
            sb.AppendLine($"{indent}_ = _network.SendDesyncReportAsync(new SharedMeta.Core.Transport.DesyncReportRequest");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    EntityId = _network.EntityId ?? string.Empty,");
            sb.AppendLine($"{indent}    ServiceName = ServiceName,");
            sb.AppendLine($"{indent}    MethodName = \"{methodAlias}\",");
            sb.AppendLine($"{indent}    ArgsBytes = argsBytes,");
            sb.AppendLine($"{indent}    ServerRandomDelta = {serverDeltaExpr},");
            sb.AppendLine($"{indent}    LocalRandomDelta = {localDeltaExpr},");
            sb.AppendLine($"{indent}    MismatchKind = (int)SharedMeta.Core.Transport.DesyncMismatchKind.Random,");
            sb.AppendLine($"{indent}}});");
        }

        // Mirrors ContextInjectionGenerator's helper of the same name — [MetaConfig(Default=true)]
        // resolution for [MetaService(DefaultConfig=true)] services with no explicit ConfigType.
        private static string? FindDefaultConfigType(Compilation compilation)
        {
            var result = FindDefaultConfigTypeInNamespace(compilation.Assembly.GlobalNamespace);
            if (result != null) return result;

            foreach (var reference in compilation.References)
            {
                var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                if (assemblySymbol == null) continue;
                var name = assemblySymbol.Name;
                if (name.StartsWith("System") || name.StartsWith("Microsoft") ||
                    name.StartsWith("netstandard") || name.StartsWith("SharedMeta"))
                    continue;

                result = FindDefaultConfigTypeInNamespace(assemblySymbol.GlobalNamespace);
                if (result != null) return result;
            }
            return null;
        }

        private static string? FindDefaultConfigTypeInNamespace(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                var attr = type.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaConfigAttribute");
                if (attr != null)
                {
                    var defaultArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "Default");
                    if (!defaultArg.Value.IsNull && defaultArg.Value.Value is true)
                        return type.ToDisplayString();
                }
            }
            foreach (var childNs in ns.GetNamespaceMembers())
            {
                var result = FindDefaultConfigTypeInNamespace(childNs);
                if (result != null) return result;
            }
            return null;
        }

        private static string GetEventName(string methodName)
        {
            if (methodName.StartsWith("On") && methodName.Length > 2 && char.IsUpper(methodName[2]))
            {
                return $"{methodName}_Replayed";
            }
            return $"On{methodName}_Replayed";
        }

    }
}
