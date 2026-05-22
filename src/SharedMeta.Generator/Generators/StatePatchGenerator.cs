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
        // Element sub-wrappable: a List<T> where T itself has [Id] properties
        // (so the element gets its own PatchWrapper). Phase 1 supports List<T> only.
        public bool IsElementSubWrappable { get; set; }
        public string? ElementSubTypeName { get; set; }
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
                // For reference types, the nullable annotation is part of the symbol's
                // display string (`Card?` vs `Card`). When the same nested type appears
                // both as `Card?` and `Card` in different parents, our dedupe logic
                // (keyed on full type name) would treat them as two distinct types and
                // emit two competing PatchWrappers in the same nested scope. Strip the
                // annotation here so all downstream logic sees one canonical type.
                nonNullType = nonNullType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
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
                    // Normalize the element type's nullable annotation (see notes on the
                    // reference-type branch above) so List<Card?> and List<Card> resolve
                    // to the same canonical CardPatchWrapper.
                    var elemTypeArg = named.TypeArguments[0];
                    if (elemTypeArg.NullableAnnotation == NullableAnnotation.Annotated)
                        elemTypeArg = elemTypeArg.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                    field.CollectionBaseType = $"System.Collections.Generic.List<{elemTypeArg.ToDisplayString()}>";
                    field.ElementTypeFullName = elemTypeArg.ToDisplayString();

                    // Element sub-wrappable: if the element type itself has [Id]/[MemoryPackOrder]/[Key]
                    // properties, we generate a specialized PatchableList that wraps each element in its
                    // own PatchWrapper. Mutations through the wrapper write into a per-element subtree.
                    if (elemTypeArg is INamedTypeSymbol elementType
                        && !elementType.IsValueType
                        && elementType.SpecialType != SpecialType.System_String
                        && HasIdProperties(elementType))
                    {
                        field.IsElementSubWrappable = true;
                        field.ElementSubTypeName = elementType.Name;
                        // Recursively analyze the element type so we have its PatchTypeInfo
                        // available when generating the specialized list class.
                        var elemInfo = AnalyzeType(elementType, subTypes, visited);
                        if (elemInfo != null && elemInfo.Fields.Count > 0)
                            subTypes.Add(elemInfo);
                    }
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

            // Implicit conversion from raw {StateType} → wrapper. Produces an "untracked
            // proxy" wrapper (no parent node, no serializer) — reads/writes apply to the
            // raw state but never reach a patch tree. This exists for compile-time-only
            // shape-matching: helper methods that return {Wrapper} can be invoked outside
            // the PatchTracked context (where collections still hand out raw elements)
            // without forcing the caller to write boilerplate. Inside the PatchTracked
            // context the specialized PatchableList already returns a real wrapper, so
            // this conversion is never actually exercised.
            //
            // Note: there is intentionally NO reverse operator. {Wrapper} → {StateType}
            // would silently strip patch tracking, which is exactly the bug class we're
            // using the type system to prevent.
            //
            // Two overloads (nullable + non-nullable) so common helper patterns like
            //   var hero = state.Heroes.FirstOrDefault(...);  // Hero?
            //   return hero;                                  // wrapper return type
            // don't hit a CS8604 warning for the nullable input.
            //
            // The attribute uses a string literal ("state") instead of nameof(state)
            // because `nameof(parameter)` inside an attribute on the same method only
            // works in C# 11+. Generated code must compile against older language
            // versions too (older test/example projects).
            sb.AppendLine($"{ii}[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(\"state\")]");
            sb.AppendLine($"{ii}public static implicit operator {wrapperName}?({stateType}? state)");
            sb.AppendLine($"{ii}    => state == null ? null : new {wrapperName}(state, null, null!);");
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

            // Nested sub-wrapper classes (deduplicated by sub-type full name)
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

            // Element-sub-wrappable list element wrappers + their specialized list class.
            // The element type's PatchWrapper must exist before its list class can reference it,
            // so emit the wrapper first (also deduplicated against the sub-wrappable set above).
            var generatedListElems = new HashSet<string>();
            foreach (var field in typeInfo.Fields.Where(f => f.IsElementSubWrappable))
            {
                if (field.ElementTypeFullName == null || generatedListElems.Contains(field.ElementTypeFullName))
                    continue;
                generatedListElems.Add(field.ElementTypeFullName);

                var elemInfo = subTypes.FirstOrDefault(s => s.TypeFullName == field.ElementTypeFullName);
                if (elemInfo == null) continue;

                // Emit the element type's PatchWrapper if it wasn't already emitted as a sub-wrapper
                if (!generated.Contains(field.ElementTypeFullName))
                {
                    generated.Add(field.ElementTypeFullName);
                    sb.AppendLine();
                    GenerateWrapper(sb, elemInfo, subTypes, ii);
                }

                sb.AppendLine();
                GenerateElementListClass(sb, field, elemInfo, ii);
            }

            sb.AppendLine($"{indent}}}");
        }

        /// <summary>
        /// Generate a specialized PatchableList class for a List&lt;T&gt; field whose element
        /// type T has its own PatchWrapper. Indexer / enumerator return TPatchWrapper bound
        /// to a per-element subtree of the patch tree (an element-by-index child of the
        /// collection node), so element-level mutations write into <c>{field}/[index]/{member}</c>
        /// rather than dumping the whole list as a terminal blob.
        ///
        /// Phase 2 (granular list patches): structural ops are recorded as
        /// <see cref="PatchListOp"/> entries on the collection node's <c>StructuralOps</c>
        /// list, and Insert/RemoveAt also shift the indices of any pending element children
        /// so they remain canonical for the post-op state.
        /// </summary>
        private static void GenerateElementListClass(
            StringBuilder sb, PatchFieldInfo field, PatchTypeInfo elemInfo, string ii)
        {
            var className = field.ElementSubTypeName + "PatchableList";
            var elemType = field.ElementTypeFullName!;
            var elemWrapper = field.ElementSubTypeName + "PatchWrapper";
            var listType = $"System.Collections.Generic.List<{elemType}>";
            var inner = ii + "    ";

            sb.AppendLine($"{ii}/// <summary>");
            sb.AppendLine($"{ii}/// Specialized PatchableList for List&lt;{field.ElementSubTypeName}&gt;.");
            sb.AppendLine($"{ii}/// Hands out {elemWrapper} elements bound to per-element subtree nodes,");
            sb.AppendLine($"{ii}/// records structural mutations as PatchListOp entries on the collection node.");
            sb.AppendLine($"{ii}/// </summary>");
            sb.AppendLine($"{ii}public class {className}");
            sb.AppendLine($"{ii}    : System.Collections.Generic.IList<{elemWrapper}>,");
            sb.AppendLine($"{ii}      System.Collections.Generic.IReadOnlyList<{elemWrapper}>");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{inner}private readonly {listType} _list;");
            sb.AppendLine($"{inner}private readonly PatchNode? _collectionNode;");
            sb.AppendLine($"{inner}private readonly IMetaSerializer? _serializer;");
            sb.AppendLine();
            sb.AppendLine($"{inner}public {className}({listType} list, PatchNode? collectionNode, IMetaSerializer? serializer)");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    _list = list;");
            sb.AppendLine($"{inner}    _collectionNode = collectionNode;");
            sb.AppendLine($"{inner}    _serializer = serializer;");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}/// <summary>Access the underlying List directly.</summary>");
            sb.AppendLine($"{inner}public {listType} Inner => _list;");
            sb.AppendLine();
            sb.AppendLine($"{inner}/// <summary>Implicit conversion from raw List for setter compatibility on the parent wrapper.</summary>");
            sb.AppendLine($"{inner}public static implicit operator {className}({listType} list) => new {className}(list, null, null);");
            sb.AppendLine();
            sb.AppendLine($"{inner}public int Count => _list.Count;");
            sb.AppendLine($"{inner}public bool IsReadOnly => false;");
            sb.AppendLine();
            sb.AppendLine($"{inner}private bool TrackingEnabled => _collectionNode != null && _serializer != null;");
            sb.AppendLine();
            sb.AppendLine($"{inner}/// <summary>");
            sb.AppendLine($"{inner}/// Indexer get returns a wrapper bound to the per-element subtree node so");
            sb.AppendLine($"{inner}/// mutations through it (<c>list[5].Field = X</c>) flow into the patch.");
            sb.AppendLine($"{inner}/// Indexer set records a Set structural op and drops any in-place mutations");
            sb.AppendLine($"{inner}/// previously recorded for that index (the element is being wholesale-replaced).");
            sb.AppendLine($"{inner}/// </summary>");
            sb.AppendLine($"{inner}public {elemWrapper} this[int index]");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    get => new {elemWrapper}(_list[index], _collectionNode?.GetOrCreateElementChild(index), _serializer!);");
            sb.AppendLine($"{inner}    set");
            sb.AppendLine($"{inner}    {{");
            sb.AppendLine($"{inner}        _list[index] = value.Raw;");
            sb.AppendLine($"{inner}        if (TrackingEnabled)");
            sb.AppendLine($"{inner}        {{");
            sb.AppendLine($"{inner}            _collectionNode!.RemoveElementChild(index);");
            sb.AppendLine($"{inner}            _collectionNode.AddStructuralOp(new SharedMeta.Core.Patch.PatchListOp");
            sb.AppendLine($"{inner}            {{");
            sb.AppendLine($"{inner}                Kind = SharedMeta.Core.Patch.PatchListOpKind.Set,");
            sb.AppendLine($"{inner}                Index = index,");
            sb.AppendLine($"{inner}                ElementBytes = _serializer!.Pack(value.Raw).ToArray(),");
            sb.AppendLine($"{inner}            }});");
            sb.AppendLine($"{inner}        }}");
            sb.AppendLine($"{inner}    }}");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}public void Add({elemWrapper} item)");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    _list.Add(item.Raw);");
            sb.AppendLine($"{inner}    if (TrackingEnabled)");
            sb.AppendLine($"{inner}    {{");
            sb.AppendLine($"{inner}        _collectionNode!.AddStructuralOp(new SharedMeta.Core.Patch.PatchListOp");
            sb.AppendLine($"{inner}        {{");
            sb.AppendLine($"{inner}            Kind = SharedMeta.Core.Patch.PatchListOpKind.Insert,");
            sb.AppendLine($"{inner}            Index = _list.Count - 1,");
            sb.AppendLine($"{inner}            ElementBytes = _serializer!.Pack(item.Raw).ToArray(),");
            sb.AppendLine($"{inner}        }});");
            sb.AppendLine($"{inner}    }}");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}public void Insert(int index, {elemWrapper} item)");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    if (TrackingEnabled)");
            sb.AppendLine($"{inner}        _collectionNode!.ShiftElementChildren(fromIndex: index, delta: +1);");
            sb.AppendLine($"{inner}    _list.Insert(index, item.Raw);");
            sb.AppendLine($"{inner}    if (TrackingEnabled)");
            sb.AppendLine($"{inner}    {{");
            sb.AppendLine($"{inner}        _collectionNode!.AddStructuralOp(new SharedMeta.Core.Patch.PatchListOp");
            sb.AppendLine($"{inner}        {{");
            sb.AppendLine($"{inner}            Kind = SharedMeta.Core.Patch.PatchListOpKind.Insert,");
            sb.AppendLine($"{inner}            Index = index,");
            sb.AppendLine($"{inner}            ElementBytes = _serializer!.Pack(item.Raw).ToArray(),");
            sb.AppendLine($"{inner}        }});");
            sb.AppendLine($"{inner}    }}");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}public bool Remove({elemWrapper} item)");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    var idx = _list.IndexOf(item.Raw);");
            sb.AppendLine($"{inner}    if (idx < 0) return false;");
            sb.AppendLine($"{inner}    RemoveAt(idx);");
            sb.AppendLine($"{inner}    return true;");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}public void RemoveAt(int index)");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    if (TrackingEnabled)");
            sb.AppendLine($"{inner}    {{");
            sb.AppendLine($"{inner}        _collectionNode!.RemoveElementChild(index);");
            sb.AppendLine($"{inner}        _collectionNode.ShiftElementChildren(fromIndex: index + 1, delta: -1);");
            sb.AppendLine($"{inner}    }}");
            sb.AppendLine($"{inner}    _list.RemoveAt(index);");
            sb.AppendLine($"{inner}    if (TrackingEnabled)");
            sb.AppendLine($"{inner}    {{");
            sb.AppendLine($"{inner}        _collectionNode!.AddStructuralOp(new SharedMeta.Core.Patch.PatchListOp");
            sb.AppendLine($"{inner}        {{");
            sb.AppendLine($"{inner}            Kind = SharedMeta.Core.Patch.PatchListOpKind.RemoveAt,");
            sb.AppendLine($"{inner}            Index = index,");
            sb.AppendLine($"{inner}        }});");
            sb.AppendLine($"{inner}    }}");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}public void Clear()");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    _list.Clear();");
            sb.AppendLine($"{inner}    if (TrackingEnabled)");
            sb.AppendLine($"{inner}    {{");
            sb.AppendLine($"{inner}        _collectionNode!.ClearCollectionState();");
            sb.AppendLine($"{inner}        _collectionNode.AddStructuralOp(new SharedMeta.Core.Patch.PatchListOp");
            sb.AppendLine($"{inner}        {{");
            sb.AppendLine($"{inner}            Kind = SharedMeta.Core.Patch.PatchListOpKind.Clear,");
            sb.AppendLine($"{inner}        }});");
            sb.AppendLine($"{inner}    }}");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}public bool Contains({elemWrapper} item) => _list.Contains(item.Raw);");
            sb.AppendLine($"{inner}public int IndexOf({elemWrapper} item) => _list.IndexOf(item.Raw);");
            sb.AppendLine($"{inner}public void CopyTo({elemWrapper}[] array, int arrayIndex)");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    for (int i = 0; i < _list.Count; i++) array[arrayIndex + i] = this[i];");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine();
            sb.AppendLine($"{inner}public System.Collections.Generic.IEnumerator<{elemWrapper}> GetEnumerator()");
            sb.AppendLine($"{inner}{{");
            sb.AppendLine($"{inner}    for (int i = 0; i < _list.Count; i++) yield return this[i];");
            sb.AppendLine($"{inner}}}");
            sb.AppendLine($"{inner}System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
            sb.AppendLine($"{ii}}}");
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

            // Element-sub-wrappable List<T>: use a specialized list class that hands
            // out element-level PatchWrappers and tracks mutations into per-element
            // sub-trees of the patch.
            if (field.IsElementSubWrappable && field.CollectionWrapperType == "PatchableList")
            {
                var elementWrapper = field.ElementSubTypeName + "PatchWrapper";
                var listClassName = field.ElementSubTypeName + "PatchableList";

                sb.AppendLine($"{ii}/// <summary>[Id({field.FieldId})] {field.Name} — element-level patch tracking via {elementWrapper}.</summary>");
                sb.AppendLine($"{ii}private {listClassName}? {fieldVar};");
                sb.AppendLine($"{ii}public {listClassName} {field.Name}");
                sb.AppendLine($"{ii}{{");
                sb.AppendLine($"{ii}    get => {fieldVar} ??= new {listClassName}(_state.{field.Name}, _node?.GetOrCreateChild({field.FieldId}), _serializer);");
                // Setter records a FullReplace structural op rather than a terminal Value.
                // Keeps the invariant: list nodes never have Value; subsequent in-place
                // mutations (Add, indexer Set, etc.) chain in submission order with the
                // replacement instead of being silently dropped by the receiver.
                sb.AppendLine($"{ii}    set {{ _state.{field.Name} = value.Inner; _node?.MarkChildFullReplace({field.FieldId}, _serializer.Pack(value.Inner)); {fieldVar} = null; }}");
                sb.AppendLine($"{ii}}}");
                sb.AppendLine();
                sb.AppendLine($"{ii}/// <summary>Replace the entire {field.Name} collection.</summary>");
                sb.AppendLine($"{ii}public void Set{field.Name}({field.CollectionBaseType} value)");
                sb.AppendLine($"{ii}{{");
                sb.AppendLine($"{ii}    _state.{field.Name} = value;");
                sb.AppendLine($"{ii}    _node?.MarkChildFullReplace({field.FieldId}, _serializer.Pack(value));");
                sb.AppendLine($"{ii}    {fieldVar} = null;");
                sb.AppendLine($"{ii}}}");
                return;
            }

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

            // PatchableList uses op-based replacement (FullReplace structural op) so
            // subsequent mutations chain in order. Dict / HashSet / Array don't have an
            // op stream — they keep using terminal Value writes (each mutation overwrites
            // the snapshot wholesale, so the invariant holds trivially for them).
            var replaceCall = field.CollectionWrapperType == "PatchableList"
                ? "MarkChildFullReplace"
                : "MarkChildTerminal";

            sb.AppendLine($"{ii}/// <summary>[Id({field.FieldId})] {field.Name} — auto-tracks mutations.</summary>");
            sb.AppendLine($"{ii}private {patchableType}? {fieldVar};");
            sb.AppendLine($"{ii}public {patchableType} {field.Name}");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    get => {fieldVar} ??= new {patchableType}(_state.{field.Name}, _node, {field.FieldId}, _serializer);");
            sb.AppendLine($"{ii}    set {{ _state.{field.Name} = value.Inner; _node?.{replaceCall}({field.FieldId}, _serializer.Pack(value.Inner)); {fieldVar} = null; }}");
            sb.AppendLine($"{ii}}}");
            sb.AppendLine();
            sb.AppendLine($"{ii}/// <summary>Replace the entire {field.Name} collection.</summary>");
            sb.AppendLine($"{ii}public void Set{field.Name}({field.CollectionBaseType} value)");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    _state.{field.Name} = value;");
            sb.AppendLine($"{ii}    _node?.{replaceCall}({field.FieldId}, _serializer.Pack(value));");
            sb.AppendLine($"{ii}    {fieldVar} = null;");
            sb.AppendLine($"{ii}}}");
        }

        private static void GenerateSubWrappableProp(StringBuilder sb, PatchFieldInfo field, string ii)
        {
            var subWrapper = field.SubTypeName + "PatchWrapper";
            var fieldVar = "_" + char.ToLower(field.Name[0]) + field.Name.Substring(1);

            sb.AppendLine($"{ii}/// <summary>[Id({field.FieldId})] {field.Name} — sub-wrapper for granular tracking.</summary>");
            sb.AppendLine($"{ii}private {subWrapper}? {fieldVar};");

            // Both getter AND setter are emitted so user code can write the natural
            // form `wrapper.Field = new SubType { ... }`. The implicit operator on
            // the sub-wrapper class converts the raw object into an untracked wrapper,
            // and the setter extracts .Raw and writes a terminal Value on the field's
            // patch node. Subsequent in-place mutations through the same wrapper after
            // the assignment add Field-children on top of that Value, and the generated
            // applier handles the Value+Children pair (Value is unpacked first, then
            // the children mutations are layered on top in submission order).
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
                sb.AppendLine($"{ii}    set");
                sb.AppendLine($"{ii}    {{");
                sb.AppendLine($"{ii}        _state.{field.Name} = value?.Raw;");
                sb.AppendLine($"{ii}        _node?.MarkChildTerminal({field.FieldId}, _serializer.Pack(_state.{field.Name}));");
                sb.AppendLine($"{ii}        {fieldVar} = null;");
                sb.AppendLine($"{ii}    }}");
                sb.AppendLine($"{ii}}}");
            }
            else
            {
                sb.AppendLine($"{ii}public {subWrapper} {field.Name}");
                sb.AppendLine($"{ii}{{");
                sb.AppendLine($"{ii}    get => {fieldVar} ??= new {subWrapper}(");
                sb.AppendLine($"{ii}        _state.{field.Name}, _node?.GetOrCreateChild({field.FieldId}), _serializer);");
                sb.AppendLine($"{ii}    set");
                sb.AppendLine($"{ii}    {{");
                sb.AppendLine($"{ii}        _state.{field.Name} = value.Raw;");
                sb.AppendLine($"{ii}        _node?.MarkChildTerminal({field.FieldId}, _serializer.Pack(value.Raw));");
                sb.AppendLine($"{ii}        {fieldVar} = null;");
                sb.AppendLine($"{ii}    }}");
                sb.AppendLine($"{ii}}}");
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
                EmitFieldApplyCase(sb, field, ii);

            sb.AppendLine($"{ii}        }}");
            sb.AppendLine($"{ii}    }}");
            sb.AppendLine($"{ii}}}");

            // Private methods for sub-type applying (deduplicated). This includes both
            // SubWrappable child fields AND element types of IsElementSubWrappable lists,
            // because both cases need a per-type Apply{Name} method to recurse into.
            var generated = new HashSet<string>();
            GenerateSubAppliers(sb, typeInfo, subTypes, generated, ii);

            sb.AppendLine($"{indent}}}");
        }

        /// <summary>
        /// Emit the switch case for a single patch-tree field. Shared between the root
        /// Apply method and per-sub-type Apply{Name} methods so the same field shapes
        /// (terminal / sub-wrappable / element-sub-wrappable list) are handled identically
        /// at every depth of the patch tree.
        /// </summary>
        private static void EmitFieldApplyCase(StringBuilder sb, PatchFieldInfo field, string ii)
        {
            sb.AppendLine($"{ii}            case {field.FieldId}: // {field.Name}");

            if (field.IsTracked)
            {
                // [Tracked] backing field — use generated property setter which handles change tracking
                sb.AppendLine($"{ii}                if (child.IsTerminal)");
                sb.AppendLine($"{ii}                    state.{field.Name} = serializer.Unpack<{field.TypeFullName}>(child.Value!);");
            }
            else if (field.IsElementSubWrappable && field.CollectionWrapperType == "PatchableList")
            {
                // List<TElement> with element-sub-wrappable elements: structural ops +
                // per-element subtree mutations applied via CollectionPatchApplier.
                sb.AppendLine($"{ii}                if (state.{field.Name} != null)");
                sb.AppendLine($"{ii}                {{");
                sb.AppendLine($"{ii}                    SharedMeta.Core.Patch.CollectionPatchApplier.Apply<{field.ElementTypeFullName}>(");
                sb.AppendLine($"{ii}                        state.{field.Name},");
                sb.AppendLine($"{ii}                        child,");
                sb.AppendLine($"{ii}                        serializer,");
                sb.AppendLine($"{ii}                        unpackElement: bytes => serializer.Unpack<{field.ElementTypeFullName}>(bytes)!,");
                sb.AppendLine($"{ii}                        unpackList: bytes => serializer.Unpack<{field.CollectionBaseType}>(bytes)!,");
                sb.AppendLine($"{ii}                        applyElementSubtree: (elem, subtree) => Apply{field.ElementSubTypeName}(elem, subtree, serializer));");
                sb.AppendLine($"{ii}                }}");
            }
            else if (field.Kind == PatchFieldKind.Collection && field.CollectionWrapperType == "PatchableList")
            {
                // List<TElement> where TElement is a primitive / value-type / non-sub-wrappable
                // class: structural ops without element subtree recursion.
                sb.AppendLine($"{ii}                if (state.{field.Name} != null)");
                sb.AppendLine($"{ii}                {{");
                sb.AppendLine($"{ii}                    SharedMeta.Core.Patch.CollectionPatchApplier.Apply<{field.ElementTypeFullName}>(");
                sb.AppendLine($"{ii}                        state.{field.Name},");
                sb.AppendLine($"{ii}                        child,");
                sb.AppendLine($"{ii}                        serializer,");
                sb.AppendLine($"{ii}                        unpackElement: bytes => serializer.Unpack<{field.ElementTypeFullName}>(bytes)!,");
                sb.AppendLine($"{ii}                        unpackList: bytes => serializer.Unpack<{field.CollectionBaseType}>(bytes)!);");
                sb.AppendLine($"{ii}                }}");
            }
            else if (field.Kind == PatchFieldKind.SubWrappable)
            {
                // Value-then-Children: a SetX call writes a snapshot of the new sub-object,
                // and any subsequent in-place mutations through the wrapper add Field
                // children to the same node. Apply Value first (replacing the sub-object),
                // then recurse into Children to layer on the later mutations in order.
                sb.AppendLine($"{ii}                if (child.IsTerminal)");
                sb.AppendLine($"{ii}                    state.{field.Name} = serializer.Unpack<{field.TypeFullName}>(child.Value!);");
                if (field.IsNullable)
                    sb.AppendLine($"{ii}                if (state.{field.Name} != null && child.Children != null)");
                else
                    sb.AppendLine($"{ii}                if (child.Children != null)");
                sb.AppendLine($"{ii}                    Apply{field.SubTypeName}(state.{field.Name}!, child, serializer);");
            }
            else
            {
                sb.AppendLine($"{ii}                if (child.IsTerminal)");
                sb.AppendLine($"{ii}                    state.{field.Name} = serializer.Unpack<{field.TypeFullName}>(child.Value!);");
            }

            sb.AppendLine($"{ii}                break;");
        }

        private static void GenerateSubAppliers(
            StringBuilder sb, PatchTypeInfo typeInfo, List<PatchTypeInfo> subTypes,
            HashSet<string> generated, string ii)
        {
            // SubWrappable child fields → emit Apply{SubTypeName}
            foreach (var field in typeInfo.Fields.Where(f => f.Kind == PatchFieldKind.SubWrappable))
            {
                if (field.SubTypeFullName == null) continue;
                EmitSubApplier(sb, field.SubTypeFullName, subTypes, generated, ii);
            }

            // Element types of element-sub-wrappable lists → emit Apply{ElementName} too,
            // because the parent's switch case calls Apply{ElementName}(list[idx], elemChild, ...)
            foreach (var field in typeInfo.Fields.Where(f => f.IsElementSubWrappable))
            {
                if (field.ElementTypeFullName == null) continue;
                EmitSubApplier(sb, field.ElementTypeFullName, subTypes, generated, ii);
            }
        }

        /// <summary>
        /// Emit an Apply{TypeName}(state, patch, serializer) method for the given sub-type
        /// and recurse into its own sub-types. Idempotent — adds the type to <paramref name="generated"/>
        /// to dedupe across sibling fields that reference the same nested type.
        /// </summary>
        private static void EmitSubApplier(
            StringBuilder sb, string subTypeFullName, List<PatchTypeInfo> subTypes,
            HashSet<string> generated, string ii)
        {
            if (generated.Contains(subTypeFullName)) return;
            generated.Add(subTypeFullName);

            var subInfo = subTypes.FirstOrDefault(s => s.TypeFullName == subTypeFullName);
            if (subInfo == null) return;

            sb.AppendLine();
            sb.AppendLine($"{ii}private static void Apply{subInfo.TypeName}({subInfo.TypeFullName} state, PatchNode patch, IMetaSerializer serializer)");
            sb.AppendLine($"{ii}{{");
            sb.AppendLine($"{ii}    if (patch.Children == null) return;");
            sb.AppendLine($"{ii}    foreach (var child in patch.Children)");
            sb.AppendLine($"{ii}    {{");
            sb.AppendLine($"{ii}        switch (child.FieldId)");
            sb.AppendLine($"{ii}        {{");

            foreach (var subField in subInfo.Fields)
                EmitFieldApplyCase(sb, subField, ii);

            sb.AppendLine($"{ii}        }}");
            sb.AppendLine($"{ii}    }}");
            sb.AppendLine($"{ii}}}");

            // Recursively generate sub-appliers for this sub-type's own nested types
            GenerateSubAppliers(sb, subInfo, subTypes, generated, ii);
        }
    }
}
