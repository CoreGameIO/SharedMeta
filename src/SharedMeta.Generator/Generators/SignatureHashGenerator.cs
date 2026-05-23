using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Utility for generating method signature hashes.
    /// Used to validate that client and server have compatible method signatures.
    /// </summary>
    public static class SignatureHashGenerator
    {
        /// <summary>
        /// Compute a signature hash for a method.
        /// Format: {ServiceName}.{MethodAlias}({ParamType1},{ParamType2},...)->{ReturnType}
        /// </summary>
        public static ulong ComputeMethodHash(string serviceName, string methodAlias, IMethodSymbol method)
        {
            var signature = BuildSignatureString(serviceName, methodAlias, method);
            return ComputeFnv1aHash(signature);
        }

        /// <summary>
        /// Build canonical signature string for a method.
        /// </summary>
        public static string BuildSignatureString(string serviceName, string methodAlias, IMethodSymbol method)
        {
            var sb = new StringBuilder();
            sb.Append(serviceName);
            sb.Append('.');
            sb.Append(methodAlias);
            sb.Append('(');

            var paramTypes = method.Parameters.Select(p => GetCanonicalTypeName(p.Type));
            sb.Append(string.Join(",", paramTypes));

            sb.Append(")->");
            sb.Append(GetCanonicalTypeName(method.ReturnType));

            return sb.ToString();
        }

        /// <summary>
        /// Get canonical type name for hashing.
        /// Simplifies generic types and uses short names for common types.
        /// </summary>
        public static string GetCanonicalTypeName(ITypeSymbol type)
        {
            // Handle Task<T> -> extract inner type with "Task<>" wrapper
            if (type is INamedTypeSymbol namedType)
            {
                var fullName = namedType.ToDisplayString();

                // Handle Task<T>
                if (fullName.StartsWith("System.Threading.Tasks.Task<") && namedType.TypeArguments.Length == 1)
                {
                    var innerType = GetCanonicalTypeName(namedType.TypeArguments[0]);
                    return $"Task<{innerType}>";
                }

                // Handle Task (non-generic)
                if (fullName == "System.Threading.Tasks.Task")
                {
                    return "Task";
                }

                // Handle nullable reference types - strip the ?
                if (namedType.NullableAnnotation == NullableAnnotation.Annotated)
                {
                    var nonNullable = namedType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                    return GetCanonicalTypeName(nonNullable) + "?";
                }

                // Handle generic types
                if (namedType.IsGenericType && namedType.TypeArguments.Length > 0)
                {
                    var baseName = namedType.ConstructedFrom.ToDisplayString();
                    var genericIdx = baseName.IndexOf('<');
                    if (genericIdx > 0)
                        baseName = baseName.Substring(0, genericIdx);

                    var args = namedType.TypeArguments.Select(GetCanonicalTypeName);
                    return $"{baseName}<{string.Join(",", args)}>";
                }

                // Handle arrays
                if (namedType.TypeKind == TypeKind.Array)
                {
                    var arrayType = (IArrayTypeSymbol)type;
                    return GetCanonicalTypeName(arrayType.ElementType) + "[]";
                }
            }

            // Handle array types
            if (type is IArrayTypeSymbol arraySymbol)
            {
                return GetCanonicalTypeName(arraySymbol.ElementType) + "[]";
            }

            // Use simplified names for common types
            var displayName = type.ToDisplayString();
            return SimplifyTypeName(displayName);
        }

        /// <summary>
        /// Simplify common type names for consistency.
        /// </summary>
        private static string SimplifyTypeName(string typeName)
        {
            return typeName switch
            {
                "System.String" => "string",
                "System.Int32" => "int",
                "System.Int64" => "long",
                "System.Int16" => "short",
                "System.Byte" => "byte",
                "System.Boolean" => "bool",
                "System.Single" => "float",
                "System.Double" => "double",
                "System.Decimal" => "decimal",
                "System.Char" => "char",
                "System.Void" => "void",
                "System.Object" => "object",
                _ => typeName
            };
        }

        /// <summary>
        /// Compute FNV-1a 64-bit hash.
        /// Fast, non-cryptographic hash with good distribution.
        /// </summary>
        public static ulong ComputeFnv1aHash(string input)
        {
            const ulong FnvPrime = 0x00000100000001B3;
            const ulong FnvOffsetBasis = 0xcbf29ce484222325;

            var hash = FnvOffsetBasis;
            foreach (var c in input)
            {
                hash ^= c;
                hash *= FnvPrime;
            }
            return hash;
        }

        /// <summary>
        /// Format hash as hex string for generated code.
        /// </summary>
        public static string FormatHashLiteral(ulong hash)
        {
            return $"0x{hash:X16}UL";
        }

        /// <summary>
        /// Build a valid C# identifier from a <c>(ServiceName, MethodAlias, Version)</c> tuple.
        /// Used as the const name in <c>GameMethodIds</c> emitted by
        /// <c>GameServiceDiscoveryGenerator</c> and consumed by client / dispatcher generators.
        /// Replaces any non-identifier characters with '_' so service / alias names that
        /// contain dots or other punctuation still produce valid C#.
        /// </summary>
        public static string MakeMethodIdConstName(string serviceName, string methodAlias, int version)
        {
            var sb = new System.Text.StringBuilder(serviceName.Length + methodAlias.Length + 8);
            foreach (var ch in serviceName) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            sb.Append('_');
            foreach (var ch in methodAlias) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            sb.Append("_v").Append(version);
            return sb.ToString();
        }
    }
}
