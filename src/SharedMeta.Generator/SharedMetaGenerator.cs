using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharedMeta.Generator.Generators;

namespace SharedMeta.Generator
{
    [Generator]
    public class SharedMetaGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Debugging helper: Uncomment to launch debugger
            // if (!System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Launch();

            var pipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharedMeta.Core.MetaServiceAttribute",
                predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax,
                transform: static (ctx, _) => ctx // Pass full context for semantic model
            );

            context.RegisterSourceOutput(pipeline, (spc, ctx) =>
            {
                var node = ctx.TargetNode as Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax;
                if (node == null) return;
                
                var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                if (symbol == null) return;
                
                var interfaceName = symbol.Name;
                var namespaceName = symbol.ContainingNamespace.ToDisplayString();

                // Client Proxy Generation (legacy IMetaProvider-based)
                var clientSource = ClientProxyGenerator.Generate(interfaceName, namespaceName, node);
                spc.AddSource($"{interfaceName}Client.g.cs", clientSource);

                // Server Dispatcher Generation (with trigger support)
                var serverSource = ServerDispatcherGenerator.Generate(interfaceName, namespaceName, node, symbol, ctx.SemanticModel.Compilation);
                spc.AddSource($"{interfaceName}Dispatcher.g.cs", serverSource);
                
                // ApiClient Generation (INetwork-based with runtime mode switching)
                // Replaces the old XApiClientGenerator
                var simplifiedSource = SimplifiedApiClientGenerator.Generate(node, symbol, ctx.SemanticModel.Compilation);
                if (simplifiedSource != null)
                {
                    var baseName = interfaceName.StartsWith("I") && interfaceName.Length > 1 && char.IsUpper(interfaceName[1])
                        ? interfaceName.Substring(1)
                        : interfaceName;
                    spc.AddSource($"{baseName}ApiClient.g.cs", simplifiedSource);
                }

                // QueryClient Generation (for [MetaMethod(Query = true)] methods)
                var querySource = QueryClientGenerator.Generate(node, symbol, ctx.SemanticModel.Compilation);
                if (querySource != null)
                {
                    var baseName = interfaceName.StartsWith("I") && interfaceName.Length > 1 && char.IsUpper(interfaceName[1])
                        ? interfaceName.Substring(1)
                        : interfaceName;
                    spc.AddSource($"{baseName}QueryApi.g.cs", querySource);
                }

                // Service Registration Extensions (DI registration)
                var registrationSource = ServiceRegistrationGenerator.Generate(node, symbol, ctx.SemanticModel.Compilation);
                if (registrationSource != null)
                {
                    var baseName = interfaceName.StartsWith("I") && interfaceName.Length > 1 && char.IsUpper(interfaceName[1])
                        ? interfaceName.Substring(1)
                        : interfaceName;
                    spc.AddSource($"{baseName}ServiceExtensions.g.cs", registrationSource);
                }
            });

            // Context Injection (Partial Classes)
            var implPipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharedMeta.Core.MetaServiceImplAttribute",
                predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
                transform: static (ctx, _) => ctx // Pass the whole context to get semantic model
            );

            context.RegisterSourceOutput(implPipeline, (spc, ctx) =>
            {
                var node = ctx.TargetNode as Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax;
                if (node == null) return;

                var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                if (symbol == null) return;

                // Context injection (State, CallerId, dependencies)
                var source = ContextInjectionGenerator.Generate(node, symbol, ctx.SemanticModel.Compilation);
                if (source != null)
                {
                    spc.AddSource($"{symbol.Name}.Context.g.cs", source);
                }

                // Deep desync: generate PatchTracked copy of the service class
                var deepDesyncSource = PatchTrackedClassGenerator.Generate(node, symbol, ctx.SemanticModel.Compilation);
                if (deepDesyncSource != null)
                {
                    spc.AddSource($"{symbol.Name}_PatchTracked.g.cs", deepDesyncSource);
                }

                // Subscriber dispatcher (for framework service events)
                var subscriberSource = SubscriberDispatcherGenerator.Generate(node, symbol);
                if (subscriberSource != null)
                {
                    spc.AddSource($"{symbol.Name}.SubscriberDispatcher.g.cs", subscriberSource);
                }
            });

            // Server Meta Service Wrappers (Recorder + Replayer)
            var serverServicePipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharedMeta.Core.ServerMetaServiceAttribute",
                predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax,
                transform: static (ctx, _) => ctx // Pass full context
            );

            context.RegisterSourceOutput(serverServicePipeline, (spc, ctx) =>
            {
                var node = ctx.TargetNode as Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax;
                if (node == null) return;

                var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                if (symbol == null) return;

                var (recorder, replayer) = ServerServiceWrapperGenerator.Generate(node, symbol);
                
                var baseName = symbol.Name;
                if (baseName.StartsWith("I") && baseName.Length > 1 && char.IsUpper(baseName[1]))
                {
                    baseName = baseName.Substring(1);
                }
                
                if (recorder != null)
                {
                    spc.AddSource($"{baseName}Recorder.g.cs", recorder);
                }
                if (replayer != null)
                {
                    spc.AddSource($"{baseName}Replayer.g.cs", replayer);
                }
            });

            // GameServiceDiscovery Generation
            // Collects all [MetaService] interfaces and generates discovery class
            var discoveryPipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharedMeta.Core.MetaServiceAttribute",
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                    if (symbol == null) return null;
                    return GameServiceDiscoveryGenerator.Analyze(symbol);
                }
            ).Where(static info => info != null);

            var collectedServices = discoveryPipeline.Collect();

            context.RegisterSourceOutput(collectedServices, (spc, services) =>
            {
                var validServices = services.Where(s => s != null && s!.StateTypeFullName != null).ToList()!;
                if (validServices.Count == 0) return;

                // Use the first namespace as the root namespace for the discovery class
                var rootNamespace = validServices.First()!.Namespace;

                var source = GameServiceDiscoveryGenerator.Generate(rootNamespace, validServices!);
                if (!string.IsNullOrEmpty(source))
                {
                    spc.AddSource("GameServiceDiscovery.g.cs", source);
                }
            });

            // Client Service Aggregate Registration
            // Collects all [MetaService] interfaces and generates RegisterAllServices() extension method
            var clientAggregatePipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharedMeta.Core.MetaServiceAttribute",
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                    if (symbol == null) return null;
                    return ClientServiceAggregateGenerator.Analyze(symbol);
                }
            ).Where(static info => info != null);

            var collectedClientServices = clientAggregatePipeline.Collect();

            context.RegisterSourceOutput(collectedClientServices, (spc, services) =>
            {
                var validServices = services.Where(s => s != null).ToList()!;
                if (validServices.Count == 0) return;

                var rootNamespace = validServices.First()!.Namespace;

                var source = ClientServiceAggregateGenerator.Generate(rootNamespace, validServices!);
                if (!string.IsNullOrEmpty(source))
                {
                    spc.AddSource("MetaClientExtensions.g.cs", source);
                }
            });

            // MessagePack Configuration Generation
            // Generates GeneratedMetaMessagePackConfiguration.Configure() with CompositeResolver
            // from all referenced assemblies that have source-generated MessagePack resolvers.
            context.RegisterSourceOutput(context.CompilationProvider, (spc, compilation) =>
            {
                var source = MessagePackConfigurationGenerator.Generate(compilation);
                if (!string.IsNullOrEmpty(source))
                {
                    spc.AddSource("GeneratedMetaMessagePackConfiguration.g.cs", source!);
                }
            });

            // NOTE: MetaProviderBase generation is disabled because it generates server-side code
            // that depends on SharedMeta.Server.Core, but [MetaService] interfaces are in Shared projects.
            // Instead, create MetaProviderBase manually in your server project.
            // See CardGame.Server for an example.

            // Server Meta Configuration Generation - Source-based (for Shared projects)
            // Collects [MetaServiceImpl] classes from SOURCE and generates with #if wrapper.
            var serverConfigSourcePipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharedMeta.Core.MetaServiceImplAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                    if (symbol == null) return null;
                    return ServerMetaConfigurationGenerator.Analyze(symbol);
                }
            ).Where(static info => info != null);

            var collectedSourceImpls = serverConfigSourcePipeline.Collect();

            // Combine with compilation to check if this is NOT a server project
            var sourceImplsWithCompilation = collectedSourceImpls.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(sourceImplsWithCompilation, (spc, tuple) =>
            {
                var (impls, compilation) = tuple;
                var validImpls = impls.Where(i => i != null).ToList()!;
                if (validImpls.Count == 0) return;

                // Only generate from source in Shared projects (not server projects)
                // Server projects use the referenced assembly pipeline below
                var isServerProject = compilation.ReferencedAssemblyNames
                    .Any(a => a.Name == "SharedMeta.Server.Core");
                if (isServerProject) return;

                // Resolve DefaultConfig references
                ServerMetaConfigurationGenerator.ResolveDefaultConfigs(validImpls!, compilation);

                var rootNamespace = validImpls.First()!.Namespace;
                var source = ServerMetaConfigurationGenerator.Generate(rootNamespace, validImpls!);
                if (!string.IsNullOrEmpty(source))
                {
                    spc.AddSource("ServerMetaConfiguration.g.cs", source!);
                }
            });

            // Server Meta Configuration Generation - Assembly-based (for Server projects)
            // Scans REFERENCED ASSEMBLIES for [MetaServiceImpl] types and generates without #if wrapper.
            context.RegisterSourceOutput(context.CompilationProvider, (spc, compilation) =>
            {
                // Only generate in server projects (those that reference SharedMeta.Server.Core)
                var isServerProject = compilation.ReferencedAssemblyNames
                    .Any(a => a.Name == "SharedMeta.Server.Core");
                if (!isServerProject) return;

                var implInfos = new List<ServiceImplInfo>();
                var metaServiceImplAttrName = "SharedMeta.Core.MetaServiceImplAttribute";

                // Scan all referenced assemblies for [MetaServiceImpl] types
                foreach (var reference in compilation.References)
                {
                    var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                    if (assemblySymbol == null) continue;

                    // Skip system/framework assemblies
                    var assemblyName = assemblySymbol.Name;
                    if (assemblyName.StartsWith("System") || assemblyName.StartsWith("Microsoft") ||
                        assemblyName.StartsWith("netstandard") || assemblyName == "mscorlib" ||
                        assemblyName.StartsWith("Orleans") || assemblyName == "MemoryPack.Core" ||
                        assemblyName == "MessagePack" || assemblyName == "MessagePack.Annotations")
                        continue;

                    // Find all types with [MetaServiceImpl] attribute
                    var typesWithAttr = GetTypesWithAttribute(assemblySymbol.GlobalNamespace, metaServiceImplAttrName);
                    foreach (var typeSymbol in typesWithAttr)
                    {
                        var info = ServerMetaConfigurationGenerator.Analyze(typeSymbol);
                        if (info != null)
                        {
                            implInfos.Add(info);
                        }
                    }
                }

                if (implInfos.Count == 0) return;

                // Resolve DefaultConfig references
                ServerMetaConfigurationGenerator.ResolveDefaultConfigs(implInfos, compilation);

                var rootNamespace = implInfos.First().Namespace;
                var source = ServerMetaConfigurationGenerator.GenerateForServerProject(rootNamespace, implInfos);
                if (!string.IsNullOrEmpty(source))
                {
                    spc.AddSource("ServerMetaConfiguration.g.cs", source!);
                }
            });

            // State Patch Generation (PatchWrapper + PatchApplier per ISharedState type)
            var sharedStatePipeline = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is ClassDeclarationSyntax cds &&
                    cds.BaseList != null &&
                    cds.BaseList.Types.Any(t => t.ToString().Contains("ISharedState")),
                transform: static (ctx, _) =>
                {
                    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol;
                    if (symbol == null) return null;
                    // Verify it actually implements ISharedState
                    if (!symbol.AllInterfaces.Any(i =>
                        i.ToDisplayString() == "SharedMeta.Core.ISharedState"))
                        return null;
                    return StatePatchGenerator.Analyze(symbol);
                }
            ).Where(static info => info != null);

            var collectedStates = sharedStatePipeline.Collect();

            context.RegisterSourceOutput(collectedStates, (spc, states) =>
            {
                var validStates = states.Where(s => s != null).ToList();
                foreach (var state in validStates)
                {
                    var source = StatePatchGenerator.Generate(state!);
                    if (source != null)
                    {
                        spc.AddSource($"{state!.RootType.TypeName}Patch.g.cs", source);
                    }

                    // Companion schema for diagnostic / desync rendering
                    var schemaSource = PatchSchemaGenerator.Generate(state!);
                    if (schemaSource != null)
                    {
                        spc.AddSource($"{state!.RootType.TypeName}PatchSchema.g.cs", schemaSource);
                    }
                }
            });

            // Push-Based Change Tracking (classes with [Tracked] private fields → generated property setters)
            var trackedPipeline = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is ClassDeclarationSyntax cds &&
                    cds.Members.Any(m => m is FieldDeclarationSyntax fds &&
                        fds.AttributeLists.Any(al => al.Attributes.Any(a =>
                            a.Name.ToString().Contains("Tracked")))),
                transform: static (ctx, _) =>
                {
                    var symbol = ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol;
                    if (symbol == null) return null;
                    return TrackedStateGenerator.AnalyzeSingle(symbol);
                }
            ).Where(static info => info != null).Collect();

            context.RegisterSourceOutput(trackedPipeline, (spc, allInfos) =>
            {
                var allTypes = TrackedStateGenerator.CollectAllTypes(allInfos);
                var source = TrackedStateGenerator.Generate(allTypes);
                if (source != null)
                {
                    spc.AddSource("ChangeTracking.g.cs", source);
                }
            });

            // Transformer Registration Generation
            // Scans for classes implementing IArgumentTransformer or IStateArgumentTransformer
            var transformerPipeline = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax cds && cds.BaseList != null,
                transform: static (ctx, _) =>
                {
                    var classNode = (ClassDeclarationSyntax)ctx.Node;
                    var symbol = ctx.SemanticModel.GetDeclaredSymbol(classNode) as INamedTypeSymbol;
                    if (symbol == null) return null;

                    return TransformerRegistrationGenerator.Analyze(symbol);
                }
            ).Where(static info => info != null);

            // Collect all transformers and generate a single registration file per assembly
            var collected = transformerPipeline.Collect();

            context.RegisterSourceOutput(collected, (spc, transformers) =>
            {
                var validTransformers = transformers.Where(t => t != null).ToList()!;
                if (validTransformers.Count == 0) return;

                // Group by namespace for better organization
                var byNamespace = validTransformers
                    .GroupBy(t => GetNamespace(t!.TransformerFullName))
                    .ToList();

                foreach (var group in byNamespace)
                {
                    var ns = group.Key;
                    var source = TransformerRegistrationGenerator.Generate(ns, group!);
                    var safeNs = ns.Replace(".", "_");
                    spc.AddSource($"TransformerRegistrations_{safeNs}.g.cs", source);
                }
            });
        }

        private static string GetNamespace(string fullTypeName)
        {
            var lastDot = fullTypeName.LastIndexOf('.');
            return lastDot > 0 ? fullTypeName.Substring(0, lastDot) : "Global";
        }

        /// <summary>
        /// Recursively find all types with a specific attribute in a namespace.
        /// </summary>
        private static IEnumerable<INamedTypeSymbol> GetTypesWithAttribute(INamespaceSymbol ns, string attributeFullName)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                if (type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeFullName))
                {
                    yield return type;
                }

                // Check nested types
                foreach (var nested in GetNestedTypesWithAttribute(type, attributeFullName))
                {
                    yield return nested;
                }
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                foreach (var type in GetTypesWithAttribute(childNs, attributeFullName))
                {
                    yield return type;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> GetNestedTypesWithAttribute(INamedTypeSymbol type, string attributeFullName)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                if (nested.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeFullName))
                {
                    yield return nested;
                }

                foreach (var deepNested in GetNestedTypesWithAttribute(nested, attributeFullName))
                {
                    yield return deepNested;
                }
            }
        }
    }
}
