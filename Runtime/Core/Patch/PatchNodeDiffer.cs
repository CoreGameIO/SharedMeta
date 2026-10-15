using System;
using System.Collections.Generic;
using System.Linq;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core.Patch
{
    public enum PatchDiffType
    {
        Modified,    // Both have the field but values differ
        OnlyServer,  // Server changed this field, client didn't
        OnlyClient   // Client changed this field, server didn't
    }

    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class PatchDiffEntry
    {
        /// <summary>Field path as dot-separated field IDs, e.g. "3.1.0".</summary>
        [Id(0), Key(0)] public string FieldPath { get; set; } = "";
        [Id(1), Key(1)] public byte[]? ServerValue { get; set; }
        [Id(2), Key(2)] public byte[]? ClientValue { get; set; }
        [Id(3), Key(3)] public PatchDiffType Type { get; set; }
    }

    /// <summary>
    /// Compares two PatchNode trees field-by-field and returns a list of differences.
    /// Used for deep desync reporting.
    /// </summary>
    public static class PatchNodeDiffer
    {
        public static List<PatchDiffEntry> Compare(PatchNode? server, PatchNode? client)
        {
            var results = new List<PatchDiffEntry>();
            CompareRecursive(server, client, "", results);
            return results;
        }

        private static void CompareRecursive(PatchNode? server, PatchNode? client, string path, List<PatchDiffEntry> results)
        {
            if (server == null && client == null) return;

            // One side has no node at all
            if (server == null)
            {
                CollectAll(client!, path, PatchDiffType.OnlyClient, results);
                return;
            }
            if (client == null)
            {
                CollectAll(server, path, PatchDiffType.OnlyServer, results);
                return;
            }

            // Both are terminal — compare values
            if (server.Value != null && client.Value != null)
            {
                if (!server.Value.AsSpan().SequenceEqual(client.Value))
                {
                    results.Add(new PatchDiffEntry
                    {
                        FieldPath = path,
                        ServerValue = server.Value,
                        ClientValue = client.Value,
                        Type = PatchDiffType.Modified
                    });
                }
                return;
            }

            // One terminal, one has children — structural difference
            if (server.Value != null && client.Children != null)
            {
                results.Add(new PatchDiffEntry
                {
                    FieldPath = path,
                    ServerValue = server.Value,
                    Type = PatchDiffType.Modified
                });
                return;
            }
            if (client.Value != null && server.Children != null)
            {
                results.Add(new PatchDiffEntry
                {
                    FieldPath = path,
                    ClientValue = client.Value,
                    Type = PatchDiffType.Modified
                });
                return;
            }

            // Both have children — recurse by (Kind, FieldId).
            // Field children and element-by-index children must not collide on the
            // same dictionary key when both kinds are present in a collection node.
            var serverChildren = server.Children ?? new List<PatchNode>();
            var clientChildren = client.Children ?? new List<PatchNode>();

            var serverMap = serverChildren.ToDictionary(c => MakeChildKey(c));
            var clientMap = clientChildren.ToDictionary(c => MakeChildKey(c));

            var allKeys = new HashSet<(PatchChildKind, long)>(serverMap.Keys);
            allKeys.UnionWith(clientMap.Keys);

            foreach (var key in allKeys)
            {
                var label = key.Item1 == PatchChildKind.ElementByIndex
                    ? "[" + key.Item2 + "]"
                    : key.Item2.ToString();
                var childPath = string.IsNullOrEmpty(path) ? label : $"{path}.{label}";
                serverMap.TryGetValue(key, out var sChild);
                clientMap.TryGetValue(key, out var cChild);
                CompareRecursive(sChild, cChild, childPath, results);
            }
        }

        private static (PatchChildKind, long) MakeChildKey(PatchNode node) => (node.Kind, node.FieldId);

        private static void CollectAll(PatchNode node, string path, PatchDiffType type, List<PatchDiffEntry> results)
        {
            var nodePath = string.IsNullOrEmpty(path) ? node.FieldId.ToString() : path;

            if (node.Value != null)
            {
                results.Add(new PatchDiffEntry
                {
                    FieldPath = nodePath,
                    ServerValue = type == PatchDiffType.OnlyServer ? node.Value : null,
                    ClientValue = type == PatchDiffType.OnlyClient ? node.Value : null,
                    Type = type
                });
                return;
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    var childPath = $"{nodePath}.{child.FieldId}";
                    CollectAll(child, childPath, type, results);
                }
            }
        }
    }
}
