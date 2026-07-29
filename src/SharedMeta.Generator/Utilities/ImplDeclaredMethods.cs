using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharedMeta.Generator.Utilities
{
    /// <summary>
    /// A meta method a service declares on its <c>[MetaServiceImpl]</c> class instead of on its
    /// <c>[MetaService]</c> interface. Carries both forms because the generator families need
    /// different ones: dispatcher / api-client read syntax, discovery / signature read symbols.
    /// </summary>
    public sealed class ImplDeclaredMethod
    {
        public ImplDeclaredMethod(MethodDeclarationSyntax? syntax, IMethodSymbol symbol)
        {
            Syntax = syntax;
            Symbol = symbol;
        }

        /// <summary>Null when the impl arrives as metadata (server project referencing shared).</summary>
        public MethodDeclarationSyntax? Syntax { get; }

        public IMethodSymbol Symbol { get; }
    }

    /// <summary>
    /// Finds meta methods a service declares on its implementation class rather than on its
    /// interface. Used when the contract is inherited from a base interface the game does not own.
    /// </summary>
    /// <remarks>
    /// A framework contract (e.g. <c>ILobbyRequester</c>) ships in the package, so its members live
    /// in a referenced assembly and have no syntax in the game's compilation. Method ids, dispatcher
    /// cases and client broadcast replay are all driven from <c>MethodDeclarationSyntax</c>, so an
    /// interface that merely inherits the contract would generate nothing at all.
    /// <para>
    /// The implementing class does have syntax. Marking its implementation with <c>[MetaMethod]</c>
    /// pulls the method into the service's surface with the same machinery as an interface-declared
    /// one: every consumer reads only attributes, identifier, parameters and return type, all of
    /// which a class method declaration carries identically. Generated code still calls through the
    /// interface-typed variable — the C# compiler resolves the inherited member.
    /// </para>
    /// <para>
    /// The base interface supplies the signature guarantee: the class must implement the inherited
    /// member, so an argument-shape drift is a compile error rather than a silent protocol break.
    /// </para>
    /// <para>
    /// MUST stay the single rule for "is this method part of the service surface". Method ids are
    /// assigned by one generator and switched on by others; if two of them disagreed about which
    /// methods exist, the tables would silently misalign at runtime rather than fail to build.
    /// </para>
    /// </remarks>
    public static class ImplDeclaredMethods
    {
        private const string MetaServiceImplAttribute = "SharedMeta.Core.MetaServiceImplAttribute";
        private const string MetaMethodAttributeFullName = "SharedMeta.Core.MetaMethodAttribute";

        /// <summary>
        /// <c>[MetaMethod]</c>-annotated methods on classes whose <c>[MetaServiceImpl]</c> binds
        /// them to <paramref name="serviceInterface"/>, excluding any method already declared on the
        /// interface itself — the interface declaration wins, since that is the source the service
        /// surface is normally built from.
        /// </summary>
        public static List<ImplDeclaredMethod> ForService(INamedTypeSymbol serviceInterface, Compilation compilation)
        {
            var result = new List<ImplDeclaredMethod>();

            // Direct interface declarations already flow through the normal path; re-adding them
            // from the impl would emit a duplicate case label for the same method id.
            var seen = new HashSet<string>(
                serviceInterface.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary)
                    .Select(Key));

            var serviceFqn = serviceInterface.ToDisplayString();

            // Impls live alongside the interface they implement. Scanning the declaring assembly
            // (rather than only the current one) is what makes this work in a server project, where
            // both the service and its impl arrive as metadata from the referenced shared assembly.
            foreach (var assembly in ScopesFor(serviceInterface, compilation))
            {
                if (!IndexFor(assembly).TryGetValue(serviceFqn, out var candidates)) continue;

                foreach (var method in candidates)
                {
                    if (!seen.Add(Key(method))) continue;

                    // Null in a server project: metadata symbols carry no syntax. Only the
                    // syntax-driven consumers need it, and those all run where the impl is source.
                    var syntax = method.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax())
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault();

                    result.Add(new ImplDeclaredMethod(syntax, method));
                }
            }

            return result;
        }

        /// <summary>
        /// Impl-declared meta methods of one assembly, grouped by the service interface their
        /// <c>[MetaServiceImpl]</c> names. Built once per assembly and reused by every service.
        /// </summary>
        /// <remarks>
        /// Keyed on the assembly symbol rather than the service: the walk is over a whole global
        /// namespace, so doing it per service made it O(services × types). Symbol instances are
        /// stable within a compilation and unreachable once it is collected, so a weak table both
        /// gives the reuse and avoids pinning symbols from stale compilations. The factory is pure,
        /// so a concurrent double-build is wasteful but harmless.
        /// </remarks>
        private static readonly ConditionalWeakTable<IAssemblySymbol, Dictionary<string, List<IMethodSymbol>>> IndexCache = new();

        private static Dictionary<string, List<IMethodSymbol>> IndexFor(IAssemblySymbol assembly) =>
            IndexCache.GetValue(assembly, BuildIndex);

        private static Dictionary<string, List<IMethodSymbol>> BuildIndex(IAssemblySymbol assembly)
        {
            var index = new Dictionary<string, List<IMethodSymbol>>();

            foreach (var implType in TypesWithAttribute(assembly.GlobalNamespace, MetaServiceImplAttribute))
            {
                var methods = implType.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary
                             && m.DeclaredAccessibility == Accessibility.Public
                             && HasMetaMethodAttribute(m))
                    .ToList();
                if (methods.Count == 0) continue;

                // One class may carry several [MetaServiceImpl] attributes; the methods belong to
                // whichever services it declares.
                foreach (var serviceFqn in DeclaredServices(implType))
                {
                    if (!index.TryGetValue(serviceFqn, out var bucket))
                        index[serviceFqn] = bucket = new List<IMethodSymbol>();
                    bucket.AddRange(methods);
                }
            }

            return index;
        }

        private static IEnumerable<string> DeclaredServices(INamedTypeSymbol implType)
        {
            foreach (var attr in implType.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() != MetaServiceImplAttribute) continue;
                if (attr.ConstructorArguments.Length == 0) continue;
                if (attr.ConstructorArguments[0].Value is INamedTypeSymbol declared)
                    yield return declared.ToDisplayString();
            }
        }

        /// <summary>Syntax-only view, for generators driven by the interface declaration node.</summary>
        public static List<MethodDeclarationSyntax> SyntaxForService(INamedTypeSymbol serviceInterface, Compilation compilation) =>
            ForService(serviceInterface, compilation)
                .Where(m => m.Syntax != null)
                .Select(m => m.Syntax!)
                .ToList();

        /// <summary>Symbol-only view, for generators driven by the interface symbol.</summary>
        public static List<IMethodSymbol> SymbolsForService(INamedTypeSymbol serviceInterface, Compilation compilation) =>
            ForService(serviceInterface, compilation).Select(m => m.Symbol).ToList();

        /// <summary>
        /// Assemblies that may declare an impl for this service: the one being compiled, and the
        /// one declaring the interface when the service arrives as a reference.
        /// </summary>
        private static IEnumerable<IAssemblySymbol> ScopesFor(INamedTypeSymbol serviceInterface, Compilation compilation)
        {
            yield return compilation.Assembly;

            var declaring = serviceInterface.ContainingAssembly;
            if (declaring != null && !SymbolEqualityComparer.Default.Equals(declaring, compilation.Assembly))
                yield return declaring;
        }

        private static bool HasMetaMethodAttribute(IMethodSymbol method) =>
            method.GetAttributes()
                .Any(a => a.AttributeClass?.ToDisplayString() == MetaMethodAttributeFullName);

        /// <summary>
        /// Identity for dedup: name + parameter count. Interface and impl declarations of the same
        /// member always agree on both.
        /// </summary>
        private static string Key(IMethodSymbol method) => method.Name + "/" + method.Parameters.Length;

        private static IEnumerable<INamedTypeSymbol> TypesWithAttribute(INamespaceSymbol ns, string attributeFullName)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                if (type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeFullName))
                    yield return type;

                foreach (var nested in NestedTypesWithAttribute(type, attributeFullName))
                    yield return nested;
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                foreach (var type in TypesWithAttribute(childNs, attributeFullName))
                    yield return type;
            }
        }

        private static IEnumerable<INamedTypeSymbol> NestedTypesWithAttribute(INamedTypeSymbol type, string attributeFullName)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                if (nested.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeFullName))
                    yield return nested;

                foreach (var deeper in NestedTypesWithAttribute(nested, attributeFullName))
                    yield return deeper;
            }
        }
    }
}
