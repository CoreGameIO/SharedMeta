using System.Collections.Generic;
using MemoryPack;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Distinguishes how a <see cref="PatchNode"/>'s <see cref="PatchNode.FieldId"/>
    /// should be interpreted by the receiver.
    /// </summary>
    public enum PatchChildKind : byte
    {
        /// <summary>
        /// The child addresses a property of its parent state by serialization id
        /// (<c>[MemoryPackOrder]</c> / <c>[Id]</c> / <c>[Key]</c>). This is the default
        /// for all classical state field nodes.
        /// </summary>
        Field = 0,

        /// <summary>
        /// The child addresses an element of a list-typed parent collection by its
        /// **current index** (after all <see cref="PatchNode.StructuralOps"/> on the parent
        /// collection node have been applied). The sender is responsible for keeping
        /// element children's <see cref="PatchNode.FieldId"/> in sync with the post-op
        /// indices via <see cref="PatchNode.ShiftElementChildren"/>.
        /// </summary>
        ElementByIndex = 1,
    }

    /// <summary>
    /// A node in the state diff tree.
    /// Terminal nodes contain the serialized value of a changed field.
    /// Non-terminal nodes represent partial changes (some sub-fields changed).
    /// Collection nodes additionally carry an ordered <see cref="StructuralOps"/> list
    /// describing structural mutations applied to the underlying list.
    /// </summary>
    [MemoryPackable]
    public partial class PatchNode
    {
        /// <summary>
        /// How this node's <see cref="FieldId"/> is interpreted. Defaults to
        /// <see cref="PatchChildKind.Field"/> for backward source compatibility with
        /// classical state-field nodes.
        /// </summary>
        [MemoryPackOrder(0)] public PatchChildKind Kind { get; set; }

        /// <summary>
        /// Field id from <c>[MemoryPackOrder(n)]</c> / <c>[Id(n)]</c> / <c>[Key(n)]</c> on
        /// the state property when <see cref="Kind"/> is <see cref="PatchChildKind.Field"/>;
        /// otherwise the current index of the addressed list element. Stored as <c>long</c>
        /// for forward compatibility with future kinds.
        /// Root node uses <see cref="FieldId"/> = -1.
        /// </summary>
        [MemoryPackOrder(1)] public long FieldId { get; set; }

        /// <summary>
        /// Serialized value. Non-null = terminal node (field was replaced entirely).
        /// </summary>
        [MemoryPackOrder(2)] public byte[]? Value { get; set; }

        /// <summary>
        /// Child patches. Non-null = non-terminal (only some sub-fields changed).
        /// For a list-typed collection field, children are element subtrees with
        /// <see cref="Kind"/> = <see cref="PatchChildKind.ElementByIndex"/>.
        /// </summary>
        [MemoryPackOrder(3)] public List<PatchNode>? Children { get; set; }

        /// <summary>
        /// Ordered list of structural mutations applied to a list-typed collection field.
        /// Only meaningful when this node represents a list-typed field (the receiver
        /// applies these via <see cref="CollectionPatchApplier"/> before processing
        /// element children). Null for all other node kinds.
        /// </summary>
        [MemoryPackOrder(4)] public List<PatchListOp>? StructuralOps { get; set; }

        /// <summary>Runtime-only: parent node for upward propagation.</summary>
        [MemoryPackIgnore] public PatchNode? Parent { get; set; }

        /// <summary>Runtime-only: whether this node or any descendant has changes.</summary>
        [MemoryPackIgnore] public bool HasChanges { get; set; }

        /// <summary>True if this node contains a serialized value (terminal).</summary>
        public bool IsTerminal => Value != null;

        [MemoryPackConstructor]
        public PatchNode() { }

        public PatchNode(long fieldId)
        {
            FieldId = fieldId;
        }

        public PatchNode(PatchChildKind kind, long fieldId)
        {
            Kind = kind;
            FieldId = fieldId;
        }

        /// <summary>
        /// Mark this node as terminal with the given serialized value.
        /// Clears children and propagates HasChanges up to the root.
        /// </summary>
        public void MarkTerminal(byte[] value)
        {
            Value = value;
            Children = null;
            StructuralOps = null;
            HasChanges = true;
            PropagateChanges();
        }

        /// <summary>
        /// Get or create a Field child with the given field id. Field children are
        /// distinguished from element children by <see cref="Kind"/>.
        /// </summary>
        public PatchNode GetOrCreateFieldChild(long fieldId)
        {
            if (Children != null)
            {
                for (int i = 0; i < Children.Count; i++)
                {
                    var c = Children[i];
                    if (c.Kind == PatchChildKind.Field && c.FieldId == fieldId)
                        return c;
                }
            }

            var child = new PatchNode(PatchChildKind.Field, fieldId) { Parent = this };
            Children ??= new List<PatchNode>();
            Children.Add(child);
            return child;
        }

        /// <summary>
        /// Get or create an Element-by-index child for the given list index. Element
        /// children sit alongside <see cref="StructuralOps"/> on a collection field's node.
        /// </summary>
        public PatchNode GetOrCreateElementChild(int index)
        {
            if (Children != null)
            {
                for (int i = 0; i < Children.Count; i++)
                {
                    var c = Children[i];
                    if (c.Kind == PatchChildKind.ElementByIndex && c.FieldId == index)
                        return c;
                }
            }

            var child = new PatchNode(PatchChildKind.ElementByIndex, index) { Parent = this };
            Children ??= new List<PatchNode>();
            Children.Add(child);
            return child;
        }

        /// <summary>
        /// Remove the element-by-index child for <paramref name="index"/> if present.
        /// Used by Set/RemoveAt/etc. on the sender side to drop in-place mutations of
        /// an element that no longer exists or is being wholesale-replaced.
        /// </summary>
        public bool RemoveElementChild(int index)
        {
            if (Children == null) return false;
            for (int i = 0; i < Children.Count; i++)
            {
                var c = Children[i];
                if (c.Kind == PatchChildKind.ElementByIndex && c.FieldId == index)
                {
                    Children.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Shift the indices of all element-by-index children whose current index is at
        /// or above <paramref name="fromIndex"/> by <paramref name="delta"/>. Called by the
        /// sender immediately before recording a structural op that reshapes the list, so
        /// the children's <see cref="FieldId"/> stays canonical for the post-op state.
        /// </summary>
        public void ShiftElementChildren(int fromIndex, int delta)
        {
            if (Children == null || delta == 0) return;
            for (int i = 0; i < Children.Count; i++)
            {
                var c = Children[i];
                if (c.Kind == PatchChildKind.ElementByIndex && c.FieldId >= fromIndex)
                    c.FieldId += delta;
            }
        }

        /// <summary>
        /// Append a structural op to this collection node. Allocates the list lazily and
        /// propagates HasChanges up to the root.
        /// </summary>
        public void AddStructuralOp(PatchListOp op)
        {
            StructuralOps ??= new List<PatchListOp>();
            StructuralOps.Add(op);
            HasChanges = true;
            PropagateChanges();
        }

        /// <summary>
        /// Drop ALL element children AND clear the structural op list. Used by Clear /
        /// FullReplace where the entire collection state is being thrown away.
        /// </summary>
        public void ClearCollectionState()
        {
            Children = null;
            StructuralOps = null;
        }

        /// <summary>
        /// Get or create a child node with the given field ID.
        /// Sets the Parent reference on the child.
        /// </summary>
        /// <remarks>
        /// Backwards-compat alias for <see cref="GetOrCreateFieldChild"/>. Existing
        /// callers used this method when only field children existed; element children
        /// are accessed via <see cref="GetOrCreateElementChild"/>.
        /// </remarks>
        public PatchNode GetOrCreateChild(long fieldId) => GetOrCreateFieldChild(fieldId);

        /// <summary>
        /// Get or create a child node and mark it as terminal with the given value.
        /// Used for classical field-by-id mutations.
        /// </summary>
        public void MarkChildTerminal(long fieldId, byte[] value)
        {
            var child = GetOrCreateFieldChild(fieldId);
            child.MarkTerminal(value);
        }

        /// <summary>
        /// Get or create a list-typed collection child and record a wholesale replacement
        /// as a <see cref="PatchListOpKind.FullReplace"/> structural op. Drops any prior
        /// element children / structural ops on that child first, since the entire
        /// collection state is being thrown away.
        ///
        /// Used by generated collection-field setters to keep the invariant
        /// "list nodes never have <see cref="Value"/>; replacement is just another op
        /// in the <see cref="StructuralOps"/> stream" — so subsequent in-place mutations
        /// (Add, indexer Set, etc.) chain after the replacement in submission order
        /// rather than being silently dropped by an early-return on <see cref="Value"/>.
        /// </summary>
        public void MarkChildFullReplace(long fieldId, byte[] packedList)
        {
            var child = GetOrCreateFieldChild(fieldId);
            child.ClearCollectionState();
            child.Value = null;
            child.AddStructuralOp(new PatchListOp
            {
                Kind = PatchListOpKind.FullReplace,
                ElementBytes = packedList,
            });
        }

        /// <summary>
        /// Walk up the Parent chain setting HasChanges = true.
        /// Stops early if a parent already has HasChanges set.
        /// </summary>
        public void PropagateChanges()
        {
            var p = Parent;
            while (p != null && !p.HasChanges)
            {
                p.HasChanges = true;
                p = p.Parent;
            }
        }

        /// <summary>
        /// Post-execution: recursively remove branches that have no changes.
        /// After pruning, only nodes with actual changes remain.
        /// </summary>
        public void Prune()
        {
            if (Children == null) return;

            for (int i = Children.Count - 1; i >= 0; i--)
            {
                if (!Children[i].HasChanges)
                {
                    Children.RemoveAt(i);
                }
                else
                {
                    Children[i].Prune();
                }
            }

            if (Children.Count == 0)
                Children = null;
        }
    }
}
