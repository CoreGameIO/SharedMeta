using System.Text;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Generators
{
    public enum PatchFieldKind
    {
        Terminal,
        Collection,
        SubWrappable
    }

    public class PatchFieldInfo
    {
        public string Name { get; set; } = "";
        public int FieldId { get; set; }
        public string TypeFullName { get; set; } = "";
        public PatchFieldKind Kind { get; set; }
        public bool IsNullable { get; set; }
        // Collection
        public string? CollectionWrapperType { get; set; }
        public string? CollectionBaseType { get; set; }
        public string? ElementTypeFullName { get; set; }
        public string? KeyTypeFullName { get; set; }
        public string? ValueTypeFullName { get; set; }
        // Sub-wrappable
        public string? SubTypeName { get; set; }
        public string? SubTypeFullName { get; set; }
        // Tracked (backing-field approach)
        public bool IsTracked { get; set; }
    }

    public class PatchTypeInfo
    {
        public string TypeName { get; set; } = "";
        public string TypeFullName { get; set; } = "";
        public string Namespace { get; set; } = "";
        public List<PatchFieldInfo> Fields { get; set; } = new();
    }

    public class PatchGenerationInfo
    {
        public PatchTypeInfo RootType { get; set; } = null!;
        public List<PatchTypeInfo> SubTypes { get; set; } = new();
    }

    /// <summary>
    /// Generates PatchWrapper and PatchApplier classes for ISharedState types.
    /// PatchWrapper tracks field-level changes via PatchNode tree.
    /// PatchApplier applies a PatchNode diff to state on the client.
    /// </summary>
    public static class StatePatchGenerator
    {
        // ============================
        // Analysis
        // ============================

        public static PatchGenerationInfo? Analyze(INamedTypeSymbol stateSymbol)
        {
            var visited = new HashSet<string>();
            var subTypes = new List<PatchTypeInfo>();

            var rootInfo = AnalyzeType(stateSymbol, subTypes, visited);
            if (rootInfo == null || rootInfo.Fields.Count == 0) return null;

            return new PatchGenerationInfo
            {
                RootType = rootInfo,
                SubTypes = subTypes
            };
        }

        private static PatchTypeInfo? AnalyzeType(
            INamedTypeSymbol typeSymbol, List<PatchTypeInfo> subTypes, HashSet<string> visited)
        {
            var fullName = typeSymbol.ToDisplayString();
            if (visited.Contains(fullName)) return null;
            visited.Add(fullName);

            var info = new PatchTypeInfo
            {
                TypeName = typeSymbol.Name,
                TypeFullName = fullName,
                Namespace = typeSymbol.ContainingNamespace.ToDisplayString()
            };

            foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.DeclaredAccessibility != Accessibility.Public) continue;
                if (member.IsStatic) continue;
                if (member.GetMethod == null || member.SetMethod == null) continue;

                var idAttr = FindIdAttribute(member);
                if (idAttr == null) continue;
                if (HasMemoryPackIgnore(member)) continue;

                var rawValue = idAttr.ConstructorArguments[0].Value!;
                var fieldId = rawValue is uint u ? (int)u : (int)rawValue;
                var fieldInfo = AnalyzeField(member, fieldId, subTypes, visited);
                info.Fields.Add(fieldInfo);
            }

            // Also check private fields with [Tracked] attribute — these have generated public properties
            foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.IsStatic) continue;
                if (member.DeclaredAccessibility != Accessibility.Private) continue;
                if (!member.GetAttributes().Any(a =>
                    a.AttributeClass?.Name == "TrackedAttribute" &&
                    a.AttributeClass.ContainingNamespace.ToDisplayString() == "SharedMeta.Core"))
                    continue;
                if (!member.Name.StartsWith("_") || member.Name.Length < 2) continue;

                var idAttr = FindIdAttribute(member);
                if (idAttr == null) continue;

                var rawValue = idAttr.ConstructorArguments[0].Value!;
                var fieldId = rawValue is uint u2 ? (int)u2 : (int)rawValue;

                // Derive generated property name: _health → Health
                var propName = char.ToUpperInvariant(member.Name[1]) + member.Name.Substring(2);

                info.Fields.Add(new PatchFieldInfo
                {
                    Name = propName,
                    FieldId = fieldId,
                    TypeFullName = member.Type.ToDisplayString(),
                    Kind = PatchFieldKind.Terminal,
                    IsTracked = true // marks for ChangeTracker integration in PatchApplier
                });
            }

            return info;
        }

        private static PatchFieldInfo AnalyzeField(
            IPropertySymbol prop, int fieldId, List<PatchTypeInfo> subTypes, HashSet<string> visited)
        {
            var propType = prop.Type;
            var field = new PatchFieldInfo
            {
                Name = prop.Name,
                FieldId = fieldId,
                TypeFullName = propType.ToDisplayString()
            };

            // Determine nullability and strip nullable wrapper
            var nonNullType = propType;
            if (propType is INamedTypeSymbol nullableVT &&
                nullableVT.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                field.IsNullable = true;
                nonNullType = nullableVT.TypeArguments[0];
            }
            else if (propType.NullableAnnotation == NullableAnnotation.Annotated)
            {
                field.IsNullable = true;
            }

            // Array (but not byte[])
            if (nonNullType is IArrayTypeSymbol arrayType)
            {
                if (arrayType.ElementType.SpecialType == SpecialType.System_Byte)
                {
                    field.Kind = PatchFieldKind.Terminal;
                    return field;
                }
                field.Kind = PatchFieldKind.Collection;
                field.CollectionWrapperType = "PatchableArray";
                field.CollectionBaseType = arrayType.ElementType.ToDisplayString() + "[]";
                field.ElementTypeFullName = arrayType.ElementType.ToDisplayString();
                return field;
            }

            if (nonNullType is INamedTypeSymbol named)
            {
                var origDef = named.OriginalDefinition.ToDisplayString();

                if (origDef == "System.Collections.Generic.List<T>")
                {
                    field.Kind = PatchFieldKind.Collection;
                    field.CollectionWrapperType = "PatchableList";
                    field.CollectionBaseType = $"System.Collections.Generic.List<{named.TypeArguments[0].ToDisplayString()}>";
                    field.ElementTypeFullName = named.TypeArguments[0].ToDisplayString();
                    return field;
                }

                if (origDef == "System.Collections.Generic.Dictionary<TKey, TValue>")
                {
                    field.Kind = PatchFieldKind.Collection;
                    field.CollectionWrapperType = "PatchableDictionary";
                    field.CollectionBaseType = $"System.Collections.Generic.Dictionary<{named.TypeArguments[0].ToDisplayString()}, {named.TypeArguments[1].ToDisplayString()}>";
                    field.KeyTypeFullName = named.TypeArguments[0].ToDisplayString();
                    field.ValueTypeFullName = named.TypeArguments[1].ToDisplayString();
                    return field;
                }

                if (origDef == "System.Collections.Generic.HashSet<T>")
                {
                    field.Kind = PatchFieldKind.Collection;
                    field.CollectionWrapperType = "PatchableHashSet";
                    field.CollectionBaseType = $"System.Collections.Generic.HashSet<{named.TypeArguments[0].ToDisplayString()}>";
                    field.ElementTypeFullName = named.TypeArguments[0].ToDisplayString();
                    return field;
                }

                // Value types → terminal
                if (named.IsValueType)
                {
                    field.Kind = PatchFieldKind.Terminal;
                    return field;
                }

                // String → terminal
                if (named.SpecialType == SpecialType.System_String)
                {
                    field.Kind = PatchFieldKind.Terminal;
                    return field;
                }

                // Check for [Id] properties → sub-wrappable
                if (HasIdProperties(named))
                {
                    field.Kind = PatchFieldKind.SubWrappable;
                    field.SubTypeName = named.Name;
                    field.SubTypeFullName = named.ToDisplayString();
                    // Recursively analyze sub-type
                    var subInfo = AnalyzeType(named, subTypes, visited);
                    if (subInfo != null && subInfo.Fields.Count > 0)
                        subTypes.Add(subInfo);
                    return field;
                }
            }

            field.Kind = PatchFieldKind.Terminal;
            return field;
        }

        /// <summary>
        /// Find a serialization ordinal attribute on a property.
        /// Checks (in order): Orleans [Id(n)], MessagePack [Key(n)], MemoryPack [MemoryPackOrder(n)].
        /// All use the same int constructor pattern for the ordinal.
        /// </summary>
        private static AttributeData? FindIdAttribute(ISymbol symbol)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                var name = attr.AttributeClass?.Name;
                var ns = attr.AttributeClass?.ContainingNamespace.ToDisplayString();

                // Orleans [Id(n)]
                if (name == "IdAttribute" && ns == "Orleans")
                    return attr;

                // MessagePack [Key(n)]
                if (name == "KeyAttribute" && ns == "MessagePack")
                    return attr;

                // MemoryPack [MemoryPackOrderAttribute(n)]
                if (name == "MemoryPackOrderAttribute" && ns == "MemoryPack")
                    return attr;
            }
            return null;
        }

        private static bool HasMemoryPackIgnore(IPropertySymbol prop)
        {
            return prop.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "MemoryPackIgnoreAttribute");
        }

        private static bool HasIdProperties(INamedTypeSymbol type)
        {
            return type.GetMembers().OfType<IPropertySymbol>().Any(p =>
                p.DeclaredAccessibility == Accessibility.Public &&
                !p.IsStatic &&
                p.GetMethod != null && p.SetMethod != null &&
                FindIdAttribute(p) != null &&
                !HasMemoryPackIgnore(p));
        }

        // ============================
        // Code Generation
        // ============================

        public static string? Generate(PatchGenerationInfo info)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#pragma warning disable CS8602 // Dereference of a possibly null reference");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Patch;");
            sb.AppendLine();

            // Add usings for sub-type namespaces if different from root
            var targetNs = info.RootType.Namespace;
            var extraNamespaces = info.SubTypes
                .Select(s => s.Namespace)
                .Where(ns => ns != targetNs)
                .Distinct()
                .OrderBy(ns => ns);
            foreach (var ns in extraNamespaces)
                sb.AppendLine($"using {ns};");

            sb.AppendLine($"namespace {targetNs}");
            sb.AppendLine("{");

            // Generate root wrapper (sub-wrappers are nested inside)
            GenerateWrapper(sb, info.RootType, info.SubTypes, "    ");

            sb.AppendLine();

            // Generate root applier (sub-appliers are private methods)
            GenerateApplier(sb, info.RootType, info.SubTypes, "    ");

            sb.AppendLine("}");
            return sb.ToString();
        }

        // ---- Wrapper Generation ----

        private static void GenerateWrapper(
            StringBuilder sb, PatchTypeInfo typeInfo, List<PatchTypeInfo> subTypes, string indent)
        {
            var wrapperName = typeInfo.TypeName + "PatchWrapper";
            var stateType = typeInfo.TypeFullName;
            var ii = indent + "    "; // inner indent

            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// Patch-tracking wrapper for {typeInfo.TypeName}.");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}public class {wrapperName} : SharedMeta.Core.Patch.IPatchWrapper");
            sb.AppendLine($"{indent}{{");

            // Fields
            sb.AppendLine($"{ii}private readonly {stateType} _state;");
            sb.AppendLine($"{ii}private readonly PatchNode? _node;");
            sb.AppendLine($"{ii}private readonly IMetaSerializer _serializer;");
            sb.AppendLine();

            // Constructor
            sb.AppendLine($"{ii}public {wrapperName}({stateType} state, PatchNode? node, IMetaSerializer serializer)");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    _state = state;");
            sb.AppendLine($"{ii}    _node = node;");
            sb.AppendLine($"{ii}    _serializer = serializer;");
            sb.AppendLine($"{ii}}}");
            sb.AppendLine();

            // Raw, IsTracking, SetDirty
            sb.AppendLine($"{ii}/// <summary>Access the underlying {typeInfo.TypeName} directly.</summary>");
            sb.AppendLine($"{ii}public {stateType} Raw => _state;");
            sb.AppendLine();
            sb.AppendLine($"{ii}/// <summary>True when patch tracking is active.</summary>");
            sb.AppendLine($"{ii}public bool IsTracking => _node != null;");
            sb.AppendLine();
            sb.AppendLine($"{ii}/// <summary>The underlying PatchNode tree (for CRC computation in deep desync detection).</summary>");
            sb.AppendLine($"{ii}public PatchNode? Node => _node;");
            sb.AppendLine();
            sb.AppendLine($"{ii}/// <summary>Mark the entire {typeInfo.TypeName} as dirty (full replacement).</summary>");
            sb.AppendLine($"{ii}public void SetDirty()");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    _node?.MarkTerminal(_serializer.Pack(_state));");
            sb.AppendLine($"{ii}}}");

            // Properties
            foreach (var field in typeInfo.Fields)
            {
                sb.AppendLine();
                switch (field.Kind)
                {
                    case PatchFieldKind.Terminal:
                        GenerateTerminalProp(sb, field, ii);
                        break;
                    case PatchFieldKind.Collection:
                        GenerateCollectionProp(sb, field, ii);
                        break;
                    case PatchFieldKind.SubWrappable:
                        GenerateSubWrappableProp(sb, field, ii);
                        break;
                }
            }

            // Nested sub-wrapper classes (deduplicated)
            var generated = new HashSet<string>();
            foreach (var field in typeInfo.Fields.Where(f => f.Kind == PatchFieldKind.SubWrappable))
            {
                if (field.SubTypeFullName == null || generated.Contains(field.SubTypeFullName))
                    continue;
                generated.Add(field.SubTypeFullName);

                var subInfo = subTypes.FirstOrDefault(s => s.TypeFullName == field.SubTypeFullName);
                if (subInfo != null)
                {
                    sb.AppendLine();
                    GenerateWrapper(sb, subInfo, subTypes, ii);
                }
            }

            sb.AppendLine($"{indent}}}");
        }

        private static void GenerateTerminalProp(StringBuilder sb, PatchFieldInfo field, string ii)
        {
            {
                sb.AppendLine($"{ii}/// <summary>[Id({field.FieldId})] {field.Name}</summary>");
                sb.AppendLine($"{ii}public {field.TypeFullName} {field.Name}");
                sb.AppendLine($"{ii}{{");
                sb.AppendLine($"{ii}    get => _state.{field.Name};");
                sb.AppendLine($"{ii}    set {{ _state.{field.Name} = value; _node?.MarkChildTerminal({field.FieldId}, _serializer.Pack(value)); }}");
                sb.AppendLine($"{ii}}}");
            }
        }

        private static void GenerateCollectionProp(StringBuilder sb, PatchFieldInfo field, string ii)
        {
            var fieldVar = "_" + char.ToLower(field.Name[0]) + field.Name.Substring(1);
            string patchableType;

            switch (field.CollectionWrapperType)
            {
                case "PatchableList":
                    patchableType = $"PatchableList<{field.ElementTypeFullName}>";
                    break;
                case "PatchableDictionary":
                    patchableType = $"PatchableDictionary<{field.KeyTypeFullName}, {field.ValueTypeFullName}>";
                    break;
                case "PatchableHashSet":
                    patchableType = $"PatchableHashSet<{field.ElementTypeFullName}>";
                    break;
                case "PatchableArray":
                    patchableType = $"PatchableArray<{field.ElementTypeFullName}>";
                    break;
                default:
                    return;
            }

            sb.AppendLine($"{ii}/// <summary>[Id({field.FieldId})] {field.Name} — auto-tracks mutations.</summary>");
            sb.AppendLine($"{ii}private {patchableType}? {fieldVar};");
            sb.AppendLine($"{ii}public {patchableType} {field.Name}");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    get => {fieldVar} ??= new {patchableType}(_state.{field.Name}, _node, {field.FieldId}, _serializer);");
            sb.AppendLine($"{ii}    set {{ _state.{field.Name} = value.Inner; _node?.MarkChildTerminal({field.FieldId}, _serializer.Pack(value.Inner)); {fieldVar} = null; }}");
            sb.AppendLine($"{ii}}}");
            sb.AppendLine();
            sb.AppendLine($"{ii}/// <summary>Replace the entire {field.Name} collection.</summary>");
            sb.AppendLine($"{ii}public void Set{field.Name}({field.CollectionBaseType} value)");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    _state.{field.Name} = value;");
            sb.AppendLine($"{ii}    _node?.MarkChildTerminal({field.FieldId}, _serializer.Pack(value));");
            sb.AppendLine($"{ii}    {fieldVar} = null;");
            sb.AppendLine($"{ii}}}");
        }

        private static void GenerateSubWrappableProp(StringBuilder sb, PatchFieldInfo field, string ii)
        {
            var subWrapper = field.SubTypeName + "PatchWrapper";
            var fieldVar = "_" + char.ToLower(field.Name[0]) + field.Name.Substring(1);

            sb.AppendLine($"{ii}/// <summary>[Id({field.FieldId})] {field.Name} — sub-wrapper for granular tracking.</summary>");
            sb.AppendLine($"{ii}private {subWrapper}? {fieldVar};");

            if (field.IsNullable)
            {
                sb.AppendLine($"{ii}public {subWrapper}? {field.Name}");
                sb.AppendLine($"{ii}{{");
                sb.AppendLine($"{ii}    get");
                sb.AppendLine($"{ii}    {{");
                sb.AppendLine($"{ii}        if (_state.{field.Name} == null) return null;");
                sb.AppendLine($"{ii}        return {fieldVar} ??= new {subWrapper}(");
                sb.AppendLine($"{ii}            _state.{field.Name}, _node?.GetOrCreateChild({field.FieldId}), _serializer);");
                sb.AppendLine($"{ii}    }}");
                sb.AppendLine($"{ii}}}");
            }
            else
            {
                sb.AppendLine($"{ii}public {subWrapper} {field.Name}");
                sb.AppendLine($"{ii}    => {fieldVar} ??= new {subWrapper}(");
                sb.AppendLine($"{ii}        _state.{field.Name}, _node?.GetOrCreateChild({field.FieldId}), _serializer);");
            }

            sb.AppendLine();
            sb.AppendLine($"{ii}/// <summary>Replace the entire {field.Name} object.</summary>");
            sb.AppendLine($"{ii}public void Set{field.Name}({field.TypeFullName} value)");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    _state.{field.Name} = value;");
            sb.AppendLine($"{ii}    _node?.MarkChildTerminal({field.FieldId}, _serializer.Pack(value));");
            sb.AppendLine($"{ii}    {fieldVar} = null;");
            sb.AppendLine($"{ii}}}");
        }

        private static bool HasAnyTrackedFields(PatchGenerationInfo info)
        {
            if (info.RootType.Fields.Any(f => f.IsTracked)) return true;
            return info.SubTypes.Any(st => st.Fields.Any(f => f.IsTracked));
        }

        private static int GetStableHash(string s)
        {
            unchecked
            {
                int hash = (int)2166136261;
                foreach (var c in s)
                    hash = (hash ^ c) * 16777619;
                return hash;
            }
        }

        private static string GetChangeValueExpr(string typeFullName, string varExpr)
        {
            switch (typeFullName)
            {
                case "int":
                case "System.Int32":
                case "long":
                case "System.Int64":
                case "float":
                case "System.Single":
                case "double":
                case "System.Double":
                case "bool":
                case "System.Boolean":
                case "string":
                case "System.String":
                case "string?":
                    return $"ChangeValue.From({varExpr})";
                default:
                    return $"ChangeValue.FromObject({varExpr})";
            }
        }

        // ---- Applier Generation ----

        private static void GenerateApplier(
            StringBuilder sb, PatchTypeInfo typeInfo, List<PatchTypeInfo> subTypes, string indent)
        {
            var applierName = typeInfo.TypeName + "PatchApplier";
            var stateType = typeInfo.TypeFullName;
            var ii = indent + "    ";

            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// Applies a PatchNode diff to {typeInfo.TypeName}.");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}public static class {applierName}");
            sb.AppendLine($"{indent}{{");

            // Main Apply method
            sb.AppendLine($"{ii}public static void Apply({stateType} state, PatchNode patch, IMetaSerializer serializer)");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    if (patch.Children == null) return;");

            sb.AppendLine($"{ii}    foreach (var child in patch.Children)");
            sb.AppendLine($"{ii}    {{");
            sb.AppendLine($"{ii}        switch (child.FieldId)");
            sb.AppendLine($"{ii}        {{");

            foreach (var field in typeInfo.Fields)
            {
                sb.AppendLine($"{ii}            case {field.FieldId}: // {field.Name}");

                if (field.IsTracked)
                {
                    // [Tracked] backing field — use generated property setter which handles change tracking
                    sb.AppendLine($"{ii}                if (child.IsTerminal)");
                    sb.AppendLine($"{ii}                    state.{field.Name} = serializer.Unpack<{field.TypeFullName}>(child.Value!);");
                }
                else if (field.Kind == PatchFieldKind.SubWrappable)
                {
                    sb.AppendLine($"{ii}                if (child.IsTerminal)");
                    sb.AppendLine($"{ii}                    state.{field.Name} = serializer.Unpack<{field.TypeFullName}>(child.Value!);");
                    if (field.IsNullable)
                        sb.AppendLine($"{ii}                else if (state.{field.Name} != null)");
                    else
                        sb.AppendLine($"{ii}                else");
                    sb.AppendLine($"{ii}                    Apply{field.SubTypeName}(state.{field.Name}!, child, serializer);");
                }
                else
                {
                    sb.AppendLine($"{ii}                if (child.IsTerminal)");
                    sb.AppendLine($"{ii}                    state.{field.Name} = serializer.Unpack<{field.TypeFullName}>(child.Value!);");
                }

                sb.AppendLine($"{ii}                break;");
            }

            sb.AppendLine($"{ii}        }}");
            sb.AppendLine($"{ii}    }}");
            sb.AppendLine($"{ii}}}");

            // Private methods for sub-type applying (deduplicated)
            var generated = new HashSet<string>();
            GenerateSubAppliers(sb, typeInfo, subTypes, generated, ii);

            sb.AppendLine($"{indent}}}");
        }

        private static void GenerateSubAppliers(
            StringBuilder sb, PatchTypeInfo typeInfo, List<PatchTypeInfo> subTypes,
            HashSet<string> generated, string ii)
        {
            foreach (var field in typeInfo.Fields.Where(f => f.Kind == PatchFieldKind.SubWrappable))
            {
                if (field.SubTypeFullName == null || generated.Contains(field.SubTypeFullName))
                    continue;
                generated.Add(field.SubTypeFullName);

                var subInfo = subTypes.FirstOrDefault(s => s.TypeFullName == field.SubTypeFullName);
                if (subInfo == null) continue;

                sb.AppendLine();
                sb.AppendLine($"{ii}private static void Apply{subInfo.TypeName}({subInfo.TypeFullName} state, PatchNode patch, IMetaSerializer serializer)");
                sb.AppendLine($"{ii}{{");
                sb.AppendLine($"{ii}    if (patch.Children == null) return;");
                sb.AppendLine($"{ii}    foreach (var child in patch.Children)");
                sb.AppendLine($"{ii}    {{");
                sb.AppendLine($"{ii}        switch (child.FieldId)");
                sb.AppendLine($"{ii}        {{");

                foreach (var subField in subInfo.Fields)
                {
                    sb.AppendLine($"{ii}            case {subField.FieldId}: // {subField.Name}");

                    if (subField.Kind == PatchFieldKind.SubWrappable)
                    {
                        sb.AppendLine($"{ii}                if (child.IsTerminal)");
                        sb.AppendLine($"{ii}                    state.{subField.Name} = serializer.Unpack<{subField.TypeFullName}>(child.Value!);");
                        if (subField.IsNullable)
                            sb.AppendLine($"{ii}                else if (state.{subField.Name} != null)");
                        else
                            sb.AppendLine($"{ii}                else");
                        sb.AppendLine($"{ii}                    Apply{subField.SubTypeName}(state.{subField.Name}!, child, serializer);");
                    }
                    else
                    {
                        sb.AppendLine($"{ii}                if (child.IsTerminal)");
                        sb.AppendLine($"{ii}                    state.{subField.Name} = serializer.Unpack<{subField.TypeFullName}>(child.Value!);");
                    }

                    sb.AppendLine($"{ii}                break;");
                }

                sb.AppendLine($"{ii}        }}");
                sb.AppendLine($"{ii}    }}");
                sb.AppendLine($"{ii}}}");

                // Recursively generate sub-appliers for this sub-type's sub-wrappable fields
                GenerateSubAppliers(sb, subInfo, subTypes, generated, ii);
            }
        }
    }
}
