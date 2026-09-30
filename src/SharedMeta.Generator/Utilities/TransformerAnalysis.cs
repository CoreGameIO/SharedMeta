using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharedMeta.Generator.Utilities
{
    /// <summary>
    /// One parameter's compile-time transformation decision.
    /// </summary>
    public sealed class ParameterTransform
    {
        /// <summary>Parameter identifier, exactly as the declaration writes it.</summary>
        public string Name { get; set; } = "";

        /// <summary>Parameter type as the declaration writes it — the type the method body sees.</summary>
        public string DeclaredType { get; set; } = "";

        /// <summary>Fully-qualified transformer type, or null when this parameter is not transformed.</summary>
        public string? TransformerType { get; set; }

        /// <summary>Fully-qualified boxed type, or null when this parameter is not transformed.</summary>
        public string? SimpleType { get; set; }

        /// <summary>Fully-qualified state type for state-aware transformers, else null.</summary>
        public string? StateType { get; set; }

        /// <summary>Fully-qualified <c>[ServiceConfig]</c> type for config-aware transformers, else null.</summary>
        public string? ConfigType { get; set; }

        public bool Transformed => TransformerType != null;

        /// <summary>The type that actually goes on the wire for this parameter.</summary>
        public string WireType => SimpleType ?? DeclaredType;

        /// <summary>Name of the generated local holding the wire-shaped value.</summary>
        public string WireLocal => "__wire_" + Name;
    }

    /// <summary>
    /// Where a generated call site finds the state and config a transformer may ask for.
    /// </summary>
    public sealed class TransformScope
    {
        private readonly System.Func<ParameterTransform, string> _state;
        private readonly System.Func<ParameterTransform, string> _config;

        private TransformScope(System.Func<ParameterTransform, string> state, System.Func<ParameterTransform, string> config)
        {
            _state = state;
            _config = config;
        }

        public string StateExpr(ParameterTransform t) => _state(t);
        public string ConfigExpr(ParameterTransform t) => _config(t);

        /// <summary>Generated ApiClient: its own entity's state and resolved service configs.</summary>
        public static TransformScope ApiClient { get; } = new(
            _ => "_state",
            t => $"(global::SharedMeta.Core.ServiceConfigLookup.Find<{t.ConfigType}>(_serviceConfigs)"
                 + MissingConfig(t, "is not resolved for this entity (is its client config provider registered?)"));

        /// <summary>Code running with a <c>MetaContext</c> in scope (dispatchers).</summary>
        public static TransformScope Context(string contextExpr) => new(
            t => $"(({t.StateType}){contextExpr}.StateObject)",
            t => $"({contextExpr}.GetServiceConfig<{t.ConfigType}>()"
                 + MissingConfig(t, "is not resolved for this call (is its config provider registered?)"));

        /// <summary>
        /// A sender that owns no state or configs of its own — server-originated calls (admin
        /// APIs, cross-entity hops) and query proxies. The transformer needs what its Box was
        /// written against; the ambient meta call is the only candidate, and if it lacks it there
        /// is no sensible fallback, so fail with a message that names the cause.
        /// </summary>
        public static TransformScope Ambient { get; } = new(
            t => $"(global::SharedMeta.Core.MetaContextAccessor.Current?.StateObject as {t.StateType}"
                 + $" ?? throw new System.InvalidOperationException("
                 + $"\"Transformer {Display(t.TransformerType)} needs a {Display(t.StateType)} context, which this call path does not have.\"))",
            t => $"(global::SharedMeta.Core.MetaContextAccessor.Current?.GetServiceConfig<{t.ConfigType}>()"
                 + MissingConfig(t, "this call path does not have: it boxes with the config of the meta call it runs in,"
                                    + " so call it from a meta method whose service declares that config"));

        private static string MissingConfig(ParameterTransform t, string reason)
            => $" ?? throw new System.InvalidOperationException("
               + $"\"Transformer {Display(t.TransformerType)} needs config {Display(t.ConfigType)}, which {reason}.\"))";

        /// <summary>Type name for messages — without the <c>global::</c> alias code needs.</summary>
        internal static string Display(string? fqn) => (fqn ?? "").Replace("global::", "");
    }

    /// <summary>
    /// Resolves, at compile time, which argument transformer applies to each parameter of a meta
    /// method — the single source of truth every side of the wire generates against.
    /// </summary>
    /// <remarks>
    /// The decision must be identical for the writer and the reader of a payload, so it may not
    /// depend on anything observed at runtime: a registry populated by one process and not the
    /// other silently misframes every argument after the first transformed one. Everything here
    /// is derived from the compilation alone.
    /// </remarks>
    public static class TransformerAnalysis
    {
        private const string TransformAttr = "SharedMeta.Core.TransformAttribute";
        private const string SkipTransformAttr = "SharedMeta.Core.SkipTransformAttribute";
        private const string TransformerAttr = "SharedMeta.Core.TransformerAttribute";
        private const string SimpleInterface = "SharedMeta.Core.IArgumentTransformer<";
        private const string StateInterface = "SharedMeta.Core.IStateArgumentTransformer<";
        private const string ConfigInterface = "SharedMeta.Core.IConfigArgumentTransformer<";
        private const string StateConfigInterface = "SharedMeta.Core.IStateConfigArgumentTransformer<";

        private sealed class CatalogEntry
        {
            public string TransformerType = "";
            public string SimpleType = "";
            public string? StateType;
            public string? ConfigType;
        }

        // Scanning every referenced assembly for transformer implementations is not free, and the
        // generators below ask for the same answer once per emitted file.
        private static readonly ConditionalWeakTable<Compilation, Dictionary<string, CatalogEntry>> _catalogs = new();

        /// <summary>
        /// Per-parameter decisions for a method declared in syntax (service interfaces).
        /// </summary>
        public static List<ParameterTransform> Analyze(
            SeparatedSyntaxList<ParameterSyntax> parameters, Compilation? compilation)
        {
            var result = new List<ParameterTransform>(parameters.Count);

            foreach (var param in parameters)
            {
                var entry = new ParameterTransform
                {
                    Name = param.Identifier.Text,
                    DeclaredType = param.Type?.ToString() ?? "object",
                };
                result.Add(entry);

                if (compilation == null) continue;

                var attrs = param.AttributeLists.SelectMany(a => a.Attributes).ToList();
                if (attrs.Any(a => a.Name.ToString().Contains("SkipTransform")))
                    continue;

                var model = compilation.GetSemanticModel(param.SyntaxTree);

                var explicitAttr = attrs.FirstOrDefault(a =>
                    a.Name.ToString().Contains("Transform") && !a.Name.ToString().Contains("SkipTransform"));
                if (explicitAttr?.ArgumentList?.Arguments.Count > 0
                    && explicitAttr.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax typeOf)
                {
                    var transformerSymbol = model.GetSymbolInfo(typeOf.Type).Symbol as INamedTypeSymbol;
                    Apply(entry, Describe(transformerSymbol));
                    continue;
                }

                if (param.Type == null) continue;
                var paramSymbol = model.GetSymbolInfo(param.Type).Symbol as ITypeSymbol;
                if (paramSymbol == null) continue;
                Apply(entry, Lookup(compilation, Fqn(paramSymbol)));
            }

            return result;
        }

        /// <summary>
        /// Per-parameter decisions for a method reached through its symbol (referenced assemblies).
        /// </summary>
        public static List<ParameterTransform> Analyze(
            IEnumerable<IParameterSymbol> parameters, Compilation? compilation)
        {
            var result = new List<ParameterTransform>();

            foreach (var param in parameters)
            {
                var entry = new ParameterTransform
                {
                    Name = param.Name,
                    DeclaredType = param.Type.ToDisplayString(),
                };
                result.Add(entry);

                if (compilation == null) continue;

                var attrs = param.GetAttributes();
                if (attrs.Any(a => a.AttributeClass?.ToDisplayString() == SkipTransformAttr))
                    continue;

                var explicitAttr = attrs.FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == TransformAttr);
                if (explicitAttr != null && explicitAttr.ConstructorArguments.Length > 0
                    && explicitAttr.ConstructorArguments[0].Value is INamedTypeSymbol named)
                {
                    Apply(entry, Describe(named));
                    continue;
                }

                Apply(entry, Lookup(compilation, Fqn(param.Type)));
            }

            return result;
        }

        public static bool AnyTransformed(IEnumerable<ParameterTransform> transforms)
            => transforms.Any(t => t.Transformed);

        /// <summary>Expression producing the boxed (wire-shaped) value for a transformed parameter.</summary>
        public static string BoxExpr(ParameterTransform t, string valueExpr, TransformScope scope)
            => $"global::SharedMeta.Core.MetaTransformer<{t.TransformerType}>.Instance.Box({Args(t, valueExpr, scope)})";

        /// <summary>Expression producing the method-shaped value from a boxed one.</summary>
        public static string UnboxExpr(ParameterTransform t, string valueExpr, TransformScope scope)
            => $"global::SharedMeta.Core.MetaTransformer<{t.TransformerType}>.Instance.Unbox({Args(t, valueExpr, scope)})";

        private static string Args(ParameterTransform t, string valueExpr, TransformScope scope)
        {
            var args = valueExpr;
            if (t.StateType != null) args += ", " + scope.StateExpr(t);
            if (t.ConfigType != null) args += ", " + scope.ConfigExpr(t);
            return args;
        }

        /// <summary>
        /// One <c>#error</c> line per parameter of <paramref name="service"/> whose config-aware
        /// transformer needs a config the service does not declare with <c>[ServiceConfig]</c>.
        /// </summary>
        /// <remarks>
        /// Configs are looked up by type in the entity's resolved <c>[ServiceConfig]</c> list at
        /// runtime; an undeclared type is never resolved, so without this check the call would
        /// build and then throw on its first use. The legacy <c>ConfigType</c> is not a source —
        /// it lives outside that list and is on its way out.
        /// </remarks>
        public static List<string> ConfigDeclarationErrors(INamedTypeSymbol service, Compilation compilation)
        {
            var errors = new List<string>();

            var declared = new HashSet<string>(service.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.ServiceConfigAttribute"
                            && a.ConstructorArguments.Length > 0
                            && a.ConstructorArguments[0].Value is ITypeSymbol)
                .Select(a => Fqn((ITypeSymbol)a.ConstructorArguments[0].Value!)));

            var methods = service.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary)
                .Concat(ImplDeclaredMethods.SymbolsForService(service, compilation));

            foreach (var method in methods)
            {
                foreach (var t in Analyze(method.Parameters, compilation))
                {
                    if (t.ConfigType == null || declared.Contains(t.ConfigType)) continue;
                    var config = TransformScope.Display(t.ConfigType);
                    errors.Add($"#error SharedMeta: {service.Name}.{method.Name} parameter '{t.Name}' uses transformer "
                               + $"{TransformScope.Display(t.TransformerType)}, which needs config {config}, but {service.Name} "
                               + $"does not declare it. Add [ServiceConfig(typeof({config}), \"...\")] to {service.Name}.");
                }
            }

            return errors;
        }

        private static void Apply(ParameterTransform entry, CatalogEntry? found)
        {
            if (found == null) return;
            entry.TransformerType = found.TransformerType;
            entry.SimpleType = found.SimpleType;
            entry.StateType = found.StateType;
            entry.ConfigType = found.ConfigType;
        }

        private static CatalogEntry? Lookup(Compilation compilation, string complexTypeFqn)
            => GetCatalog(compilation).TryGetValue(complexTypeFqn, out var entry) ? entry : null;

        private static Dictionary<string, CatalogEntry> GetCatalog(Compilation compilation)
        {
            if (_catalogs.TryGetValue(compilation, out var cached))
                return cached;

            var catalog = BuildCatalog(compilation);
            _catalogs.Add(compilation, catalog);
            return catalog;
        }

        private static Dictionary<string, CatalogEntry> BuildCatalog(Compilation compilation)
        {
            var catalog = new Dictionary<string, CatalogEntry>();

            var assemblies = new List<IAssemblySymbol> { compilation.Assembly };
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly
                    && !IsFrameworkAssembly(assembly.Name))
                    assemblies.Add(assembly);
            }

            // Ties are resolved by name so two builds of the same sources agree — an ambiguous
            // complex type is a declaration bug, but it must not produce two different wires.
            foreach (var type in assemblies.SelectMany(a => AllTypes(a.GlobalNamespace))
                         .OrderBy(t => t.ToDisplayString(), System.StringComparer.Ordinal))
            {
                var described = Describe(type, requireAutoRegister: true);
                if (described == null) continue;

                var complexType = ComplexTypeOf(type);
                if (complexType == null || catalog.ContainsKey(complexType)) continue;
                catalog[complexType] = described;
            }

            return catalog;
        }

        /// <summary>
        /// Reads a transformer type's contract. Returns null when the type is not a usable
        /// transformer — a generated call site needs a shared singleton, so anything the
        /// framework cannot construct itself is not one.
        /// </summary>
        private static CatalogEntry? Describe(INamedTypeSymbol? type, bool requireAutoRegister = false)
        {
            if (type == null || type.IsAbstract || type.IsGenericType || type.TypeKind != TypeKind.Class)
                return null;
            if (!type.Constructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                return null;

            var attr = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == TransformerAttr);
            if (attr != null)
            {
                if (Flag(attr, "UseResolver")) return null;
                if (requireAutoRegister && Flag(attr, "NoAutoRegister")) return null;
            }

            var contract = FindContract(type);
            if (contract == null) return null;
            var (iface, hasState, hasConfig) = contract.Value;

            return new CatalogEntry
            {
                TransformerType = Fqn(type),
                SimpleType = Fqn(iface.TypeArguments[1]),
                StateType = hasState ? Fqn(iface.TypeArguments[2]) : null,
                ConfigType = hasConfig ? Fqn(iface.TypeArguments[hasState ? 3 : 2]) : null,
            };
        }

        private static string? ComplexTypeOf(INamedTypeSymbol type)
        {
            var contract = FindContract(type);
            return contract == null ? null : Fqn(contract.Value.Iface.TypeArguments[0]);
        }

        /// <summary>
        /// The transformer interface a type is generated against. A type implementing several
        /// takes the one carrying the most context, in a fixed order so every build agrees.
        /// </summary>
        private static (INamedTypeSymbol Iface, bool HasState, bool HasConfig)? FindContract(INamedTypeSymbol type)
        {
            INamedTypeSymbol? Find(string prefix, int arity) => type.AllInterfaces.FirstOrDefault(i =>
                i.TypeArguments.Length == arity && i.ConstructedFrom.ToDisplayString().StartsWith(prefix));

            if (Find(StateConfigInterface, 4) is { } stateConfig) return (stateConfig, true, true);
            if (Find(StateInterface, 3) is { } state) return (state, true, false);
            if (Find(ConfigInterface, 3) is { } config) return (config, false, true);
            if (Find(SimpleInterface, 2) is { } simple) return (simple, false, false);
            return null;
        }

        private static bool Flag(AttributeData attr, string name)
            => attr.NamedArguments.Any(a => a.Key == name && a.Value.Value is bool b && b);

        private static string Fqn(ITypeSymbol type)
            => type.WithNullableAnnotation(NullableAnnotation.None)
                   .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static bool IsFrameworkAssembly(string name)
            => name.StartsWith("System") || name.StartsWith("Microsoft") || name.StartsWith("netstandard")
               || name == "mscorlib" || name.StartsWith("Orleans") || name.StartsWith("MemoryPack")
               || name.StartsWith("MessagePack") || name.StartsWith("Newtonsoft") || name.StartsWith("xunit");

        private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                yield return type;
                foreach (var nested in NestedTypes(type))
                    yield return nested;
            }

            foreach (var child in ns.GetNamespaceMembers())
                foreach (var type in AllTypes(child))
                    yield return type;
        }

        private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in NestedTypes(nested))
                    yield return deeper;
            }
        }
    }
}
