using System;
using System.Collections;
using System.Collections.Generic;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Wraps a HashSet&lt;T&gt; with automatic change tracking for ServerPatch mode.
    /// All mutating operations auto-mark the field as dirty when tracking is active.
    /// </summary>
    public class PatchableHashSet<T> : ISet<T>, IReadOnlyCollection<T>
    {
        private readonly HashSet<T> _set;
        private readonly PatchNode? _parentNode;
        private readonly int _fieldId;
        private readonly IMetaSerializer? _serializer;

        public PatchableHashSet(HashSet<T> set, PatchNode? parentNode, int fieldId, IMetaSerializer? serializer)
        {
            _set = set;
            _parentNode = parentNode;
            _fieldId = fieldId;
            _serializer = serializer;
        }

        /// <summary>Explicitly mark this collection as dirty.</summary>
        public void SetDirty()
        {
            MarkChanged();
        }

        /// <summary>Access the underlying HashSet directly.</summary>
        public HashSet<T> Inner => _set;

        /// <summary>Implicit conversion from HashSet for assignment compatibility in PatchWrapper setters.</summary>
        public static implicit operator PatchableHashSet<T>(HashSet<T> set) => new(set, null, 0, null);

        private void MarkChanged()
        {
            if (_parentNode != null && _serializer != null)
                _parentNode.MarkChildTerminal(_fieldId, _serializer.Pack(_set));
        }

        // === Mutating operations (auto-mark dirty) ===

        public bool Add(T item)
        {
            var added = _set.Add(item);
            if (added) MarkChanged();
            return added;
        }

        void ICollection<T>.Add(T item)
        {
            if (_set.Add(item)) MarkChanged();
        }

        public bool Remove(T item)
        {
            var removed = _set.Remove(item);
            if (removed) MarkChanged();
            return removed;
        }

        public void Clear()
        {
            _set.Clear();
            MarkChanged();
        }

        public void UnionWith(IEnumerable<T> other)
        {
            _set.UnionWith(other);
            MarkChanged();
        }

        public void IntersectWith(IEnumerable<T> other)
        {
            _set.IntersectWith(other);
            MarkChanged();
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            _set.ExceptWith(other);
            MarkChanged();
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            _set.SymmetricExceptWith(other);
            MarkChanged();
        }

        public int RemoveWhere(Predicate<T> match)
        {
            var removed = _set.RemoveWhere(match);
            if (removed > 0) MarkChanged();
            return removed;
        }

        // === Read-only operations (no marking) ===

        public int Count => _set.Count;
        public bool IsReadOnly => false;

        public bool Contains(T item) => _set.Contains(item);
        public bool IsSubsetOf(IEnumerable<T> other) => _set.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<T> other) => _set.IsSupersetOf(other);
        public bool IsProperSupersetOf(IEnumerable<T> other) => _set.IsProperSupersetOf(other);
        public bool IsProperSubsetOf(IEnumerable<T> other) => _set.IsProperSubsetOf(other);
        public bool Overlaps(IEnumerable<T> other) => _set.Overlaps(other);
        public bool SetEquals(IEnumerable<T> other) => _set.SetEquals(other);
        public void CopyTo(T[] array, int arrayIndex) => _set.CopyTo(array, arrayIndex);
        public void TrimExcess() => _set.TrimExcess();
#if !NETSTANDARD2_1
        public int EnsureCapacity(int capacity) => _set.EnsureCapacity(capacity);
#endif

        public HashSet<T>.Enumerator GetEnumerator() => _set.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _set.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _set.GetEnumerator();
    }
}
