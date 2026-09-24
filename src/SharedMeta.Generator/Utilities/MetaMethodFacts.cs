using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharedMeta.Generator.Utilities
{
    /// <summary>
    /// Facts about a <c>[MetaMethod]</c> declaration that more than one generator needs to agree on.
    /// </summary>
    /// <remarks>
    /// Client-callability is read from syntax by the dispatcher / api-client and from symbols by
    /// discovery, and each consumer used to re-implement the rule. They drifted: the implicit
    /// "<c>Mode = Notification</c> is server-only" clause existed only in the api-client copy, so a
    /// notification got no client method (looked safe) while the dispatcher emitted no
    /// <c>IsClientCall</c> gate and the server signature advertised it as client-callable — a forged
    /// packet carrying its id was dispatched. Keep every reading of this rule here.
    /// </remarks>
    public static class MetaMethodFacts
    {
        private const string MetaMethodAttributeName = "MetaMethod";
        private const string MetaMethodAttributeFullName = "SharedMeta.Core.MetaMethodAttribute";

        /// <summary>
        /// True when clients must not be able to originate this method: explicit
        /// <c>GenerateClientApi = false</c>, or a mode that is structurally server-only.
        /// </summary>
        public static bool IsClientApiSuppressed(MethodDeclarationSyntax method)
        {
            var metaMethod = method.AttributeLists.SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString().Contains(MetaMethodAttributeName));
            if (metaMethod?.ArgumentList == null) return false;

            foreach (var arg in metaMethod.ArgumentList.Arguments)
            {
                var name = arg.NameEquals?.Name.Identifier.Text;
                if (name == "GenerateClientApi"
                    && arg.Expression is LiteralExpressionSyntax lit
                    && lit.Token.Text == "false")
                {
                    return true;
                }

                if (name == "Mode"
                    && arg.Expression is MemberAccessExpressionSyntax modeAccess
                    && IsServerOnlyMode(modeAccess.Name.Identifier.Text))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Symbol-side counterpart of <see cref="IsClientApiSuppressed(MethodDeclarationSyntax)"/>.</summary>
        public static bool IsClientApiSuppressed(IMethodSymbol method)
        {
            var attr = method.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MetaMethodAttributeFullName);
            if (attr == null) return false;

            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "GenerateClientApi" && !named.Value.IsNull && named.Value.Value is false)
                    return true;

                // ExecutionMode arrives as its underlying int; compare against the enum member by
                // name through the type symbol so a reordered enum cannot silently change meaning.
                if (named.Key == "Mode" && !named.Value.IsNull && named.Value.Value is int modeValue)
                {
                    var modeName = EnumMemberName(named.Value.Type, modeValue);
                    if (modeName != null && IsServerOnlyMode(modeName)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Execution modes clients can never originate. <c>Notification</c> is entity → entity
        /// fire-and-forget; there is no client-side caller by construction.
        /// </summary>
        private static bool IsServerOnlyMode(string modeName) => modeName == "Notification";

        private const string RequirePermissionAttributeName = "RequirePermission";

        /// <summary>
        /// What <c>[RequirePermission]</c> asks of a method: the permission names, plus whether an
        /// argument could not be read at compile time.
        /// </summary>
        public readonly struct PermissionRequirement
        {
            /// <summary>Permission names, any one of which admits the call. Empty = no requirement.</summary>
            public string[] Names { get; }

            /// <summary>
            /// An argument was present that resolved to no compile-time string. The consumer must
            /// fail the build rather than emit no gate: silently dropping it would leave a method
            /// its author believes is gated open to every caller.
            /// </summary>
            public bool HasUnreadableArgument { get; }

            public PermissionRequirement(string[] names, bool hasUnreadableArgument)
            {
                Names = names;
                HasUnreadableArgument = hasUnreadableArgument;
            }

            /// <summary>True when a gate has to be emitted for this method.</summary>
            public bool IsGated => Names.Length > 0;
        }

        /// <summary>
        /// Reads <c>[RequirePermission]</c> for a method: its own attribute if present, otherwise the
        /// one on the type that declares it. A method's attribute replaces the type's rather than
        /// adding to it, so one method can be opened up or tightened without splitting the service.
        /// </summary>
        /// <remarks>
        /// Constants are resolved through the semantic model, so <c>[RequirePermission(Perms.Cheat)]</c>
        /// reads the same as a literal — a permission set is exactly the kind of thing a game keeps in
        /// one <c>const</c> rather than retyping.
        /// </remarks>
        public static PermissionRequirement ReadRequiredPermissions(MethodDeclarationSyntax method, Compilation? compilation)
        {
            var own = ReadRequirePermission(method.AttributeLists, compilation);
            if (own.HasValue) return own.Value;

            if (method.Parent is TypeDeclarationSyntax owner)
            {
                var inherited = ReadRequirePermission(owner.AttributeLists, compilation);
                if (inherited.HasValue) return inherited.Value;
            }

            return new PermissionRequirement(System.Array.Empty<string>(), false);
        }

        /// <summary>
        /// Symbol-side counterpart of <see cref="ReadRequiredPermissions(MethodDeclarationSyntax, Compilation?)"/>.
        /// Attribute arguments are constants by language rule and Roslyn has already folded them, so
        /// <see cref="PermissionRequirement.HasUnreadableArgument"/> never trips on this path.
        /// </summary>
        /// <param name="declaringService">
        /// Service interface to fall back to when the method is declared on the implementation
        /// instead of the interface (a contract inherited from a base interface). Its containing
        /// type is then the impl class, which carries no requirement of its own.
        /// </param>
        public static PermissionRequirement ReadRequiredPermissions(IMethodSymbol method, INamedTypeSymbol? declaringService = null)
        {
            var own = ReadRequirePermission(method.GetAttributes());
            if (own.HasValue) return own.Value;

            var owner = method.ContainingType;
            if (owner != null)
            {
                var inherited = ReadRequirePermission(owner.GetAttributes());
                if (inherited.HasValue) return inherited.Value;
            }

            if (declaringService != null && !SymbolEqualityComparer.Default.Equals(declaringService, owner))
            {
                var fromService = ReadRequirePermission(declaringService.GetAttributes());
                if (fromService.HasValue) return fromService.Value;
            }

            return new PermissionRequirement(System.Array.Empty<string>(), false);
        }

        private static PermissionRequirement? ReadRequirePermission(
            System.Collections.Immutable.ImmutableArray<AttributeData> attributes)
        {
            var attr = attributes.FirstOrDefault(a => a.AttributeClass?.Name == "RequirePermissionAttribute");
            if (attr == null) return null;

            var names = new System.Collections.Generic.List<string>();
            foreach (var arg in attr.ConstructorArguments)
            {
                // params string[] arrives as one array-typed argument.
                if (arg.Kind == TypedConstantKind.Array)
                {
                    foreach (var element in arg.Values)
                    {
                        if (element.Value is string s && s.Length > 0 && !names.Contains(s)) names.Add(s);
                    }
                }
                else if (arg.Value is string single && single.Length > 0 && !names.Contains(single))
                {
                    names.Add(single);
                }
            }

            return new PermissionRequirement(names.ToArray(), false);
        }

        private static PermissionRequirement? ReadRequirePermission(
            SyntaxList<AttributeListSyntax> attributeLists, Compilation? compilation)
        {
            var attr = attributeLists.SelectMany(a => a.Attributes)
                .FirstOrDefault(a => a.Name.ToString().Contains(RequirePermissionAttributeName));
            if (attr == null) return null;

            var names = new System.Collections.Generic.List<string>();
            bool unreadable = false;

            if (attr.ArgumentList != null)
            {
                foreach (var arg in attr.ArgumentList.Arguments)
                {
                    if (arg.NameEquals != null) continue;   // the attribute takes params only

                    var value = ResolveStringArgument(arg.Expression, compilation);
                    if (value == null) { unreadable = true; continue; }
                    if (value.Length == 0) continue;        // "" carries no requirement
                    if (!names.Contains(value)) names.Add(value);
                }
            }

            return new PermissionRequirement(names.ToArray(), unreadable);
        }

        private static string? ResolveStringArgument(ExpressionSyntax expression, Compilation? compilation)
        {
            if (expression is LiteralExpressionSyntax lit)
                return lit.Token.Value as string;

            if (compilation == null) return null;

            var tree = expression.SyntaxTree;
            if (!compilation.ContainsSyntaxTree(tree)) return null;

            var constant = compilation.GetSemanticModel(tree).GetConstantValue(expression);
            return constant.HasValue ? constant.Value as string : null;
        }

        /// <summary>
        /// Permission names declared by <c>[assembly: DeclaredPermissions(...)]</c> across the
        /// compilation, or <c>null</c> when nothing declares any — which turns validation off rather
        /// than failing every use.
        /// </summary>
        public static System.Collections.Generic.HashSet<string>? ReadDeclaredPermissions(Compilation? compilation)
        {
            if (compilation == null) return null;

            System.Collections.Generic.HashSet<string>? declared = null;
            foreach (var attr in compilation.Assembly.GetAttributes())
            {
                if (attr.AttributeClass?.Name != "DeclaredPermissionsAttribute") continue;
                declared ??= new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

                foreach (var arg in attr.ConstructorArguments)
                {
                    // params string[] arrives as one array-typed argument.
                    if (arg.Kind == TypedConstantKind.Array)
                    {
                        foreach (var element in arg.Values)
                        {
                            if (element.Value is string s && s.Length > 0) declared.Add(s);
                        }
                    }
                    else if (arg.Value is string single && single.Length > 0)
                    {
                        declared.Add(single);
                    }
                }
            }
            return declared;
        }

        private static string? EnumMemberName(ITypeSymbol? enumType, int value)
        {
            if (enumType == null) return null;
            foreach (var field in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.HasConstantValue && field.ConstantValue is int fieldValue && fieldValue == value)
                    return field.Name;
            }
            return null;
        }
    }
}
