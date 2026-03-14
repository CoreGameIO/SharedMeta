using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Info about a single [Tracked] private backing field.
    /// </summary>
    public class TrackedFieldInfo
    {
        /// <summary>Private field name (e.g. "_health").</summary>
        public string FieldName { get; set; } = "";
        /// <summary>Generated public property name (e.g. "Health").</summary>
        public string PropertyName { get; set; } = "";
        /// <summary>Field type (e.g. "int", "string").</summary>
        public string TypeFullName { get; set; } = "";
        /// <summary>Serialization key from [Key(n)] or [MemoryPackOrder(n)].</summary>
        public int SerializationKey { get; set; }
    }

    /// <summary>
    /// Info about a class containing [Tracked] fields.
    /// </summary>
    public class TrackedTypeInfo
    {
        public string TypeName { get; set; } = "";
        public string TypeFullName { get; set; } = "";
        public string Namespace { get; set; } = "";
        public List<TrackedFieldInfo> Fields { get; set; } = new();
        /// <summary>Base ID for this type's fields in the unified TrackingProperty enum.</summary>
        public int FieldIdBase { get; set; }
    }

    /// <summary>
    /// Generates push-based tracked properties for private fields marked with [Tracked].
    /// For each [Tracked] private field, produces:
    /// - A public property with a tracking setter (records changes to ChangeTracker)
    /// - Entry in the unified TrackingProperty enum
    /// - Static subscription class per type
    /// </summary>
    public static class TrackedStateGenerator
    {
        private const int FieldsPerType = 100;

        // ============================
        // Analysis
        // ============================

        /// <summary>
        /// Analyze a class for [Tracked] private fields.
        /// </summary>
        public static TrackedTypeInfo? AnalyzeSingle(INamedTypeSymbol typeSymbol)
        {
            var info = new TrackedTypeInfo
            {
                TypeName = typeSymbol.Name,
                TypeFullName = typeSymbol.ToDisplayString(),
                Namespace = typeSymbol.ContainingNamespace.ToDisplayString()
            };

            foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.IsStatic) continue;
                if (!HasTrackedAttribute(member)) continue;

                // Validate: must be private
                if (member.DeclaredAccessibility != Accessibility.Private)
                    continue;

                // Validate: must start with underscore
                if (!member.Name.StartsWith("_") || member.Name.Length < 2)
                    continue;

                // Get serialization key from [Key(n)] or [MemoryPackOrder(n)]
                int? serKey = GetSerializationKey(member);
                if (serKey == null) continue;

                // Derive property name: _health → Health
                var propName = char.ToUpperInvariant(member.Name[1]) + member.Name.Substring(2);

                info.Fields.Add(new TrackedFieldInfo
                {
                    FieldName = member.Name,
                    PropertyName = propName,
                    TypeFullName = member.Type.ToDisplayString(),
                    SerializationKey = serKey.Value
                });
            }

            return info.Fields.Count > 0 ? info : null;
        }

        /// <summary>
        /// Collect all types from the pipeline, deduplicate, assign field IDs.
        /// </summary>
        public static List<TrackedTypeInfo> CollectAllTypes(ImmutableArray<TrackedTypeInfo?> roots)
        {
            var allTypes = new Dictionary<string, TrackedTypeInfo>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                if (!allTypes.ContainsKey(root.TypeFullName))
                    allTypes[root.TypeFullName] = root;
            }

            var sorted = allTypes.Values.OrderBy(t => t.TypeFullName).ToList();
            for (int i = 0; i < sorted.Count; i++)
                sorted[i].FieldIdBase = i * FieldsPerType;

            return sorted;
        }

        private static bool HasTrackedAttribute(IFieldSymbol field)
        {
            return field.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "TrackedAttribute" &&
                a.AttributeClass.ContainingNamespace.ToDisplayString() == "SharedMeta.Core");
        }

        private static int? GetSerializationKey(IFieldSymbol field)
        {
            foreach (var attr in field.GetAttributes())
            {
                var name = attr.AttributeClass?.Name;
                var ns = attr.AttributeClass?.ContainingNamespace.ToDisplayString();

                // [Key(n)] from MessagePack
                if (name == "KeyAttribute" && ns == "MessagePack" &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is int keyVal)
                    return keyVal;

                // [MemoryPackOrder(n)]
                if (name == "MemoryPackOrderAttribute" && ns == "MemoryPack" &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is int orderVal)
                    return orderVal;

                // [Id(n)] from Orleans
                if (name == "IdAttribute" && ns == "Orleans" &&
                    attr.ConstructorArguments.Length > 0 &&
                    attr.ConstructorArguments[0].Value is int idVal)
                    return idVal;
            }
            return null;
        }

        // ============================
        // Code Generation
        // ============================

        public static string? Generate(List<TrackedTypeInfo> allTypes)
        {
            if (allTypes.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using SharedMeta.Core.Reactive;");
            sb.AppendLine();

            // Collect all unique namespaces
            var namespaces = allTypes.Select(t => t.Namespace).Distinct().OrderBy(n => n).ToList();
            var targetNs = allTypes[0].Namespace;
            foreach (var ns in namespaces)
            {
                if (ns != targetNs)
                    sb.AppendLine($"using {ns};");
            }

            sb.AppendLine($"namespace {targetNs}");
            sb.AppendLine("{");

            // 1. Unified TrackingProperty enum
            GenerateTrackingPropertyEnum(sb, allTypes, "    ");
            sb.AppendLine();

            // 2. Partial class with generated properties per type
            foreach (var type in allTypes)
            {
                GeneratePartialClass(sb, type, "    ");
                sb.AppendLine();
            }

            // 3. Static subscription classes
            foreach (var type in allTypes)
            {
                GenerateSubscriptionClass(sb, type, "    ");
                sb.AppendLine();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void GenerateTrackingPropertyEnum(StringBuilder sb, List<TrackedTypeInfo> allTypes, string indent)
        {
            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// Unified tracking property IDs for all [Tracked] fields.");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}public enum TrackingProperty");
            sb.AppendLine($"{indent}{{");

            foreach (var type in allTypes)
            {
                sb.AppendLine($"{indent}    // {type.TypeName} fields (base: {type.FieldIdBase})");
                for (int i = 0; i < type.Fields.Count; i++)
                {
                    var field = type.Fields[i];
                    var enumName = $"{type.TypeName}_{field.PropertyName}";
                    var value = type.FieldIdBase + i;
                    sb.AppendLine($"{indent}    {enumName} = {value},");
                }
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}}}");
        }

        private static string GetChangeValueFactory(string typeFullName)
        {
            return typeFullName switch
            {
                "int" or "System.Int32" => "ChangeValue.From",
                "long" or "System.Int64" => "ChangeValue.From",
                "float" or "System.Single" => "ChangeValue.From",
                "double" or "System.Double" => "ChangeValue.From",
                "bool" or "System.Boolean" => "ChangeValue.From",
                "string" or "System.String" => "ChangeValue.From",
                _ => "ChangeValue.FromObject"
            };
        }

        private static void GeneratePartialClass(StringBuilder sb, TrackedTypeInfo type, string indent)
        {
            var ii = indent + "    ";
            var iii = ii + "    ";

            sb.AppendLine($"{indent}public partial class {type.TypeName}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{ii}/// <summary>Tracked type ID for ChangeTracker subscriptions.</summary>");
            sb.AppendLine($"{ii}public const int TrackedTypeId = {type.FieldIdBase};");
            sb.AppendLine();

            foreach (var field in type.Fields)
            {
                var enumValue = $"(int)TrackingProperty.{type.TypeName}_{field.PropertyName}";
                var factory = GetChangeValueFactory(field.TypeFullName);

                sb.AppendLine($"{ii}[MemoryPack.MemoryPackIgnore, MessagePack.IgnoreMember]");
                sb.AppendLine($"{ii}public {field.TypeFullName} {field.PropertyName}");
                sb.AppendLine($"{ii}{{");
                sb.AppendLine($"{iii}get => {field.FieldName};");
                sb.AppendLine($"{iii}set");
                sb.AppendLine($"{iii}{{");
                sb.AppendLine($"{iii}    if (EqualityComparer<{field.TypeFullName}>.Default.Equals({field.FieldName}, value)) return;");
                sb.AppendLine($"{iii}    var _tracker = ChangeTracker.Current;");
                sb.AppendLine($"{iii}    if (_tracker != null)");
                sb.AppendLine($"{iii}        _tracker.RecordFieldChange(this, TrackedTypeId, {enumValue}, {factory}({field.FieldName}), {factory}(value));");
                sb.AppendLine($"{iii}    {field.FieldName} = value;");
                sb.AppendLine($"{iii}}}");
                sb.AppendLine($"{ii}}}");
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}}}");
        }

        private static void GenerateSubscriptionClass(StringBuilder sb, TrackedTypeInfo type, string indent)
        {
            var ii = indent + "    ";

            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// Static type-level subscription for <see cref=\"{type.TypeName}\"/> [Tracked] field changes.");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}public static class Tracked{type.TypeName}");
            sb.AppendLine($"{indent}{{");

            sb.AppendLine($"{ii}private static Action<ChangeTreeArgs>? _handler;");
            sb.AppendLine();

            sb.AppendLine($"{ii}/// <summary>Fires when any {type.TypeName} [Tracked] field changes during a dispatched method.</summary>");
            sb.AppendLine($"{ii}public static event Action<ChangeTreeArgs>? OnChanged;");
            sb.AppendLine();

            sb.AppendLine($"{ii}/// <summary>Register this subscription with ChangeTracker. Call once at startup.</summary>");
            sb.AppendLine($"{ii}public static void Register()");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    _handler = args => OnChanged?.Invoke(args);");
            sb.AppendLine($"{ii}    ChangeTracker.Subscribe({type.TypeName}.TrackedTypeId, _handler);");
            sb.AppendLine($"{ii}}}");
            sb.AppendLine();

            sb.AppendLine($"{ii}/// <summary>Unregister this subscription.</summary>");
            sb.AppendLine($"{ii}public static void Unregister()");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    if (_handler != null)");
            sb.AppendLine($"{ii}    {{");
            sb.AppendLine($"{ii}        ChangeTracker.Unsubscribe({type.TypeName}.TrackedTypeId, _handler);");
            sb.AppendLine($"{ii}        _handler = null;");
            sb.AppendLine($"{ii}    }}");
            sb.AppendLine($"{ii}}}");

            sb.AppendLine($"{indent}}}");
        }
    }
}
