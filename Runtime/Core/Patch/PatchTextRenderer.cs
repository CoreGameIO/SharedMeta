using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Renders <see cref="PatchNode"/> trees as human-readable JSON for diagnostics.
    /// Two entry points:
    /// <list type="bullet">
    ///   <item><see cref="ToJson"/> — visualize a single patch (only changed fields, with names and decoded values).</item>
    ///   <item><see cref="DiffToJson"/> — compare two patches for the same state and emit a side-by-side JSON of divergences.</item>
    /// </list>
    /// Cold path only — used by desync reporting / debug tooling. Not allocated in normal RPC flow.
    /// </summary>
    public static class PatchTextRenderer
    {
        // ─────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Render a single patch as compact JSON: only changed fields, with names + decoded values.
        /// </summary>
        public static string ToJson(PatchNode node, IPatchSchema schema, IMetaSerializer serializer, bool indented = true)
        {
            if (node == null) return "null";
            var sb = new StringBuilder();
            WriteNode(sb, node, schema, serializer, indented ? 0 : -1);
            return sb.ToString();
        }

        /// <summary>
        /// Render a side-by-side diff of two patches as JSON. Each diverged leaf becomes
        /// <c>"FieldName": { "server": ..., "client": ... }</c>. Sub-trees recurse.
        /// </summary>
        public static string DiffToJson(PatchNode? server, PatchNode? client, IPatchSchema schema, IMetaSerializer serializer, bool indented = true)
        {
            var sb = new StringBuilder();
            WriteDiff(sb, server, client, schema, serializer, indented ? 0 : -1);
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────
        // Single-patch renderer
        // ─────────────────────────────────────────────────────────────────

        private static void WriteNode(StringBuilder sb, PatchNode node, IPatchSchema schema, IMetaSerializer serializer, int indent)
        {
            // Only field children participate in object rendering. Element children
            // (Kind = ElementByIndex) live underneath collection-field nodes and are
            // rendered by WriteCollectionNode.
            var fieldChildren = FilterFieldChildren(node.Children);
            if (fieldChildren == null || fieldChildren.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            NewLine(sb, indent + 1);

            for (int i = 0; i < fieldChildren.Count; i++)
            {
                var child = fieldChildren[i];
                var name = schema.GetFieldName(child.FieldId);
                AppendString(sb, name);
                sb.Append(':');
                if (indent >= 0) sb.Append(' ');

                if (IsCollectionNode(child))
                {
                    WriteCollectionNode(sb, child, schema, serializer, indent + 1);
                }
                else if (child.IsTerminal)
                {
                    var decoded = SafeDecode(schema, child.FieldId, child.Value!, serializer);
                    AppendValue(sb, decoded, indent + 1);
                }
                else
                {
                    var nested = schema.GetNestedSchema(child.FieldId);
                    if (nested != null)
                        WriteNode(sb, child, nested, serializer, indent + 1);
                    else
                        sb.Append("\"<non-terminal, no nested schema>\"");
                }

                if (i < fieldChildren.Count - 1) sb.Append(',');
                NewLine(sb, i < fieldChildren.Count - 1 ? indent + 1 : indent);
            }

            sb.Append('}');
        }

        // ─────────────────────────────────────────────────────────────────
        // Collection-node renderer (Phase 2)
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// True if a node represents a list-typed collection field — it carries
        /// structural ops, element-by-index children, or both.
        /// </summary>
        private static bool IsCollectionNode(PatchNode node)
        {
            if (node.StructuralOps is { Count: > 0 }) return true;
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    if (node.Children[i].Kind == PatchChildKind.ElementByIndex)
                        return true;
            }
            return false;
        }

        private static void WriteCollectionNode(StringBuilder sb, PatchNode node, IPatchSchema schema, IMetaSerializer serializer, int indent)
        {
            sb.Append('{');
            bool firstSection = true;

            // Structural ops section
            if (node.StructuralOps is { Count: > 0 })
            {
                NewLine(sb, indent + 1);
                AppendString(sb, "ops");
                sb.Append(": [");
                for (int i = 0; i < node.StructuralOps.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, indent + 2);
                    WriteListOp(sb, node.StructuralOps[i]);
                }
                NewLine(sb, indent + 1);
                sb.Append(']');
                firstSection = false;
            }

            // Element children section — recurse into the element's nested schema
            // for sub-wrappable list elements. The schema's GetNestedSchema is keyed
            // by the field id (collection field id), and for an element-sub-wrappable
            // list it returns the schema of the element type.
            var elementChildren = FilterElementChildren(node.Children);
            if (elementChildren != null && elementChildren.Count > 0)
            {
                if (!firstSection) sb.Append(',');
                NewLine(sb, indent + 1);
                AppendString(sb, "elements");
                sb.Append(':');
                if (indent >= 0) sb.Append(' ');
                sb.Append('{');
                var elementSchema = schema.GetNestedSchema(node.FieldId);
                for (int i = 0; i < elementChildren.Count; i++)
                {
                    var elem = elementChildren[i];
                    if (i > 0) sb.Append(',');
                    NewLine(sb, indent + 2);
                    AppendString(sb, "[" + elem.FieldId.ToString(CultureInfo.InvariantCulture) + "]");
                    sb.Append(':');
                    if (indent >= 0) sb.Append(' ');
                    if (elementSchema != null)
                        WriteNode(sb, elem, elementSchema, serializer, indent + 2);
                    else
                        sb.Append("\"<element, no nested schema>\"");
                }
                NewLine(sb, indent + 1);
                sb.Append('}');
                firstSection = false;
            }

            if (!firstSection) NewLine(sb, indent);
            sb.Append('}');
        }

        private static void WriteListOp(StringBuilder sb, PatchListOp op)
        {
            sb.Append('{');
            AppendString(sb, "kind");
            sb.Append(": ");
            AppendString(sb, op.Kind.ToString());
            switch (op.Kind)
            {
                case PatchListOpKind.Insert:
                case PatchListOpKind.Set:
                case PatchListOpKind.RemoveAt:
                    sb.Append(", ");
                    AppendString(sb, "index");
                    sb.Append(": ");
                    sb.Append(op.Index.ToString(CultureInfo.InvariantCulture));
                    break;
            }
            // ElementBytes is intentionally not decoded — we don't know its type here
            // (renderer is generic, schema is per-field). The kind+index alone is enough
            // for diagnostics; the receiver gets the actual values from the apply path.
            if (op.ElementBytes is { Length: > 0 })
            {
                sb.Append(", ");
                AppendString(sb, "payloadBytes");
                sb.Append(": ");
                sb.Append(op.ElementBytes.Length.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        // ─────────────────────────────────────────────────────────────────
        // Diff renderer
        // ─────────────────────────────────────────────────────────────────

        private static void WriteDiff(StringBuilder sb, PatchNode? server, PatchNode? client, IPatchSchema schema, IMetaSerializer serializer, int indent)
        {
            // Collect union of FieldIds present on either side (FIELD children only)
            var ids = new SortedSet<long>();
            if (server?.Children != null)
                foreach (var c in server.Children)
                    if (c.Kind == PatchChildKind.Field) ids.Add(c.FieldId);
            if (client?.Children != null)
                foreach (var c in client.Children)
                    if (c.Kind == PatchChildKind.Field) ids.Add(c.FieldId);

            if (ids.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            NewLine(sb, indent + 1);

            int written = 0;
            foreach (var id in ids)
            {
                var sChild = FindFieldChild(server, id);
                var cChild = FindFieldChild(client, id);

                // Skip if both sides are identical terminals AND neither has any
                // structural ops or children layered on top (which would carry their own
                // divergences that the terminal-equal check would otherwise hide).
                if (sChild != null && cChild != null
                    && sChild.IsTerminal && cChild.IsTerminal
                    && BytesEqual(sChild.Value, cChild.Value)
                    && (sChild.Children == null || sChild.Children.Count == 0)
                    && (cChild.Children == null || cChild.Children.Count == 0)
                    && (sChild.StructuralOps == null || sChild.StructuralOps.Count == 0)
                    && (cChild.StructuralOps == null || cChild.StructuralOps.Count == 0))
                {
                    continue;
                }

                if (written > 0)
                {
                    sb.Append(',');
                    NewLine(sb, indent + 1);
                }
                written++;

                var name = schema.GetFieldName(id);
                AppendString(sb, name);
                sb.Append(':');
                if (indent >= 0) sb.Append(' ');

                bool sIsTerm = sChild?.IsTerminal == true;
                bool cIsTerm = cChild?.IsTerminal == true;
                bool sIsCollection = sChild != null && IsCollectionNode(sChild);
                bool cIsCollection = cChild != null && IsCollectionNode(cChild);

                if (sIsCollection || cIsCollection)
                {
                    // Collection diff: show each side's collection JSON. Per-op-level
                    // diffing is non-trivial because op streams aren't aligned by index;
                    // for diagnostics this is good enough.
                    sb.Append('{');
                    NewLine(sb, indent + 2);
                    AppendString(sb, "server");
                    sb.Append(':');
                    if (indent >= 0) sb.Append(' ');
                    if (sChild != null) WriteCollectionNode(sb, sChild, schema, serializer, indent + 2);
                    else sb.Append("\"<absent>\"");
                    sb.Append(',');
                    NewLine(sb, indent + 2);
                    AppendString(sb, "client");
                    sb.Append(':');
                    if (indent >= 0) sb.Append(' ');
                    if (cChild != null) WriteCollectionNode(sb, cChild, schema, serializer, indent + 2);
                    else sb.Append("\"<absent>\"");
                    NewLine(sb, indent + 1);
                    sb.Append('}');
                }
                else if ((sChild == null || sIsTerm) && (cChild == null || cIsTerm))
                {
                    // Leaf-level divergence: emit { "server": ..., "client": ... }
                    sb.Append('{');
                    NewLine(sb, indent + 2);
                    AppendString(sb, "server");
                    sb.Append(':');
                    if (indent >= 0) sb.Append(' ');
                    if (sChild != null)
                    {
                        var decoded = SafeDecode(schema, id, sChild.Value!, serializer);
                        AppendValue(sb, decoded, indent + 2);
                    }
                    else sb.Append("\"<absent>\"");
                    sb.Append(',');
                    NewLine(sb, indent + 2);
                    AppendString(sb, "client");
                    sb.Append(':');
                    if (indent >= 0) sb.Append(' ');
                    if (cChild != null)
                    {
                        var decoded = SafeDecode(schema, id, cChild.Value!, serializer);
                        AppendValue(sb, decoded, indent + 2);
                    }
                    else sb.Append("\"<absent>\"");
                    NewLine(sb, indent + 1);
                    sb.Append('}');
                }
                else
                {
                    // At least one side has children — recurse via nested schema
                    var nested = schema.GetNestedSchema(id);
                    if (nested != null)
                        WriteDiff(sb, sChild, cChild, nested, serializer, indent + 1);
                    else
                        sb.Append("\"<sub-tree, no nested schema>\"");
                }
            }

            NewLine(sb, indent);
            sb.Append('}');
        }

        // ─────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────

        private static List<PatchNode>? FilterFieldChildren(List<PatchNode>? children)
        {
            if (children == null) return null;
            List<PatchNode>? result = null;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Kind == PatchChildKind.Field)
                    (result ??= new List<PatchNode>()).Add(children[i]);
            }
            return result;
        }

        private static List<PatchNode>? FilterElementChildren(List<PatchNode>? children)
        {
            if (children == null) return null;
            List<PatchNode>? result = null;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Kind == PatchChildKind.ElementByIndex)
                    (result ??= new List<PatchNode>()).Add(children[i]);
            }
            return result;
        }

        private static PatchNode? FindFieldChild(PatchNode? parent, long fieldId)
        {
            if (parent?.Children == null) return null;
            for (int i = 0; i < parent.Children.Count; i++)
            {
                var c = parent.Children[i];
                if (c.Kind == PatchChildKind.Field && c.FieldId == fieldId)
                    return c;
            }
            return null;
        }

        private static bool BytesEqual(byte[]? a, byte[]? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────
        // Safe decode wrapper
        // ─────────────────────────────────────────────────────────────────

        private static object? SafeDecode(IPatchSchema schema, long fieldId, byte[] bytes, IMetaSerializer serializer)
        {
            try { return schema.DecodeLeaf(fieldId, bytes, serializer); }
            catch (Exception ex) { return $"<decode error: {ex.GetType().Name}: {ex.Message}>"; }
        }

        // ─────────────────────────────────────────────────────────────────
        // Minimal JSON value writer (no Newtonsoft / System.Text.Json dep)
        // ─────────────────────────────────────────────────────────────────

        private static void AppendValue(StringBuilder sb, object? value, int indent)
        {
            switch (value)
            {
                case null: sb.Append("null"); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: AppendString(sb, s); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte sb1: sb.Append(sb1.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case ushort ush: sb.Append(ush.ToString(CultureInfo.InvariantCulture)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case uint ui: sb.Append(ui.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case decimal dec: sb.Append(dec.ToString(CultureInfo.InvariantCulture)); return;
                case Enum e: AppendString(sb, e.ToString()); return;
                case byte[] bytes: AppendByteArray(sb, bytes); return;
                case IDictionary dict: AppendDictionary(sb, dict, indent); return;
                case IEnumerable enumerable: AppendArray(sb, enumerable, indent); return;
                default: AppendString(sb, value.ToString() ?? ""); return;
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void AppendArray(StringBuilder sb, IEnumerable enumerable, int indent)
        {
            sb.Append('[');
            bool first = true;
            int count = 0;
            foreach (var item in enumerable)
            {
                if (!first) sb.Append(',');
                if (indent >= 0 && count > 0 && count % 16 == 0) NewLine(sb, indent + 1);
                else if (indent >= 0 && !first) sb.Append(' ');
                AppendValue(sb, item, indent + 1);
                first = false;
                count++;
            }
            sb.Append(']');
        }

        private static void AppendByteArray(StringBuilder sb, byte[] bytes)
        {
            // Compact one-line form for byte arrays — they're usually serialized blobs.
            sb.Append('[');
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(bytes[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
        }

        private static void AppendDictionary(StringBuilder sb, IDictionary dict, int indent)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry kvp in dict)
            {
                if (!first) sb.Append(',');
                if (indent >= 0) NewLine(sb, indent + 1);
                AppendString(sb, kvp.Key?.ToString() ?? "null");
                sb.Append(':');
                if (indent >= 0) sb.Append(' ');
                AppendValue(sb, kvp.Value, indent + 1);
                first = false;
            }
            if (!first && indent >= 0) NewLine(sb, indent);
            sb.Append('}');
        }

        private static void NewLine(StringBuilder sb, int indent)
        {
            if (indent < 0) return;
            sb.Append('\n');
            for (int i = 0; i < indent; i++) sb.Append("  ");
        }
    }
}
