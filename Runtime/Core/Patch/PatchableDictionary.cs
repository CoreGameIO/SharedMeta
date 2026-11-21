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

        /// <summary>Implicit conversion from Dictionary for assignment compatibility in PatchWrapper setters.</summary>
        public static implicit operator PatchableDictionary<TKey, TValue>(Dictionary<TKey, TValue> dict) => new(dict, null, 0, null);

        private void MarkChanged()
        {
            if (_parentNode != null && _serializer != null)
                _parentNode.MarkChildTerminal(_fieldId, _serializer.Pack(_dict).ToArray());
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

        public bool TryAdd(TKey key, TValue value)
        {
#if NETSTANDARD2_1
            if (_dict.ContainsKey(key)) return false;
            _dict.Add(key, value);
            MarkChanged();
            return true;
#else
            var added = _dict.TryAdd(key, value);
            if (added) MarkChanged();
            return added;
#endif
        }

        // === Read-only operations (no marking) ===

        public int Count => _dict.Count;
        public bool IsReadOnly => false;
        public ICollection<TKey> Keys => _dict.Keys;
        public ICollection<TValue> Values => _dict.Values;
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _dict.Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _dict.Values;

        public bool ContainsKey(TKey key) => _dict.ContainsKey(key);
        public bool ContainsValue(TValue value) => _dict.ContainsValue(value);
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_dict).Contains(item);

        public bool TryGetValue(TKey key,
#if !NETSTANDARD2_1
            [MaybeNullWhen(false)]
#endif
            out TValue value) => _dict.TryGetValue(key, out value!);

#if !NETSTANDARD2_1
        public TValue? GetValueOrDefault(TKey key) => _dict.GetValueOrDefault(key);
        public TValue GetValueOrDefault(TKey key, TValue defaultValue) => _dict.GetValueOrDefault(key, defaultValue);
        public int EnsureCapacity(int capacity) => _dict.EnsureCapacity(capacity);
        public void TrimExcess() => _dict.TrimExcess();
        public void TrimExcess(int capacity) => _dict.TrimExcess(capacity);
#endif

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) =>
            ((ICollection<KeyValuePair<TKey, TValue>>)_dict).CopyTo(array, arrayIndex);

        public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _dict.GetEnumerator();
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => _dict.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _dict.GetEnumerator();
    }
}
