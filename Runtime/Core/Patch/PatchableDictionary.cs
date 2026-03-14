using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Wraps a Dictionary&lt;TKey, TValue&gt; with automatic change tracking for ServerPatch mode.
    /// All mutating operations auto-mark the field as dirty when tracking is active.
    /// </summary>
    public class PatchableDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _dict;
        private readonly PatchNode? _parentNode;
        private readonly int _fieldId;
        private readonly IMetaSerializer? _serializer;

        public PatchableDictionary(Dictionary<TKey, TValue> dict, PatchNode? parentNode, int fieldId, IMetaSerializer? serializer)
        {
            _dict = dict;
            _parentNode = parentNode;
            _fieldId = fieldId;
            _serializer = serializer;
        }

        /// <summary>Explicitly mark this collection as dirty.</summary>
        public void SetDirty()
        {
            MarkChanged();
        }

        /// <summary>Access the underlying Dictionary directly.</summary>
        public Dictionary<TKey, TValue> Inner => _dict;

        private void MarkChanged()
        {
            if (_parentNode != null && _serializer != null)
                _parentNode.MarkChildTerminal(_fieldId, _serializer.Pack(_dict));
        }

        // === Mutating operations (auto-mark dirty) ===

        public TValue this[TKey key]
        {
            get => _dict[key];
            set { _dict[key] = value; MarkChanged(); }
        }

        public void Add(TKey key, TValue value)
        {
            _dict.Add(key, value);
            MarkChanged();
        }

        public void Add(KeyValuePair<TKey, TValue> item)
        {
            ((ICollection<KeyValuePair<TKey, TValue>>)_dict).Add(item);
            MarkChanged();
        }

        public bool Remove(TKey key)
        {
            var removed = _dict.Remove(key);
            if (removed) MarkChanged();
            return removed;
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            var removed = ((ICollection<KeyValuePair<TKey, TValue>>)_dict).Remove(item);
            if (removed) MarkChanged();
            return removed;
        }

        public void Clear()
        {
            _dict.Clear();
            MarkChanged();
        }

        // === Read-only operations (no marking) ===

        public int Count => _dict.Count;
        public bool IsReadOnly => false;
        public ICollection<TKey> Keys => _dict.Keys;
        public ICollection<TValue> Values => _dict.Values;
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _dict.Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _dict.Values;

        public bool ContainsKey(TKey key) => _dict.ContainsKey(key);
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_dict).Contains(item);

        public bool TryGetValue(TKey key,
#if !NETSTANDARD2_1
            [MaybeNullWhen(false)]
#endif
            out TValue value) => _dict.TryGetValue(key, out value!);

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_dict).CopyTo(array, arrayIndex);

        public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _dict.GetEnumerator();
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => _dict.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _dict.GetEnumerator();
    }
}
