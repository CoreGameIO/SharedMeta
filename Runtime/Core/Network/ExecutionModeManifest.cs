using System;
using System.Collections.Generic;

namespace SharedMeta.Core.Network
{
    /// <summary>
    /// JSON manifest model for execution mode overrides.
    /// Format: { "overrides": { "IServiceName.MethodAlias": "ServerPatch", ... } }
    /// </summary>
    public class ExecutionModeManifest
    {
        public Dictionary<string, string>? Overrides { get; set; }

        /// <summary>
        /// Parse manifest from JSON string.
        /// Minimal parser for Unity compatibility (no System.Text.Json dependency).
        /// </summary>
        public static ExecutionModeManifest Parse(string json)
        {
            var manifest = new ExecutionModeManifest();

            // Find "overrides" object
            var overridesIdx = json.IndexOf("\"overrides\"", StringComparison.OrdinalIgnoreCase);
            if (overridesIdx < 0) return manifest;

            // Find opening brace of overrides object
            var braceStart = json.IndexOf('{', overridesIdx);
            if (braceStart < 0) return manifest;

            // Find matching closing brace
            var braceEnd = FindMatchingBrace(json, braceStart);
            if (braceEnd < 0) return manifest;

            var overridesContent = json.Substring(braceStart + 1, braceEnd - braceStart - 1);
            manifest.Overrides = ParseKeyValuePairs(overridesContent);

            return manifest;
        }

        private static int FindMatchingBrace(string json, int openIndex)
        {
            int depth = 0;
            bool inString = false;
            for (int i = openIndex; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static Dictionary<string, string> ParseKeyValuePairs(string content)
        {
            var result = new Dictionary<string, string>();
            int pos = 0;

            while (pos < content.Length)
            {
                var key = ReadNextString(content, ref pos);
                if (key == null) break;

                // Skip colon
                SkipWhitespace(content, ref pos);
                if (pos >= content.Length || content[pos] != ':') break;
                pos++;

                var value = ReadNextString(content, ref pos);
                if (value == null) break;

                result[key] = value;

                // Skip comma
                SkipWhitespace(content, ref pos);
                if (pos < content.Length && content[pos] == ',')
                    pos++;
            }

            return result;
        }

        private static string? ReadNextString(string content, ref int pos)
        {
            // Find opening quote
            while (pos < content.Length && content[pos] != '"')
                pos++;
            if (pos >= content.Length) return null;
            pos++; // skip opening quote

            var start = pos;
            while (pos < content.Length && content[pos] != '"')
            {
                if (content[pos] == '\\') pos++; // skip escaped char
                pos++;
            }
            if (pos >= content.Length) return null;

            var value = content.Substring(start, pos - start);
            pos++; // skip closing quote
            return value;
        }

        private static void SkipWhitespace(string content, ref int pos)
        {
            while (pos < content.Length && char.IsWhiteSpace(content[pos]))
                pos++;
        }
    }
}
