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
