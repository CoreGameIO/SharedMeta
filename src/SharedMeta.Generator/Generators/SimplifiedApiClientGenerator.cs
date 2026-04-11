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

            // Get subscriber interfaces from attribute
            var subscriberInterfacesArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "SubscriberInterfaces");
            var subscriberInterfaces = new List<SubscriberInterfaceInfo>();
            if (!subscriberInterfacesArg.Value.IsNull && subscriberInterfacesArg.Value.Values.Length > 0)
            {
                foreach (var val in subscriberInterfacesArg.Value.Values)
                {
                    if (val.Value is INamedTypeSymbol subscriberType)
                    {
                        var info = new SubscriberInterfaceInfo
                        {
                            Name = subscriberType.Name,
                            FullName = subscriberType.ToDisplayString()
                        };
                        foreach (var member in subscriberType.GetMembers().OfType<IMethodSymbol>())
                        {
                            if (member.Parameters.Length == 1)
                            {
                                info.Methods.Add(new SubscriberMethodInfo
                                {
                                    MethodName = member.Name,
                                    EventTypeName = member.Parameters[0].Type.ToDisplayString(),
                                    IsAsync = !member.ReturnsVoid && member.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks.Task")
                                });
                            }
                        }
                        if (info.Methods.Count > 0)
                        {
                            subscriberInterfaces.Add(info);
                        }
                    }
                }
            }

            // Detect serializer type
            var serializer = compilation != null ? SerializerDetector.Detect(compilation) : DetectedSerializer.Generic;

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#pragma warning disable CS1998, CS1522");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Packets;");
            sb.AppendLine("using SharedMeta.Core.Network;");
            sb.AppendLine("using SharedMeta.Core.Diagnostics;");
            sb.AppendLine("using SharedMeta.Core.Random;");
            sb.AppendLine("using SharedMeta.Core.Patch;");
            sb.AppendLine("using SharedMeta.Client;");
            sb.AppendLine("using SharedMeta.Core.Logging;");
            sb.AppendLine("using ExecutionMode = SharedMeta.Core.ExecutionMode;");
            if (serializer == DetectedSerializer.MemoryPack)
            {
                sb.AppendLine("using MemoryPack;");
            }
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
            sb.AppendLine($"        private {stateTypeName} _state;");
            sb.AppendLine($"        private readonly {namespaceName}.{implClassName} _service;");
            if (hasDeepDesync)
                sb.AppendLine($"        private readonly {namespaceName}.{patchTrackedClassName} _patchTrackedService;");
            sb.AppendLine("        private readonly IExecutionModeProvider _modeProvider;");
            sb.AppendLine("        private readonly IDesyncDiagnostics? _diagnostics;");
            sb.AppendLine("        private readonly ICrossEntityResolver? _crossEntityResolver;");
            sb.AppendLine("        private MetaRandom? _optimisticRandom;");
            sb.AppendLine("        private readonly object? _config;");
            sb.AppendLine($"        private const string ServiceName = \"{interfaceName}\";");
            sb.AppendLine($"        private Exception? _errorException;");
            sb.AppendLine();

            // Events
            sb.AppendLine("        // Events fired when methods are replayed from broadcasts");
            foreach (var method in methods)
            {
                // Skip query methods — they don't produce broadcasts or replays
                if (IsQueryMethod(method)) continue;

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

            // Subscriber events
            if (subscriberInterfaces.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        // Subscriber interface events");
                foreach (var subscriber in subscriberInterfaces)
                {
                    foreach (var method in subscriber.Methods)
                    {
                        var eventName = GetEventName(method.MethodName);
                        sb.AppendLine($"        public event Action<{method.EventTypeName}>? {eventName};");
                    }
                }
            }
            sb.AppendLine();

            // State refresh event
            sb.AppendLine($"        /// <summary>Fired after state is replaced on reconnect. Use to update Views.</summary>");
            sb.AppendLine($"        public event Action<{stateTypeName}>? OnStateRefreshed;");
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>Fired after any state mutation (broadcast replay, subscriber event, reconnect).</summary>");
            sb.AppendLine($"        public event Action? OnStateMutated;");
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
            sb.AppendLine($"            {stateTypeName} state,");
            sb.AppendLine($"            IExecutionModeProvider modeProvider,");
            sb.AppendLine($"            IDesyncDiagnostics? diagnostics = null,");
            sb.AppendLine($"            ICrossEntityResolver? crossEntityResolver = null,");
            sb.AppendLine($"            MetaRandom? optimisticRandom = null,");
            sb.AppendLine($"            object? config = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            _network = network;");
            sb.AppendLine("            _serializer = serializer;");
            sb.AppendLine("            _state = state;");
            sb.AppendLine("            _modeProvider = modeProvider;");
            sb.AppendLine("            _diagnostics = diagnostics;");
            sb.AppendLine("            _crossEntityResolver = crossEntityResolver;");
            sb.AppendLine("            _optimisticRandom = optimisticRandom;");
            sb.AppendLine("            _config = config;");
            sb.AppendLine($"            _service = new {namespaceName}.{implClassName}();");
            if (hasDeepDesync)
                sb.AppendLine($"            _patchTrackedService = new {namespaceName}.{patchTrackedClassName}();");
            sb.AppendLine();
            sb.AppendLine("            _network.OnBroadcast += HandleBroadcast;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void SetError(Exception ex, string methodName)");
            sb.AppendLine("        {");
            sb.AppendLine("            MetaLog.Error($\"[{ServiceName}.{methodName}] Service error\", ex);");
            sb.AppendLine("            _errorException = ex;");
            sb.AppendLine("            OnServiceError?.Invoke(ServiceName, ex);");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Generate methods
            foreach (var method in methods)
            {
                GenerateMethod(sb, method, interfaceName, namespaceName, implClassName, stateTypeName, serializer, hasDeepDesync);
            }

            // Context management
            GenerateContextMethods(sb, stateTypeName);

            // Broadcast handling
            GenerateHandleBroadcast(sb, methods, interfaceName, namespaceName, implClassName, stateTypeName, subscriberInterfaces, serializer);

            // Trigger replay
            GenerateTriggerReplayMethods(sb, methods, stateTypeName);

            // RefreshState — called by MetaServiceResolver on reconnect
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// Replace internal state and random after reconnect.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public void RefreshState({stateTypeName} newState, MetaRandom? newRandom)");
            sb.AppendLine("        {");
            sb.AppendLine("            _state = newState;");
            sb.AppendLine("            _optimisticRandom = newRandom;");
            sb.AppendLine("            _errorException = null;");
            sb.AppendLine("            OnStateRefreshed?.Invoke(newState);");
            sb.AppendLine("            OnStateMutated?.Invoke();");
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
            DetectedSerializer serializer, bool hasDeepDesync = false)
        {
            var methodName = method.Identifier.Text;
            var returnType = method.ReturnType.ToString();
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            var paramCount = method.ParameterList.Parameters.Count;

            // Parse MetaMethod attribute for default mode
            var methodAlias = GetMethodAlias(method, methodName);
            var defaultMode = "Server";
            bool isQueryMethod = false;

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
                            defaultMode = modeAccess.Name.Identifier.Text;
                        if (name == "Query" && arg.Expression is LiteralExpressionSyntax queryLit
                            && queryLit.Token.Text == "true")
                            isQueryMethod = true;
                    }
                }
            }

            // Query methods — execute locally on client state, no network call
            if (isQueryMethod)
            {
                GenerateLocalQueryMethod(sb, method);
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

            // Public method with mode switch
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// {methodName} - Default mode: {defaultMode}");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        public {asyncReturnType} {methodName}Async({parameters})");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_errorException != null) throw new ServiceErrorStateException(ServiceName, _errorException);");
            sb.AppendLine($"            var mode = _modeProvider.GetMode(ServiceName, \"{methodAlias}\", ExecutionMode.{defaultMode});");
            sb.AppendLine($"            if (mode == ExecutionMode.ServerPatch)");
            sb.AppendLine($"                return {methodName}Async_ServerPatch({callArgs});");
            sb.AppendLine($"            if (mode == ExecutionMode.ServerReplace)");
            sb.AppendLine($"                return {methodName}Async_ServerReplace({callArgs});");
            sb.AppendLine($"            if (mode == ExecutionMode.Server)");
            sb.AppendLine($"                return {methodName}Async_Server({callArgs});");
            sb.AppendLine($"            if (mode == ExecutionMode.CrossOptimistic)");
            sb.AppendLine($"                return {methodName}Async_CrossOptimistic({callArgs});");
            sb.AppendLine($"            return {methodName}Async_Optimistic({callArgs});");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Server mode implementation
            GenerateServerMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, serializer, stateTypeName, hasDeepDesync);

            // Optimistic mode implementation
            GenerateOptimisticMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, serializer, stateTypeName, hasDeepDesync);

            // CrossOptimistic mode implementation
            GenerateCrossOptimisticMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, serializer, stateTypeName, hasDeepDesync);

            // ServerPatch mode implementation
            GenerateServerPatchMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, serializer, stateTypeName);

            // ServerReplace mode implementation
            GenerateServerReplaceMethod(sb, method, methodAlias, innerReturnType, isVoidReturn, isAsync, paramCount, callArgs, serializer, stateTypeName);
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

        private static void GenerateServerMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs,
            DetectedSerializer serializer, string? stateTypeName = null, bool hasDeepDesync = false)
        {
            var methodName = method.Identifier.Text;
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";

            // Use await for service call if the service method is async
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {methodName}Async_Server({parameters})");
            sb.AppendLine("        {");

            // Capture server time before the call
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Serialize arguments before suppressing broadcasts (no network involved)
            GenerateArgumentSerialization(sb, method, paramCount, serializer);
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
                sb.AppendLine($"                var response = await _network.CallVoidAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            else
            {
                sb.AppendLine($"                var response = await _network.CallBytesAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks);");
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
                sb.AppendLine("                    _ddLocalPatchBytes = _serializer.Pack(_ddRoot);");
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
                // Use byte-level comparison to avoid reference equality issues with class types
                GenerateResultByteComparison(sb, returnType, serializer, "response.ResultBytes", "                ");
                sb.AppendLine("                {");
                sb.AppendLine($"                    _diagnostics?.OnResultMismatch(ServiceName, \"{methodAlias}\", serverResult, localResult);");
                GenerateResultMismatchReport(sb, methodAlias, "response.ResultBytes", "localResultBytes", "                    ");
                sb.AppendLine($"                    throw new DesyncException(ServiceName, \"{methodAlias}\", serverResult, localResult);");
                sb.AppendLine("                }");
            }

            // Deep desync: compare patch CRC after execution (uses precomputed _ddLocalCrc/_ddLocalPatchBytes)
            GenerateDeepDesyncCheck(sb, methodAlias, "response", "                ", hasDeepDesync);

            if (!isVoidReturn)
            {
                sb.AppendLine();
                sb.AppendLine("                _tracker.FlushAndNotify();");
                sb.AppendLine("                return localResult;");
            }
            else
            {
                sb.AppendLine("                _tracker.FlushAndNotify();");
            }
            sb.AppendLine("                }");
            sb.AppendLine($"                catch (Exception ex) {{ _tracker.Discard(); SetError(ex, \"{methodAlias}\"); throw; }}");

            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                _network.ResumeBroadcasts();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateArgumentSerialization(StringBuilder sb, MethodDeclarationSyntax method,
            int paramCount, DetectedSerializer serializer)
        {
            if (paramCount == 0)
            {
                sb.AppendLine("            var argsBytes = Array.Empty<byte>();");
                return;
            }

            if (serializer == DetectedSerializer.MemoryPack)
            {
                if (paramCount == 1)
                {
                    var paramName = method.ParameterList.Parameters[0].Identifier.Text;
                    sb.AppendLine($"            var argsBytes = MemoryPackSerializer.Serialize({paramName});");
                }
                else
                {
                    sb.AppendLine("            var buffer = new System.Buffers.ArrayBufferWriter<byte>();");
                    foreach (var param in method.ParameterList.Parameters)
                    {
                        sb.AppendLine($"            MemoryPackSerializer.Serialize(buffer, {param.Identifier.Text});");
                    }
                    sb.AppendLine("            var argsBytes = buffer.WrittenSpan.ToArray();");
                }
            }
            else
            {
                // Generic serializer — always use writer for consistent length-prefixed format.
                // Server dispatcher reads with CreateReader() which expects length-prefixed data.
                sb.AppendLine("            using var writer = _serializer.CreateWriter();");
                foreach (var param in method.ParameterList.Parameters)
                {
                    sb.AppendLine($"            writer.Write({param.Identifier.Text});");
                }
                sb.AppendLine("            var argsBytes = writer.Complete();");
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
                sb.AppendLine($"{indent}var localResultBytes = _serializer.Pack(localResult);");
            }
            sb.AppendLine($"{indent}if (!{serverBytesExpr}.AsSpan().SequenceEqual(localResultBytes))");
        }

        private static void GenerateOptimisticMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs,
            DetectedSerializer serializer, string? stateTypeName, bool hasDeepDesync = false)
        {
            var methodName = method.Identifier.Text;
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";

            // Optimistic always needs async for context cleanup
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {methodName}Async_Optimistic({parameters})");
            sb.AppendLine("        {");

            // Capture server time before local execution
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Set context before local execution (MetaContextAccessor must be set for Context.State access)
            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine("            ctx.CallerId = _network.PlayerId;");
            sb.AppendLine("            ctx.ServerTimeTicks = serverTimeTicks;");
            sb.AppendLine("            ctx.Random = _optimisticRandom;");
            sb.AppendLine("            ctx.Config = _config;");
            sb.AppendLine("            MetaContextAccessor.Current = ctx;");

            if (!isVoidReturn)
            {
                sb.AppendLine($"            {returnType} localResult;");
            }

            // Capture scrollId before local execution for desync detection
            sb.AppendLine("            var scrollIdBefore = _optimisticRandom?.ScrollId ?? 0;");
            sb.AppendLine("            var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");

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
            sb.AppendLine($"            catch (Exception ex) {{ MetaContextAccessor.Current = null; _tracker.Discard(); SetError(ex, \"{methodAlias}\"); throw; }}");
            sb.AppendLine("            MetaContextAccessor.Current = null;");
            sb.AppendLine("            _tracker.FlushAndNotify();");
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
                sb.AppendLine("                    _ddLocalPatchBytes = _serializer.Pack(_ddPw.Node);");
                sb.AppendLine("                    _ddLocalCrc = SharedMeta.Core.Patch.PatchCrc.Compute(_ddLocalPatchBytes);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
            sb.AppendLine();

            // Serialize arguments based on serializer
            GenerateArgumentSerialization(sb, method, paramCount, serializer);
            sb.AppendLine();

            // Fire-and-forget to server with background validation
            if (isVoidReturn)
            {
                sb.AppendLine($"            _ = _network.CallVoidAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks)");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        var localScrollDelta = (_optimisticRandom?.ScrollId ?? 0) - scrollIdBefore;");
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[Optimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
            }
            else
            {
                sb.AppendLine($"            _ = _network.CallBytesAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks)");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                GenerateOptimisticResultDeserialization(sb, returnType, methodAlias, serializer);
                sb.AppendLine();
                sb.AppendLine("                        var localScrollDelta = (_optimisticRandom?.ScrollId ?? 0) - scrollIdBefore;");
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
                GenerateDeepDesyncCheck(sb, methodAlias, "t.Result", "                        ", hasDeepDesync);
                sb.AppendLine("                    }");
                sb.AppendLine("                    }");
                sb.AppendLine($"                    catch (Exception _ddEx) {{ SharedMeta.Core.Logging.MetaLog.Error(\"[Optimistic-Continuation] {methodAlias}: \" + _ddEx, _ddEx); }}");
                sb.AppendLine("                });");
                sb.AppendLine();
                sb.AppendLine("            return localResult;");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void GenerateCrossOptimisticMethod(StringBuilder sb, MethodDeclarationSyntax method,
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs,
            DetectedSerializer serializer, string? stateTypeName, bool hasDeepDesync = false)
        {
            var methodName = method.Identifier.Text;
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {methodName}Async_CrossOptimistic({parameters})");
            sb.AppendLine("        {");

            // Capture server time before local execution
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Set context with CrossEntityResolver
            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine("            ctx.CallerId = _network.PlayerId;");
            sb.AppendLine("            ctx.ServerTimeTicks = serverTimeTicks;");
            sb.AppendLine("            ctx.CrossEntityResolver = _crossEntityResolver;");
            sb.AppendLine("            ctx.Random = _optimisticRandom;");
            sb.AppendLine("            ctx.Config = _config;");
            sb.AppendLine("            MetaContextAccessor.Current = ctx;");

            if (!isVoidReturn)
            {
                sb.AppendLine($"            {returnType} localResult;");
            }
            sb.AppendLine("            List<CrossEntityLocalResult> localCrossResults;");

            // Capture scrollId before local execution for desync detection
            sb.AppendLine("            var scrollIdBefore = _optimisticRandom?.ScrollId ?? 0;");
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
            sb.AppendLine($"            catch (Exception ex) {{ MetaContextAccessor.Current = null; _tracker.Discard(); SetError(ex, \"{methodAlias}\"); throw; }}");
            sb.AppendLine("            MetaContextAccessor.Current = null;");
            sb.AppendLine("            _tracker.FlushAndNotify();");
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
                sb.AppendLine("                    _ddLocalPatchBytes = _serializer.Pack(_ddPw.Node);");
                sb.AppendLine("                    _ddLocalCrc = SharedMeta.Core.Patch.PatchCrc.Compute(_ddLocalPatchBytes);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
            sb.AppendLine();

            // Serialize arguments
            GenerateArgumentSerialization(sb, method, paramCount, serializer);
            sb.AppendLine();

            // Fire-and-forget to server with IsCrossOptimistic flag + validation
            if (isVoidReturn)
            {
                sb.AppendLine($"            _ = _network.CallVoidAsync(ServiceName, \"{methodAlias}\", argsBytes, isCrossOptimistic: true, serverTimeTicks: serverTimeTicks)");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                sb.AppendLine("                        var localScrollDelta = (_optimisticRandom?.ScrollId ?? 0) - scrollIdBefore;");
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
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
                sb.AppendLine($"            _ = _network.CallBytesAsync(ServiceName, \"{methodAlias}\", argsBytes, isCrossOptimistic: true, serverTimeTicks: serverTimeTicks)");
                sb.AppendLine("                .ContinueWith(t =>");
                sb.AppendLine("                {");
                sb.AppendLine("                    try");
                sb.AppendLine("                    {");
                sb.AppendLine("                    if (t.IsCompletedSuccessfully)");
                sb.AppendLine("                    {");
                // Compare main result
                GenerateOptimisticResultDeserialization(sb, returnType, methodAlias, serializer);
                sb.AppendLine();
                sb.AppendLine("                        // Compare cross-entity results");
                sb.AppendLine("                        if (t.Result.CrossEntityOperations is { Count: > 0 } serverCrossOps)");
                sb.AppendLine("                        {");
                sb.AppendLine("                            for (int i = 0; i < serverCrossOps.Count && i < localCrossResults.Count; i++)");
                sb.AppendLine("                            {");
                sb.AppendLine("                                // Object-level comparison; typed comparison requires generated per-method code");
                sb.AppendLine("                                _diagnostics?.OnCrossEntityResult(serverCrossOps[i].EntityId, serverCrossOps[i].ServiceName, serverCrossOps[i].MethodName, serverCrossOps[i].ResultBytes);");
                sb.AppendLine("                            }");
                sb.AppendLine("                        }");
                sb.AppendLine();
                sb.AppendLine("                        var localScrollDelta = (_optimisticRandom?.ScrollId ?? 0) - scrollIdBefore;");
                sb.AppendLine("                        if (t.Result.RandomScrollDelta != localScrollDelta)");
                sb.AppendLine("                        {");
                sb.AppendLine($"                            _diagnostics?.OnRandomDesync(ServiceName, \"{methodAlias}\", t.Result.RandomScrollDelta, localScrollDelta);");
                GenerateRandomMismatchReport(sb, methodAlias, "t.Result.RandomScrollDelta", "localScrollDelta", "                            ");
                sb.AppendLine("                        }");
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
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs,
            DetectedSerializer serializer, string? stateTypeName)
        {
            var methodName = method.Identifier.Text;
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            // Compute PatchApplier full name: stateTypeName + "PatchApplier"
            var applierName = stateTypeName + "PatchApplier";

            sb.AppendLine($"        private async {asyncReturnType} {methodName}Async_ServerPatch({parameters})");
            sb.AppendLine("        {");

            // Capture server time before the call
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Serialize arguments
            GenerateArgumentSerialization(sb, method, paramCount, serializer);
            sb.AppendLine();

            // Suppress broadcasts during RPC + patch application
            sb.AppendLine("            _network.SuppressBroadcasts();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");

            // Call server — always use CallBytesAsync (we need ResultBytes + PatchBytes)
            if (isVoidReturn)
            {
                sb.AppendLine($"                var response = await _network.CallVoidAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            else
            {
                sb.AppendLine($"                var response = await _network.CallBytesAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks);");
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
            sb.AppendLine("                    _optimisticRandom?.Skip(response.RandomScrollDelta);");
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
            sb.AppendLine("                }");
            sb.AppendLine($"                catch (Exception ex) {{ _tracker.Discard(); SetError(ex, \"{methodAlias}\"); throw; }}");

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
            string methodAlias, string returnType, bool isVoidReturn, bool isAsyncServiceMethod, int paramCount, string callArgs,
            DetectedSerializer serializer, string? stateTypeName)
        {
            var methodName = method.Identifier.Text;
            var parameters = string.Join(", ", method.ParameterList.Parameters);
            string asyncReturnType = isVoidReturn ? "Task" : $"Task<{returnType}>";
            string awaitPrefix = isAsyncServiceMethod ? "await " : "";

            sb.AppendLine($"        private async {asyncReturnType} {methodName}Async_ServerReplace({parameters})");
            sb.AppendLine("        {");

            // Capture server time before the call
            sb.AppendLine("            var serverTimeTicks = _network.ServerTimeTicks;");

            // Serialize arguments
            GenerateArgumentSerialization(sb, method, paramCount, serializer);
            sb.AppendLine();

            // Suppress broadcasts during RPC + state replacement
            sb.AppendLine("            _network.SuppressBroadcasts();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");

            // Call server
            if (isVoidReturn)
            {
                sb.AppendLine($"                var response = await _network.CallVoidAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            else
            {
                sb.AppendLine($"                var response = await _network.CallBytesAsync(ServiceName, \"{methodAlias}\", argsBytes, serverTimeTicks: serverTimeTicks);");
            }
            sb.AppendLine();

            // Replace state or fallback to replay
            sb.AppendLine("                var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine("                if (response.StateBytes is { Length: > 0 } stateData)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    _state = _serializer.Unpack<{stateTypeName}>(stateData)!;");
            sb.AppendLine("                    _crossEntityResolver?.UpdateCachedState(_network.EntityId ?? string.Empty, _state);");
            sb.AppendLine("                    _optimisticRandom?.Skip(response.RandomScrollDelta);");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            sb.AppendLine("                    // Fallback: server didn't send StateBytes, replay normally");
            sb.AppendLine("                    SetContext(response.ReplayContext, _network.PlayerId, response.ServerTimeTicks);");
            sb.AppendLine($"                    {awaitPrefix}_service.{methodName}({callArgs});");
            sb.AppendLine("                    ClearContext();");
            sb.AppendLine($"                    ReplayTriggerOperations(response.TriggerOperations, _network.PlayerId, response.ServerTimeTicks);");
            sb.AppendLine("                }");
            sb.AppendLine();

            sb.AppendLine("                _tracker.FlushAndNotify();");
            sb.AppendLine($"                OnStateRefreshed?.Invoke(_state);");
            sb.AppendLine($"                OnStateMutated?.Invoke();");
            sb.AppendLine("                }");
            sb.AppendLine($"                catch (Exception ex) {{ _tracker.Discard(); SetError(ex, \"{methodAlias}\"); throw; }}");

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
            string methodAlias, DetectedSerializer serializer)
        {
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

        private static void GenerateContextMethods(StringBuilder sb, string? stateTypeName)
        {
            sb.AppendLine("        private void SetContext(byte[] replayContext)");
            sb.AppendLine("        {");
            sb.AppendLine("            SetContext(replayContext, null, 0);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void SetContext(byte[] replayContext, string? callerId, long serverTimeTicks = 0)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var ctx = new ClientMetaContext<{stateTypeName}>(_state, _serializer);");
            sb.AppendLine("            ctx.CallerId = callerId;");
            sb.AppendLine("            ctx.ServerTimeTicks = serverTimeTicks;");
            sb.AppendLine("            ctx.BeginReplay(replayContext);");
            sb.AppendLine("            ctx.Random = _optimisticRandom;");
            sb.AppendLine("            ctx.Config = _config;");
            sb.AppendLine("            ctx.ServerRandom = new MetaRandomReplayer(ctx);");
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
        }

        private static void GenerateHandleBroadcast(StringBuilder sb,
            List<MethodDeclarationSyntax> methods,
            string interfaceName, string namespaceName, string implClassName, string? stateTypeName,
            List<SubscriberInterfaceInfo> subscriberInterfaces, DetectedSerializer serializer)
        {
            sb.AppendLine("        private void HandleBroadcast(NetworkBroadcast broadcast)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (broadcast.CallerId == _network.PlayerId)");
            sb.AppendLine("                return;");
            sb.AppendLine();
            sb.AppendLine($"            if (broadcast.ServiceName == ServiceName)");
            sb.AppendLine("            {");
            sb.AppendLine("                DispatchServiceBroadcast(broadcast);");
            sb.AppendLine("            }");

            foreach (var subscriber in subscriberInterfaces)
            {
                sb.AppendLine($"            else if (broadcast.ServiceName == \"{subscriber.Name}\")");
                sb.AppendLine("            {");
                sb.AppendLine($"                Dispatch{subscriber.Name}Broadcast(broadcast);");
                sb.AppendLine("            }");
            }

            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        private void DispatchServiceBroadcast(NetworkBroadcast broadcast)");
            sb.AppendLine("        {");
            sb.AppendLine("            var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("            switch (broadcast.MethodName)");
            sb.AppendLine("            {");

            // Compute PatchApplier full name
            var applierName = stateTypeName + "PatchApplier";

            foreach (var method in methods)
            {
                // Skip query methods — they don't produce broadcasts
                if (IsQueryMethod(method)) continue;

                var methodName = method.Identifier.Text;
                var methodAlias = GetMethodAlias(method, methodName);
                var eventName = GetEventName(methodName);
                var paramCount = method.ParameterList.Parameters.Count;
                var returnTypeStr = method.ReturnType.ToString();
                bool isAsyncMethod = returnTypeStr.StartsWith("Task") || returnTypeStr.StartsWith("System.Threading.Tasks.Task");

                sb.AppendLine($"                case \"{methodAlias}\":");
                sb.AppendLine("                {");

                if (paramCount > 0)
                {
                    // Deserialize arguments (needed for event even when using patch)
                    GenerateBroadcastArgumentDeserialization(sb, method, paramCount, serializer);
                }

                var argNames = paramCount > 0
                    ? method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToList()
                    : new List<string>();
                var callArgsStr = string.Join(", ", argNames);

                // StateBytes check (ServerReplace): replace state wholesale
                sb.AppendLine("                    if (broadcast.StateBytes is { Length: > 0 } stateData)");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        _state = _serializer.Unpack<{stateTypeName}>(stateData)!;");
                sb.AppendLine("                        _crossEntityResolver?.UpdateCachedState(_network.EntityId ?? string.Empty, _state);");
                sb.AppendLine("                        _optimisticRandom?.Skip(broadcast.RandomScrollDelta);");
                sb.AppendLine("                    }");
                // PatchBytes check: apply patch or replay normally
                sb.AppendLine("                    else if (broadcast.PatchBytes is { Length: > 0 } patchData)");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        var patch = _serializer.Unpack<PatchNode>(patchData);");
                sb.AppendLine($"                        {applierName}.Apply(_state, patch, _serializer);");
                sb.AppendLine("                        _optimisticRandom?.Skip(broadcast.RandomScrollDelta);");
                sb.AppendLine("                    }");
                sb.AppendLine("                    else");
                sb.AppendLine("                    {");

                // Normal replay
                sb.AppendLine("                        SetContext(broadcast.ReplayContext, broadcast.CallerId, broadcast.ServerTimeTicks);");
                if (paramCount == 0)
                {
                    if (isAsyncMethod)
                    {
                        sb.AppendLine($"                        BroadcastValidator.EnsureSyncCompletion(_service.{methodName}(), ServiceName, \"{methodAlias}\");");
                    }
                    else
                    {
                        sb.AppendLine($"                        _service.{methodName}();");
                    }
                }
                else
                {
                    if (isAsyncMethod)
                    {
                        sb.AppendLine($"                        BroadcastValidator.EnsureSyncCompletion(_service.{methodName}({callArgsStr}), ServiceName, \"{methodAlias}\");");
                    }
                    else
                    {
                        sb.AppendLine($"                        _service.{methodName}({callArgsStr});");
                    }
                }
                sb.AppendLine("                        ClearContext();");
                sb.AppendLine("                    }");

                // Trigger replay (always, may also have their own patches)
                sb.AppendLine("                    ReplayTriggerOperations(broadcast.TriggerOperations, broadcast.CallerId, broadcast.ServerTimeTicks);");

                // Fire event
                if (paramCount == 0)
                {
                    sb.AppendLine($"                    {eventName}?.Invoke();");
                }
                else if (paramCount == 1)
                {
                    sb.AppendLine($"                    {eventName}?.Invoke({argNames[0]});");
                }
                else
                {
                    sb.AppendLine($"                    {eventName}?.Invoke(({callArgsStr}));");
                }

                sb.AppendLine("                    break;");
                sb.AppendLine("                }");
            }

            sb.AppendLine("            }");
            sb.AppendLine("            _tracker.FlushAndNotify();");
            sb.AppendLine("            OnStateMutated?.Invoke();");
            sb.AppendLine("            }");
            sb.AppendLine("            catch (Exception ex) { _tracker.Discard(); SetError(ex, broadcast.MethodName); throw; }");
            sb.AppendLine("        }");
            sb.AppendLine();

            // Subscriber dispatchers — replay service method to update state, then fire event
            var subscriberApplierName = stateTypeName + "PatchApplier";
            foreach (var subscriber in subscriberInterfaces)
            {
                sb.AppendLine($"        private void Dispatch{subscriber.Name}Broadcast(NetworkBroadcast broadcast)");
                sb.AppendLine("        {");
                sb.AppendLine("            var _tracker = SharedMeta.Core.Reactive.ChangeTracker.Activate();");
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                sb.AppendLine("            switch (broadcast.MethodName)");
                sb.AppendLine("            {");

                foreach (var method in subscriber.Methods)
                {
                    var eventName = GetEventName(method.MethodName);
                    sb.AppendLine($"                case \"{method.MethodName}\":");
                    sb.AppendLine("                {");
                    if (serializer == DetectedSerializer.MemoryPack)
                    {
                        sb.AppendLine($"                    var @event = MemoryPackSerializer.Deserialize<{method.EventTypeName}>(broadcast.ArgsBytes)!;");
                    }
                    else
                    {
                        sb.AppendLine($"                    var @event = _serializer.Unpack<{method.EventTypeName}>(broadcast.ArgsBytes)!;");
                    }

                    // StateBytes check (ServerReplace): replace state wholesale
                    sb.AppendLine("                    if (broadcast.StateBytes is { Length: > 0 } stateData)");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        _state = _serializer.Unpack<{stateTypeName}>(stateData)!;");
                    sb.AppendLine("                        _crossEntityResolver?.UpdateCachedState(_network.EntityId ?? string.Empty, _state);");
                    sb.AppendLine("                        _optimisticRandom?.Skip(broadcast.RandomScrollDelta);");
                    sb.AppendLine("                    }");
                    // Replay the service method to update state
                    sb.AppendLine("                    else if (broadcast.PatchBytes is { Length: > 0 } patchData)");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        var patch = _serializer.Unpack<PatchNode>(patchData);");
                    sb.AppendLine($"                        {subscriberApplierName}.Apply(_state, patch, _serializer);");
                    sb.AppendLine("                        _optimisticRandom?.Skip(broadcast.RandomScrollDelta);");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                    else");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        SetContext(broadcast.ReplayContext, broadcast.CallerId, broadcast.ServerTimeTicks);");
                    if (method.IsAsync)
                    {
                        sb.AppendLine($"                        BroadcastValidator.EnsureSyncCompletion(_service.{method.MethodName}(@event), ServiceName, \"{method.MethodName}\");");
                    }
                    else
                    {
                        sb.AppendLine($"                        _service.{method.MethodName}(@event);");
                    }
                    sb.AppendLine("                        ClearContext();");
                    sb.AppendLine("                    }");

                    // Replay trigger operations if any
                    sb.AppendLine("                    ReplayTriggerOperations(broadcast.TriggerOperations, broadcast.CallerId, broadcast.ServerTimeTicks);");

                    sb.AppendLine($"                    {eventName}?.Invoke(@event);");
                    sb.AppendLine("                    break;");
                    sb.AppendLine("                }");
                }

                sb.AppendLine("            }");
                sb.AppendLine("            _tracker.FlushAndNotify();");
                sb.AppendLine("            OnStateMutated?.Invoke();");
                sb.AppendLine("            }");
                sb.AppendLine("            catch (Exception ex) { _tracker.Discard(); SetError(ex, broadcast.MethodName); throw; }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }
        }

        private static void GenerateTriggerReplayMethods(StringBuilder sb, List<MethodDeclarationSyntax> methods, string? stateTypeName)
        {
            var applierName = stateTypeName + "PatchApplier";

            // ReplayTriggerOperations helper
            sb.AppendLine("        private void ReplayTriggerOperations(List<OperationResult>? triggerOperations, string? callerId, long serverTimeTicks = 0)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (triggerOperations == null) return;");
            sb.AppendLine("            foreach (var triggerOp in triggerOperations)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (triggerOp.Response.StateBytes is { Length: > 0 } stateData)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    _state = _serializer.Unpack<{stateTypeName}>(stateData)!;");
            sb.AppendLine("                    _crossEntityResolver?.UpdateCachedState(_network.EntityId ?? string.Empty, _state);");
            sb.AppendLine("                    _optimisticRandom?.Skip(triggerOp.Response.RandomScrollDelta);");
            sb.AppendLine("                }");
            sb.AppendLine("                else if (triggerOp.Response.PatchBytes is { Length: > 0 } patchData)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    var patch = _serializer.Unpack<PatchNode>(patchData);");
            sb.AppendLine($"                    {applierName}.Apply(_state, patch, _serializer);");
            sb.AppendLine("                    _optimisticRandom?.Skip(triggerOp.Response.RandomScrollDelta);");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            sb.AppendLine("                    SetContext(triggerOp.Response.ReplayPayload ?? Array.Empty<byte>(), callerId, serverTimeTicks);");
            sb.AppendLine("                    DispatchTrigger(triggerOp.Call.MethodName);");
            sb.AppendLine("                    ClearContext();");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();

            // DispatchTrigger switch
            sb.AppendLine("        private void DispatchTrigger(string methodName)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (methodName)");
            sb.AppendLine("            {");

            foreach (var method in methods)
            {
                var methodName = method.Identifier.Text;
                var methodAlias = GetMethodAlias(method, methodName);
                var paramCount = method.ParameterList.Parameters.Count;
                var returnTypeStr = method.ReturnType.ToString();
                bool isAsyncMethod = returnTypeStr.StartsWith("Task") || returnTypeStr.StartsWith("System.Threading.Tasks.Task");

                // Triggers are always parameterless (void or Task)
                if (paramCount == 0)
                {
                    sb.AppendLine($"                case \"{methodAlias}\":");
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
            int paramCount, DetectedSerializer serializer)
        {
            if (serializer == DetectedSerializer.MemoryPack)
            {
                if (paramCount == 1)
                {
                    var paramType = method.ParameterList.Parameters[0].Type!.ToString();
                    var paramName = method.ParameterList.Parameters[0].Identifier.Text;
                    sb.AppendLine($"                    var {paramName} = MemoryPackSerializer.Deserialize<{paramType}>(broadcast.ArgsBytes)!;");
                }
                else
                {
                    // Multiple parameters - use MemoryPackReader
                    sb.AppendLine("                    var mpState = MemoryPackReaderOptionalStatePool.Rent(null);");
                    sb.AppendLine("                    var mpReader = new MemoryPackReader(broadcast.ArgsBytes, mpState);");
                    foreach (var param in method.ParameterList.Parameters)
                    {
                        var paramType = param.Type!.ToString();
                        var paramName = param.Identifier.Text;
                        sb.AppendLine($"                    var {paramName} = mpReader.ReadValue<{paramType}>()!;");
                    }
                    sb.AppendLine("                    mpReader.Dispose();");
                }
            }
            else
            {
                // Generic serializer — always use CreateReader for correct length-prefixed format
                {
                    sb.AppendLine("                    using var reader = _serializer.CreateReader(broadcast.ArgsBytes);");
                    foreach (var param in method.ParameterList.Parameters)
                    {
                        var paramType = param.Type!.ToString();
                        var paramName = param.Identifier.Text;
                        sb.AppendLine($"                    var {paramName} = reader.Read<{paramType}>()!;");
                    }
                }
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
            if (isVoid)
                sb.AppendLine($"            _service.{methodName}({callArgs});");
            else
                sb.AppendLine($"            return _service.{methodName}({callArgs});");
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
                    if (arg.NameEquals != null
                        && arg.NameEquals.Name.Identifier.Text == "Query"
                        && arg.Expression is LiteralExpressionSyntax lit
                        && lit.Token.Text == "true")
                        return true;
                }
            }
            return false;
        }

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
            sb.AppendLine($"{indent}        MismatchKind = (int)SharedMeta.Core.Transport.DesyncMismatchKind.Patch");
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
            sb.AppendLine($"{indent}    MismatchKind = (int)SharedMeta.Core.Transport.DesyncMismatchKind.Result");
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
            sb.AppendLine($"{indent}    MismatchKind = (int)SharedMeta.Core.Transport.DesyncMismatchKind.Random");
            sb.AppendLine($"{indent}}});");
        }

        private static string GetEventName(string methodName)
        {
            if (methodName.StartsWith("On") && methodName.Length > 2 && char.IsUpper(methodName[2]))
            {
                return $"{methodName}_Replayed";
            }
            return $"On{methodName}_Replayed";
        }

        private class SubscriberInterfaceInfo
        {
            public string Name { get; set; } = "";
            public string FullName { get; set; } = "";
            public List<SubscriberMethodInfo> Methods { get; } = new();
        }

        private class SubscriberMethodInfo
        {
            public string MethodName { get; set; } = "";
            public string EventTypeName { get; set; } = "";
            public bool IsAsync { get; set; }
        }
    }
}
