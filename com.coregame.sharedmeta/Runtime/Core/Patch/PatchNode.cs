using System.Collections.Generic;
using MemoryPack;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// A node in the state diff tree.
    /// Terminal nodes contain the serialized value of a changed field.
    /// Non-terminal nodes represent partial changes (some sub-fields changed).
    /// </summary>
    [MemoryPackable]
    public partial class PatchNode
    {
        /// <summary>
        /// Field ID from [Id(n)] attribute on the state property.
        /// Root node uses FieldId = -1.
        /// </summary>
        [MemoryPackOrder(0)] public int FieldId { get; set; }

        /// <summary>
        /// Serialized value. Non-null = terminal node (field was replaced entirely).
        /// </summary>
        [MemoryPackOrder(1)] public byte[]? Value { get; set; }

        /// <summary>
        /// Child patches. Non-null = non-terminal (only some sub-fields changed).
        /// </summary>
        [MemoryPackOrder(2)] public List<PatchNode>? Children { get; set; }

        /// <summary>Runtime-only: parent node for upward propagation.</summary>
        [MemoryPackIgnore] public PatchNode? Parent { get; set; }

        /// <summary>Runtime-only: whether this node or any descendant has changes.</summary>
        [MemoryPackIgnore] public bool HasChanges { get; set; }

        /// <summary>True if this node contains a serialized value (terminal).</summary>
        public bool IsTerminal => Value != null;

        [MemoryPackConstructor]
        public PatchNode() { }

        public PatchNode(int fieldId)
        {
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
            HasChanges = true;
            PropagateChanges();
        }

        /// <summary>
        /// Get or create a child node with the given field ID.
        /// Sets the Parent reference on the child.
        /// </summary>
        public PatchNode GetOrCreateChild(int fieldId)
        {
            if (Children != null)
            {
                for (int i = 0; i < Children.Count; i++)
                {
                    if (Children[i].FieldId == fieldId)
                        return Children[i];
                }
            }

            var child = new PatchNode(fieldId) { Parent = this };
            Children ??= new List<PatchNode>();
            Children.Add(child);
            return child;
        }

        /// <summary>
        /// Get or create a child node and mark it as terminal with the given value.
        /// </summary>
        public void MarkChildTerminal(int fieldId, byte[] value)
        {
            var child = GetOrCreateChild(fieldId);
            child.MarkTerminal(value);
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
