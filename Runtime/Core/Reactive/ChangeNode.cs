using System;
using System.Collections.Generic;

namespace SharedMeta.Core.Reactive
{
    /// <summary>
    /// Kind of value stored in <see cref="ChangeValue"/>.
    /// </summary>
    public enum ChangeValueKind : byte
    {
        None = 0,
        Int,
        Long,
        Float,
        Double,
        Bool,
        String,
        Object
    }

    /// <summary>
    /// Discriminated union for change values. Avoids boxing for common primitive types.
    /// </summary>
    public struct ChangeValue
    {
        public ChangeValueKind Kind;
        public int IntValue;
        public long LongValue;
        public float FloatValue;
        public double DoubleValue;
        public bool BoolValue;
        public string? StringValue;
        public object? ObjectValue;

        public static ChangeValue From(int v) => new() { Kind = ChangeValueKind.Int, IntValue = v };
        public static ChangeValue From(long v) => new() { Kind = ChangeValueKind.Long, LongValue = v };
        public static ChangeValue From(float v) => new() { Kind = ChangeValueKind.Float, FloatValue = v };
        public static ChangeValue From(double v) => new() { Kind = ChangeValueKind.Double, DoubleValue = v };
        public static ChangeValue From(bool v) => new() { Kind = ChangeValueKind.Bool, BoolValue = v };
        public static ChangeValue From(string? v) => new() { Kind = ChangeValueKind.String, StringValue = v };
        public static ChangeValue FromObject(object? v) => new() { Kind = ChangeValueKind.Object, ObjectValue = v };

        public override string ToString()
        {
            return Kind switch
            {
                ChangeValueKind.Int => IntValue.ToString(),
                ChangeValueKind.Long => LongValue.ToString(),
                ChangeValueKind.Float => FloatValue.ToString(),
                ChangeValueKind.Double => DoubleValue.ToString(),
                ChangeValueKind.Bool => BoolValue.ToString(),
                ChangeValueKind.String => StringValue ?? "null",
                ChangeValueKind.Object => ObjectValue?.ToString() ?? "null",
                _ => "none"
            };
        }
    }

    /// <summary>
    /// Struct node stored in a pooled flat list. Forms a tree via child indices.
    /// Branch nodes (ChildCount > 0) represent traversal through nested objects/collections.
    /// Leaf nodes (ChildCount == 0) represent actual field value changes.
    /// </summary>
    public struct ChangeNode
    {
        /// <summary>Which field this node represents (unified TrackingProperty enum value).</summary>
        public int Field;

        /// <summary>-1 if not a collection element, otherwise the index in the collection.</summary>
        public int CollectionIndex;

        /// <summary>Set only on leaf nodes — the value before the change.</summary>
        public ChangeValue OldValue;

        /// <summary>Set only on leaf nodes — the value after the change.</summary>
        public ChangeValue NewValue;

        /// <summary>Index into the same List where children begin. -1 if leaf.</summary>
        public int ChildStartIndex;

        /// <summary>Number of children. 0 means this is a leaf node.</summary>
        public int ChildCount;

        /// <summary>Reference to the root object that owns this change tree.</summary>
        public object? Root;

        /// <summary>Type ID of the root object (for dispatch to correct subscriber).</summary>
        public int RootTypeId;

        public bool IsLeaf => ChildCount == 0;
        public bool IsBranch => ChildCount > 0;
    }
}
