using System;
using System.Collections.Generic;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Runtime helper used by generated <c>{State}PatchApplier</c> classes to apply a
    /// collection-field <see cref="PatchNode"/> to a target list.
    ///
    /// Phase 2 (granular list patches) — the patch is interpreted as one of:
    /// <list type="bullet">
    ///   <item>Terminal value (<see cref="PatchNode.Value"/> non-null) → full list replace</item>
    ///   <item>Structural ops in <see cref="PatchNode.StructuralOps"/> applied in order</item>
    ///   <item>Element-by-index sub-tree mutations in <see cref="PatchNode.Children"/></item>
    /// </list>
    /// All three may be present in the same patch (typically structural ops + element
    /// mutations after a sequence of mixed operations on the sender side).
    /// </summary>
    public static class CollectionPatchApplier
    {
        /// <summary>
        /// Apply a collection patch to <paramref name="list"/>.
        /// </summary>
        /// <param name="list">The target list (will be mutated in place).</param>
        /// <param name="collectionNode">The patch node representing the collection field.</param>
        /// <param name="serializer">Serializer used to unpack element bytes.</param>
        /// <param name="unpackElement">
        /// Element unpacker — generator passes a delegate specialized to <typeparamref name="T"/>
        /// to avoid boxing and to let MemoryPack pick the right formatter.
        /// </param>
        /// <param name="unpackList">
        /// Full-list unpacker for the terminal / FullReplace path. Generator passes a delegate
        /// that calls <c>serializer.Unpack&lt;List&lt;T&gt;&gt;(...)</c>.
        /// </param>
        /// <param name="applyElementSubtree">
        /// Optional callback to recursively apply an element child's sub-tree to the
        /// element at the given index. Pass null when the element type is not sub-wrappable
        /// (scalar / no <c>[Id]</c> properties) — element children won't be present in that case.
        /// </param>
        public static void Apply<T>(
            IList<T> list,
            PatchNode collectionNode,
            IMetaSerializer serializer,
            Func<byte[], T> unpackElement,
            Func<byte[], List<T>> unpackList,
            Action<T, PatchNode>? applyElementSubtree = null)
        {
            // 1. Legacy terminal-Value path → full replace via packed List<T>.
            //    Generated wrappers no longer write Value for list fields (they record
            //    a FullReplace structural op instead, so order is preserved relative to
            //    subsequent mutations). Kept here as defense-in-depth so a mixed
            //    Value+ops node still applies correctly: Value is treated as the initial
            //    state, then structural ops chain on top in submission order.
            if (collectionNode.Value is { Length: > 0 })
            {
                ReplaceListContents(list, unpackList(collectionNode.Value));
                // Fall through — continue applying any structural ops / element children.
            }

            // 2. Apply structural ops in order. Each op was recorded by the sender at the
            //    index canonical to the post-prior-ops state, so applying them sequentially
            //    on the receiver reproduces the sender's final list shape.
            if (collectionNode.StructuralOps != null)
            {
                foreach (var op in collectionNode.StructuralOps)
                {
                    switch (op.Kind)
                    {
                        case PatchListOpKind.Insert:
                            list.Insert(op.Index, unpackElement(op.ElementBytes!));
                            break;
                        case PatchListOpKind.RemoveAt:
                            list.RemoveAt(op.Index);
                            break;
                        case PatchListOpKind.Set:
                            list[op.Index] = unpackElement(op.ElementBytes!);
                            break;
                        case PatchListOpKind.Clear:
                            list.Clear();
                            break;
                        case PatchListOpKind.FullReplace:
                            ReplaceListContents(list, unpackList(op.ElementBytes!));
                            break;
                    }
                }
            }

            // 3. Apply element subtree mutations. Sender already shifted these children's
            //    indices in lock-step with structural ops, so the FieldId here is the
            //    final post-op index in the now-up-to-date list.
            if (applyElementSubtree != null && collectionNode.Children != null)
            {
                foreach (var child in collectionNode.Children)
                {
                    if (child.Kind != PatchChildKind.ElementByIndex) continue;
                    var idx = (int)child.FieldId;
                    if (idx < 0 || idx >= list.Count) continue;
                    applyElementSubtree(list[idx], child);
                }
            }
        }

        private static void ReplaceListContents<T>(IList<T> target, List<T> source)
        {
            target.Clear();
            for (int i = 0; i < source.Count; i++) target.Add(source[i]);
        }
    }
}
