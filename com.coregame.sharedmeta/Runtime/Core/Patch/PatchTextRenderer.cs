using System;
using System.Collections;
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
            if (node.Children == null || node.Children.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.Append('{');
            NewLine(sb, indent + 1);

            for (int i = 0; i < node.Children.Count; i++)
            {
                var child = node.Children[i];
                var name = schema.GetFieldName(child.FieldId);
                AppendString(sb, name);
                sb.Append(':');
                if (indent >= 0) sb.Append(' ');

                if (child.IsTerminal)
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

                if (i < node.Children.Count - 1) sb.Append(',');
                NewLine(sb, i < node.Children.Count - 1 ? indent + 1 : indent);
            }

            sb.Append('}');
        }

        // ─────────────────────────────────────────────────────────────────
        // Diff renderer
        // ─────────────────────────────────────────────────────────────────

        private static void WriteDiff(StringBuilder sb, PatchNode? server, PatchNode? client, IPatchSchema schema, IMetaSerializer serializer, int indent)
        {
            // Collect union of FieldIds present on either side
            var ids = new System.Collections.Generic.SortedSet<int>();
            if (server?.Children != null)
                foreach (var c in server.Children) ids.Add(c.FieldId);
            if (client?.Children != null)
                foreach (var c in client.Children) ids.Add(c.FieldId);

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
                var sChild = FindChild(server, id);
                var cChild = FindChild(client, id);

                // Skip if both sides are identical terminals
                if (sChild != null && cChild != null
                    && sChild.IsTerminal && cChild.IsTerminal
                    && BytesEqual(sChild.Value, cChild.Value))
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

                if ((sChild == null || sIsTerm) && (cChild == null || cIsTerm))
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

        private static PatchNode? FindChild(PatchNode? parent, int fieldId)
        {
            if (parent?.Children == null) return null;
            for (int i = 0; i < parent.Children.Count; i++)
                if (parent.Children[i].FieldId == fieldId)
                    return parent.Children[i];
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

        private static object? SafeDecode(IPatchSchema schema, int fieldId, byte[] bytes, IMetaSerializer serializer)
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
