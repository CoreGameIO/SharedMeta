using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Information about an <c>IMetaResultComparer&lt;T&gt;</c> implementation discovered
    /// in the compilation. Multiple records for the same target type indicate ambiguity
    /// — the consumer (<see cref="SimplifiedApiClientGenerator"/>) emits a build-time
    /// <c>#error</c> in that case unless one comparer wins by <see cref="Priority"/>.
    /// </summary>
    public class ResultComparerInfo
    {
        /// <summary>Display name of the type the comparer compares (e.g. <c>"MyGame.PendingGrants"</c>).</summary>
        public string TargetTypeFullName { get; set; } = "";

        /// <summary>Display name of the comparer class (e.g. <c>"MyGame.PendingGrantsComparer"</c>).</summary>
        public string ComparerFullName { get; set; } = "";

        /// <summary>From <c>[ResultComparer(Priority = N)]</c>; default 0.</summary>
        public int Priority { get; set; }
    }

    /// <summary>
    /// Scans a Roslyn <see cref="Compilation"/> for classes implementing
    /// <c>SharedMeta.Core.Diagnostics.IMetaResultComparer&lt;T&gt;</c> and returns a
    /// type-name → comparer lookup. Mirrors the discovery pattern used for transformers
    /// (<see cref="TransformerRegistrationGenerator.Analyze"/>) — marker-interface +
    /// optional <c>[ResultComparer]</c> attribute for opt-out / priority.
    /// </summary>
    public static class ResultComparerScanner
    {
        private const string InterfacePrefix = "SharedMeta.Core.Diagnostics.IMetaResultComparer<";
        private const string AttributeFullName = "SharedMeta.Core.Diagnostics.ResultComparerAttribute";

        /// <summary>
        /// Build a map: <c>TargetType.ToDisplayString()</c> → list of comparer candidates.
        /// One entry = single winner; multiple = ambiguity for the consumer to handle.
        /// Only public, non-abstract classes with a parameterless constructor are picked up.
        /// </summary>
        public static Dictionary<string, List<ResultComparerInfo>> Scan(Compilation compilation)
        {
            var result = new Dictionary<string, List<ResultComparerInfo>>();
            ScanNamespace(compilation.Assembly.GlobalNamespace, result);

            foreach (var reference in compilation.References)
            {
                var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                if (assemblySymbol == null) continue;

                var name = assemblySymbol.Name;
                if (name.StartsWith("System") || name.StartsWith("Microsoft") ||
                    name.StartsWith("netstandard") || name == "mscorlib" ||
                    name.StartsWith("Orleans") || name == "MemoryPack" ||
                    name == "MemoryPack.Core" || name == "MessagePack" ||
                    name == "MessagePack.Annotations")
                    continue;

                ScanNamespace(assemblySymbol.GlobalNamespace, result);
            }

            return result;
        }

        private static void ScanNamespace(INamespaceSymbol ns, Dictionary<string, List<ResultComparerInfo>> sink)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                Visit(type, sink);
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                ScanNamespace(childNs, sink);
            }
        }

        private static void Visit(INamedTypeSymbol type, Dictionary<string, List<ResultComparerInfo>> sink)
        {
            var info = Analyze(type);
            if (info != null)
            {
                if (!sink.TryGetValue(info.TargetTypeFullName, out var list))
                {
                    list = new List<ResultComparerInfo>();
                    sink[info.TargetTypeFullName] = list;
                }
                list.Add(info);
            }

            foreach (var nested in type.GetTypeMembers())
            {
                Visit(nested, sink);
            }
        }

        /// <summary>
        /// Returns a <see cref="ResultComparerInfo"/> if <paramref name="type"/> is a
        /// usable comparer, or <c>null</c> if it's abstract, generic, lacks a
        /// parameterless constructor, opted out via <c>[ResultComparer(NoAutoRegister=true)]</c>,
        /// or doesn't implement the marker interface.
        /// </summary>
        public static ResultComparerInfo? Analyze(INamedTypeSymbol type)
        {
            if (type.IsAbstract || type.IsGenericType || type.TypeKind != TypeKind.Class)
                return null;

            // Must be public so generated code in another namespace can `new` it.
            if (type.DeclaredAccessibility != Accessibility.Public)
                return null;

            var iface = type.AllInterfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.ConstructedFrom.ToDisplayString().StartsWith(InterfacePrefix));
            if (iface == null) return null;

            // Parameterless ctor required — generator emits `new TComparer()`.
            var hasParameterlessCtor = type.InstanceConstructors.Any(c =>
                c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
            if (!hasParameterlessCtor) return null;

            // Optional [ResultComparer] attribute
            var attr = type.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == AttributeFullName);
            int priority = 0;
            if (attr != null)
            {
                var noAuto = attr.NamedArguments.FirstOrDefault(a => a.Key == "NoAutoRegister");
                if (noAuto.Value.Value is true)
                    return null;

                var prio = attr.NamedArguments.FirstOrDefault(a => a.Key == "Priority");
                if (prio.Value.Value is int p)
                    priority = p;
            }

            return new ResultComparerInfo
            {
                TargetTypeFullName = iface.TypeArguments[0].ToDisplayString(),
                ComparerFullName = type.ToDisplayString(),
                Priority = priority
            };
        }

        /// <summary>
        /// Resolves ambiguity: if <paramref name="candidates"/> has a single highest-priority
        /// entry, returns it. If two or more share the top priority, returns <c>null</c>
        /// (ambiguous — consumer should emit a <c>#error</c> with the candidate list).
        /// </summary>
        public static ResultComparerInfo? ResolveWinner(List<ResultComparerInfo> candidates)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            var maxPriority = candidates.Max(c => c.Priority);
            var winners = candidates.Where(c => c.Priority == maxPriority).ToList();
            return winners.Count == 1 ? winners[0] : null;
        }
    }
}
