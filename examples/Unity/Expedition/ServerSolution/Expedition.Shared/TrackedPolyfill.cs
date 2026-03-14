// Polyfill for [Tracked] and ChangeTracker types — not yet published in NuGet 0.3.4.
// The UPM package already has these. Server doesn't need tracking, just needs compilation.
namespace SharedMeta.Core
{
    [System.AttributeUsage(System.AttributeTargets.Field)]
    public class TrackedAttribute : System.Attribute { }
}

namespace SharedMeta.Core.Reactive
{
    public readonly struct ChangeValue
    {
        public readonly int IntValue;
        public readonly long LongValue;
        public readonly float FloatValue;
        public readonly double DoubleValue;
        public readonly bool BoolValue;
        public readonly string? StringValue;
        public readonly object? ObjectValue;

        private ChangeValue(int v) { IntValue = v; LongValue = 0; FloatValue = 0; DoubleValue = 0; BoolValue = false; StringValue = null; ObjectValue = null; }
        private ChangeValue(long v) { IntValue = 0; LongValue = v; FloatValue = 0; DoubleValue = 0; BoolValue = false; StringValue = null; ObjectValue = null; }
        private ChangeValue(float v) { IntValue = 0; LongValue = 0; FloatValue = v; DoubleValue = 0; BoolValue = false; StringValue = null; ObjectValue = null; }
        private ChangeValue(double v) { IntValue = 0; LongValue = 0; FloatValue = 0; DoubleValue = v; BoolValue = false; StringValue = null; ObjectValue = null; }
        private ChangeValue(bool v) { IntValue = 0; LongValue = 0; FloatValue = 0; DoubleValue = 0; BoolValue = v; StringValue = null; ObjectValue = null; }
        private ChangeValue(string? v) { IntValue = 0; LongValue = 0; FloatValue = 0; DoubleValue = 0; BoolValue = false; StringValue = v; ObjectValue = null; }
        private ChangeValue(object? v) { IntValue = 0; LongValue = 0; FloatValue = 0; DoubleValue = 0; BoolValue = false; StringValue = null; ObjectValue = v; }

        public static ChangeValue From(int v) => new(v);
        public static ChangeValue From(long v) => new(v);
        public static ChangeValue From(float v) => new(v);
        public static ChangeValue From(double v) => new(v);
        public static ChangeValue From(bool v) => new(v);
        public static ChangeValue From(string? v) => new(v);
        public static ChangeValue FromObject(object? v) => new(v);
    }

    public class ChangeNode
    {
        public object? Root { get; set; }
        public int TypeId { get; set; }
        public int FieldId { get; set; }
        public ChangeValue OldValue { get; set; }
        public ChangeValue NewValue { get; set; }
    }

    public readonly struct ChangeTreeArgs
    {
        public readonly object? Root;
        public readonly System.Collections.Generic.IReadOnlyList<ChangeNode> Changes;

        public ChangeTreeArgs(object? root, System.Collections.Generic.IReadOnlyList<ChangeNode> changes)
        {
            Root = root;
            Changes = changes;
        }

        public bool HasChange(int fieldId)
        {
            for (int i = 0; i < Changes.Count; i++)
                if (Changes[i].FieldId == fieldId) return true;
            return false;
        }

        public ChangeNode? FindLeaf(int fieldId)
        {
            for (int i = 0; i < Changes.Count; i++)
                if (Changes[i].FieldId == fieldId) return Changes[i];
            return null;
        }
    }

    public class ChangeTracker
    {
        [System.ThreadStatic] private static ChangeTracker? _current;
        public static ChangeTracker? Current
        {
            get => _current;
            set => _current = value;
        }

        public static ChangeTracker Activate() { var t = new ChangeTracker(); _current = t; return t; }
        public void FlushAndNotify() { _current = null; }
        public void Discard() { _current = null; }

        public void RecordFieldChange(object root, int typeId, int fieldId, ChangeValue oldValue, ChangeValue newValue) { }

        private static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<System.Action<ChangeTreeArgs>>> _subs = new();

        public static void Subscribe(int typeId, System.Action<ChangeTreeArgs> handler)
        {
            if (!_subs.TryGetValue(typeId, out var list))
            {
                list = new System.Collections.Generic.List<System.Action<ChangeTreeArgs>>();
                _subs[typeId] = list;
            }
            list.Add(handler);
        }

        public static void Unsubscribe(int typeId, System.Action<ChangeTreeArgs> handler)
        {
            if (_subs.TryGetValue(typeId, out var list))
                list.Remove(handler);
        }
    }
}
