using MemoryPack;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Kind of structural mutation recorded in <see cref="PatchNode.StructuralOps"/> for
    /// list-typed fields. Phase 2 (granular list patches).
    /// </summary>
    public enum PatchListOpKind : byte
    {
        /// <summary>Default for serializers — should never appear on the wire.</summary>
        None = 0,

        /// <summary>Insert <see cref="PatchListOp.ElementBytes"/> at <see cref="PatchListOp.Index"/>.</summary>
        Insert = 1,

        /// <summary>Remove element at <see cref="PatchListOp.Index"/>.</summary>
        RemoveAt = 2,

        /// <summary>Replace element at <see cref="PatchListOp.Index"/> wholesale with <see cref="PatchListOp.ElementBytes"/>.</summary>
        Set = 3,

        /// <summary>Clear the entire list. <see cref="PatchListOp.Index"/> and <see cref="PatchListOp.ElementBytes"/> are ignored.</summary>
        Clear = 4,

        /// <summary>
        /// Replace the entire list with <see cref="PatchListOp.ElementBytes"/> (packed as
        /// <c>List&lt;TElement&gt;</c>). Used as a fallback for operations that are awkward
        /// to express granularly (Sort, Reverse, RemoveAll, AddRange, etc).
        /// </summary>
        FullReplace = 5,
    }

    /// <summary>
    /// One structural mutation on a list field, recorded by the generated
    /// <c>{Element}PatchableList</c> wrapper as the user code mutates the list. Multiple
    /// ops accumulate in <see cref="PatchNode.StructuralOps"/> in submission order, and
    /// the receiver applies them via <see cref="CollectionPatchApplier"/> in the same order.
    /// </summary>
    [MemoryPackable]
    public partial class PatchListOp
    {
        [MemoryPackOrder(0)] public PatchListOpKind Kind { get; set; }

        /// <summary>
        /// Index at the time the op was recorded. Sender keeps element children in sync
        /// by calling <see cref="PatchNode.ShiftElementChildren"/> whenever a structural op
        /// reshapes the list, so by the time the op is dispatched the index is canonical
        /// for the post-op state.
        /// </summary>
        [MemoryPackOrder(1)] public int Index { get; set; }

        /// <summary>
        /// Packed payload — full element bytes for <see cref="PatchListOpKind.Insert"/>,
        /// <see cref="PatchListOpKind.Set"/> and <see cref="PatchListOpKind.FullReplace"/>;
        /// null otherwise.
        /// </summary>
        [MemoryPackOrder(2)] public byte[]? ElementBytes { get; set; }
    }
}
