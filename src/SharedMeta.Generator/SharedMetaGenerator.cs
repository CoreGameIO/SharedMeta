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

                // Server-side service API — lets server code (admin tooling, framework grains,
                // background jobs) invoke meta methods on an entity as an ordinary dispatch.
                var serverApiSource = ServerApiGenerator.Generate(symbol, ctx.SemanticModel.Compilation);
                if (serverApiSource != null)
                {
                    var baseName = interfaceName.StartsWith("I") && interfaceName.Length > 1 && char.IsUpper(interfaceName[1])
                        ? interfaceName.Substring(1)
                        : interfaceName;
                    spc.AddSource($"{baseName}ServerApi.g.cs", serverApiSource);
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

                // 0.29.1+ validator runs as a SEPARATE RegisterSourceOutput below, combined with
                // AnalyzerConfigOptionsProvider so it can be opt-out via MSBuild property
                // <SharedMetaDisableReadOnlyValidator>true</SharedMetaDisableReadOnlyValidator>.
                // Kept here as a comment so the connection between the impl pipeline and the
                // validator is visible at the wiring site; actual call is at the bottom of Initialize().

                // Subscriber dispatcher (for framework service events)
                var subscriberSource = SubscriberDispatcherGenerator.Generate(node, symbol);
                if (subscriberSource != null)
                {
                    spc.AddSource($"{symbol.Name}.SubscriberDispatcher.g.cs", subscriberSource);
                }
            });

            // 0.20.0: Entity-caller helper classes (Interface + Recorder/Replayer/
            // LocalEntityCaller/SiblingCaller) — emitted once per (namespace, dep) pair, not
            // per consumer. Multiple [MetaServiceImpl] classes in the same namespace declaring
            // the same dep all reference the same helpers, no CS0111 conflict. Pipeline:
            //   1. Per impl, project the (consumerNamespace, depInterfaceFqn) tuples.
            //   2. Collect → flatten → group into unique pairs.
            //   3. Emit one file per pair.
            var depPairsPipeline = implPipeline
                .Select((ctx, _) =>
                {
                    var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                    if (symbol == null) return System.Collections.Immutable.ImmutableArray<(string Ns, string DepFqn)>.Empty;
                    var attr = symbol.GetAttributes().FirstOrDefault(a =>
                        a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceImplAttribute");
                    if (attr == null || attr.ConstructorArguments.Length < 3)
                        return System.Collections.Immutable.ImmutableArray<(string Ns, string DepFqn)>.Empty;
                    var depsArg = attr.ConstructorArguments[2];
                    if (depsArg.IsNull) return System.Collections.Immutable.ImmutableArray<(string Ns, string DepFqn)>.Empty;
                    var consumerNs = symbol.ContainingNamespace.ToDisplayString();
                    var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<(string Ns, string DepFqn)>();
                    foreach (var d in depsArg.Values)
                    {
                        if (d.Value is INamedTypeSymbol depSym)
                        {
                            // Only entity-services need helpers (server-services have a different shape).
                            bool isEntityService = false;
                            foreach (var iface in depSym.AllInterfaces)
                            {
                                if (iface.ToDisplayString() == "SharedMeta.Core.IMetaService") { isEntityService = true; break; }
                            }
                            if (!isEntityService) continue;
                            // Server-meta-services aren't entity services even if they implement IMetaService.
                            bool isServerMeta = depSym.GetAttributes().Any(a =>
                                a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.ServerMetaServiceAttribute");
                            if (isServerMeta) continue;
                            builder.Add((consumerNs, depSym.ToDisplayString()));
                        }
                    }
                    return builder.ToImmutable();
                })
                .Combine(context.CompilationProvider)
                .SelectMany((tuple, _) => tuple.Left.Select(p => (Ns: p.Ns, DepFqn: p.DepFqn, Comp: tuple.Right)))
                .Collect()
                .SelectMany((all, _) =>
                {
                    // Dedup unique (Ns, DepFqn) pairs across all impls.
                    var seen = new HashSet<string>();
                    var result = System.Collections.Immutable.ImmutableArray.CreateBuilder<(string Ns, string DepFqn, Compilation Comp)>();
                    foreach (var item in all)
                    {
                        var key = $"{item.Ns}::{item.DepFqn}";
                        if (seen.Add(key))
                            result.Add((item.Ns, item.DepFqn, item.Comp));
                    }
                    return result.ToImmutable();
                });

            context.RegisterSourceOutput(depPairsPipeline, (spc, pair) =>
            {
                var depSymbol = pair.Comp.GetTypeByMetadataName(pair.DepFqn);
                if (depSymbol == null) return;
                var source = ContextInjectionGenerator.GenerateHelpersForDep(depSymbol, pair.Ns, pair.Comp);
                // Filename keyed by (namespace, dep) — guarantees one file per unique pair.
                var safeNs = new string(pair.Ns.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                var safeDep = new string(pair.DepFqn.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                spc.AddSource($"{safeNs}_{safeDep}_EntityCallerHelpers.g.cs", source);
            });

            // Stateless Meta Services — Config-property injection + client extension
            // (client.GetI{Iface}Async(), Path 2). Path 1's GetI{Iface}Async() sibling-style
            // getter is emitted by ContextInjectionGenerator as part of the existing dependency
            // loop above, not here.
            var statelessImplPipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharedMeta.Core.StatelessMetaServiceImplAttribute",
                predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
                transform: static (ctx, _) => ctx
            );

            context.RegisterSourceOutput(statelessImplPipeline, (spc, ctx) =>
            {
                var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                if (symbol == null) return;

                var (info, error) = StatelessServiceGenerator.Analyze(symbol);
                if (error != null)
                {
                    spc.AddSource($"{symbol.Name}.StatelessError.g.cs", $"#error {error}\n");
                    return;
                }
                if (info == null) return;

                spc.AddSource($"{symbol.Name}.Context.g.cs", StatelessServiceGenerator.GenerateImplContext(info));
                spc.AddSource($"{info.InterfaceName}StatelessClientExtensions.g.cs", StatelessServiceGenerator.GenerateClientExtension(info));
            });

            // Stateless config-version source — source-based (Shared projects). Wrapped in
            // #if SHAREDMETA_SERVER, mirroring ServerMetaConfigurationGenerator's dual pipeline:
            // the real DI wiring happens in the Server-project assembly-scan pipeline below.
            var statelessInfoPipeline = statelessImplPipeline.Select((ctx, _) =>
            {
                var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                if (symbol == null) return (StatelessServiceGenerator.StatelessImplInfo?)null;
                var (info, error) = StatelessServiceGenerator.Analyze(symbol);
                return error != null ? null : info;
            }).Where(static info => info != null);

            var collectedStatelessSource = statelessInfoPipeline.Collect();
            var statelessSourceWithCompilation = collectedStatelessSource.Combine(context.CompilationProvider);

            context.RegisterSourceOutput(statelessSourceWithCompilation, (spc, tuple) =>
            {
                var (infos, compilation) = tuple;
                var valid = infos.Where(i => i != null).Select(i => i!).ToList();
                if (valid.Count == 0) return;

                var isServerProject = compilation.ReferencedAssemblyNames
                    .Any(a => a.Name == "SharedMeta.Server.Core");
                if (isServerProject) return; // handled by the assembly-scan pipeline below

                var rootNamespace = valid.First().ImplNamespace;
                var source = StatelessServiceGenerator.GenerateVersionSource(rootNamespace, valid, wrapped: true);
                spc.AddSource("GeneratedStatelessConfigVersionSource.g.cs", source);
            });

            // Stateless config-version source — assembly-scan (Server projects). Scans referenced
            // assemblies for [StatelessMetaServiceImpl] types, same pattern as
            // ServerMetaConfigurationGenerator's GenerateForServerProject path.
            context.RegisterSourceOutput(context.CompilationProvider, (spc, compilation) =>
            {
                var isServerProject = compilation.ReferencedAssemblyNames
                    .Any(a => a.Name == "SharedMeta.Server.Core");
                if (!isServerProject) return;

                var statelessImplAttrName = "SharedMeta.Core.StatelessMetaServiceImplAttribute";
                var infos = new List<StatelessServiceGenerator.StatelessImplInfo>();

                foreach (var reference in compilation.References)
                {
                    var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                    if (assemblySymbol == null) continue;

                    var assemblyName = assemblySymbol.Name;
                    if (assemblyName.StartsWith("System") || assemblyName.StartsWith("Microsoft") ||
                        assemblyName.StartsWith("netstandard") || assemblyName == "mscorlib" ||
                        assemblyName.StartsWith("Orleans") || assemblyName == "MemoryPack.Core" ||
                        assemblyName == "MessagePack" || assemblyName == "MessagePack.Annotations")
                        continue;

                    var typesWithAttr = GetTypesWithAttribute(assemblySymbol.GlobalNamespace, statelessImplAttrName);
                    foreach (var typeSymbol in typesWithAttr)
                    {
                        var (info, error) = StatelessServiceGenerator.Analyze(typeSymbol);
                        if (error == null && info != null) infos.Add(info);
                    }
                }

                if (infos.Count == 0) return;

                var rootNamespace = infos.First().ImplNamespace;
                var source = StatelessServiceGenerator.GenerateVersionSource(rootNamespace, infos, wrapped: false);
                spc.AddSource("GeneratedStatelessConfigVersionSource.g.cs", source);
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
                    return GameServiceDiscoveryGenerator.Analyze(symbol, ctx.SemanticModel.Compilation);
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
                // 0.27.0+ Auto-emit AddSharedMetaConfigProvider<T>() only when the
                // SharedMeta.Orleans assembly is referenced — pure SharedMeta.Server.Core hosts
                // (tests, in-proc backends, alternative registries) don't see those calls.
                var hasOrleansConfig = compilation.ReferencedAssemblyNames
                    .Any(a => a.Name == "SharedMeta.Orleans");
                var source = ServerMetaConfigurationGenerator.Generate(rootNamespace, validImpls!, hasOrleansConfig);
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
                var hasOrleansConfig = compilation.ReferencedAssemblyNames
                    .Any(a => a.Name == "SharedMeta.Orleans");
                var source = ServerMetaConfigurationGenerator.GenerateForServerProject(rootNamespace, implInfos, hasOrleansConfig);
                if (!string.IsNullOrEmpty(source))
                {
                    spc.AddSource("ServerMetaConfiguration.g.cs", source!);
                }

                // Server APIs for the referenced services. The shared assembly emits these too, but
                // fenced behind SHAREDMETA_SERVER, which shared projects do not define — so without
                // this pass the API would exist in no compiled assembly at all.
                var emittedServerApis = new HashSet<string>();
                foreach (var impl in implInfos)
                {
                    var serviceSymbol = compilation.GetTypeByMetadataName(impl.InterfaceFullName);
                    if (serviceSymbol == null) continue;
                    if (!emittedServerApis.Add(impl.InterfaceFullName)) continue;

                    var apiSource = ServerApiGenerator.Generate(serviceSymbol, compilation, emitServerGuard: false);
                    if (string.IsNullOrEmpty(apiSource)) continue;

                    var apiBaseName = impl.InterfaceName.StartsWith("I") && impl.InterfaceName.Length > 1
                        && char.IsUpper(impl.InterfaceName[1])
                        ? impl.InterfaceName.Substring(1)
                        : impl.InterfaceName;
                    spc.AddSource($"{apiBaseName}ServerApi.g.cs", apiSource!);
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

            // 0.29.1+ Read-only contract validator (LocalQuery / Query method bodies). Combines
            // the impl pipeline with AnalyzerConfigOptionsProvider so the validator becomes a
            // no-op when the host opts out via
            //   <PropertyGroup>
            //     <SharedMetaDisableReadOnlyValidator>true</SharedMetaDisableReadOnlyValidator>
            //   </PropertyGroup>
            // Useful when generator-side compile cost matters more than catching read-only
            // contract violations at build time. Default: validator ON.
            var validatorEnabled = context.AnalyzerConfigOptionsProvider.Select((opts, _) =>
            {
                opts.GlobalOptions.TryGetValue("build_property.SharedMetaDisableReadOnlyValidator", out var v);
                return !string.Equals(v, "true", System.StringComparison.OrdinalIgnoreCase);
            });
            var validatorPipeline = implPipeline.Combine(validatorEnabled);
            context.RegisterSourceOutput(validatorPipeline, (spc, tuple) =>
            {
                var (ctx, enabled) = tuple;
                if (!enabled) return;
                var node = ctx.TargetNode as Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax;
                if (node == null) return;
                var symbol = ctx.TargetSymbol as INamedTypeSymbol;
                if (symbol == null) return;

                var readOnlyChecks = ReadOnlyMethodValidator.Generate(node, symbol, ctx.SemanticModel.Compilation);
                if (readOnlyChecks != null)
                {
                    spc.AddSource($"{symbol.Name}.ReadOnlyChecks.g.cs", readOnlyChecks);
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
