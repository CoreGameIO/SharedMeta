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

        /// <summary>Fully-qualified state type for <c>IStateArgumentTransformer</c>, else null.</summary>
        public string? StateType { get; set; }

        public bool Transformed => TransformerType != null;

        /// <summary>The type that actually goes on the wire for this parameter.</summary>
        public string WireType => SimpleType ?? DeclaredType;

        /// <summary>Name of the generated local holding the wire-shaped value.</summary>
        public string WireLocal => "__wire_" + Name;
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

        private sealed class CatalogEntry
        {
            public string TransformerType = "";
            public string SimpleType = "";
            public string? StateType;
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
        public static string BoxExpr(ParameterTransform t, string valueExpr, string stateExpr)
            => t.StateType != null
                ? $"global::SharedMeta.Core.MetaTransformer<{t.TransformerType}>.Instance.Box({valueExpr}, {stateExpr})"
                : $"global::SharedMeta.Core.MetaTransformer<{t.TransformerType}>.Instance.Box({valueExpr})";

        /// <summary>Expression producing the method-shaped value from a boxed one.</summary>
        public static string UnboxExpr(ParameterTransform t, string valueExpr, string stateExpr)
            => t.StateType != null
                ? $"global::SharedMeta.Core.MetaTransformer<{t.TransformerType}>.Instance.Unbox({valueExpr}, {stateExpr})"
                : $"global::SharedMeta.Core.MetaTransformer<{t.TransformerType}>.Instance.Unbox({valueExpr})";

        /// <summary>
        /// State expression for code that runs with a <c>MetaContext</c> in scope (dispatchers).
        /// </summary>
        public static string ContextStateExpr(ParameterTransform t, string contextExpr)
            => $"(({t.StateType}){contextExpr}.StateObject)";

        /// <summary>
        /// State expression for a sender that owns no state of its own — server-originated calls
        /// (admin APIs, cross-entity hops). A state-aware transformer needs the state its Box was
        /// written against; the ambient meta call is the only candidate, and if it is the wrong
        /// state type there is no sensible fallback, so fail with a message that names the cause.
        /// </summary>
        public static string AmbientStateExpr(ParameterTransform t)
            => $"(global::SharedMeta.Core.MetaContextAccessor.Current?.StateObject as {t.StateType}"
               + $" ?? throw new System.InvalidOperationException("
               + $"\"Transformer {t.TransformerType} needs a {t.StateType} context, which this call path does not have.\"))";

        private static void Apply(ParameterTransform entry, CatalogEntry? found)
        {
            if (found == null) return;
            entry.TransformerType = found.TransformerType;
            entry.SimpleType = found.SimpleType;
            entry.StateType = found.StateType;
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

            var stateIface = type.AllInterfaces.FirstOrDefault(i =>
                i.ConstructedFrom.ToDisplayString().StartsWith(StateInterface));
            if (stateIface != null && stateIface.TypeArguments.Length >= 3)
            {
                return new CatalogEntry
                {
                    TransformerType = Fqn(type),
                    SimpleType = Fqn(stateIface.TypeArguments[1]),
                    StateType = Fqn(stateIface.TypeArguments[2]),
                };
            }

            var simpleIface = type.AllInterfaces.FirstOrDefault(i =>
                i.ConstructedFrom.ToDisplayString().StartsWith(SimpleInterface));
            if (simpleIface != null && simpleIface.TypeArguments.Length >= 2)
            {
                return new CatalogEntry
                {
                    TransformerType = Fqn(type),
                    SimpleType = Fqn(simpleIface.TypeArguments[1]),
                };
            }

            return null;
        }

        private static string? ComplexTypeOf(INamedTypeSymbol type)
        {
            var stateIface = type.AllInterfaces.FirstOrDefault(i =>
                i.ConstructedFrom.ToDisplayString().StartsWith(StateInterface));
            if (stateIface != null && stateIface.TypeArguments.Length >= 3)
                return Fqn(stateIface.TypeArguments[0]);

            var simpleIface = type.AllInterfaces.FirstOrDefault(i =>
                i.ConstructedFrom.ToDisplayString().StartsWith(SimpleInterface));
            if (simpleIface != null && simpleIface.TypeArguments.Length >= 2)
                return Fqn(simpleIface.TypeArguments[0]);

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
