using System;
using System.Collections;
using System.Collections.Generic;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Wraps a List&lt;T&gt; with automatic change tracking for ServerPatch mode.
    /// All mutating operations auto-mark the field as dirty when tracking is active.
    /// Implements IList&lt;T&gt; for transparent use as a list.
    /// </summary>
    public class PatchableList<T> : IList<T>, IReadOnlyList<T>
    {
        private readonly List<T> _list;
        private readonly PatchNode? _parentNode;
        private readonly int _fieldId;
        private readonly IMetaSerializer? _serializer;

        public PatchableList(List<T> list, PatchNode? parentNode, int fieldId, IMetaSerializer? serializer)
        {
            _list = list;
            _parentNode = parentNode;
            _fieldId = fieldId;
            _serializer = serializer;
        }

        /// <summary>
        /// Explicitly mark this collection as dirty.
        /// Useful after mutating through Raw or external references.
        /// </summary>
        public void SetDirty()
        {
            MarkChanged();
        }

        /// <summary>Access the underlying List directly.</summary>
        public List<T> Inner => _list;

        private void MarkChanged()
        {
            if (_parentNode != null && _serializer != null)
                _parentNode.MarkChildTerminal(_fieldId, _serializer.Pack(_list));
        }

        // === Mutating operations (auto-mark dirty) ===

        public T this[int index]
        {
            get => _list[index];
            set { _list[index] = value; MarkChanged(); }
        }

        public void Add(T item)
        {
            _list.Add(item);
            MarkChanged();
        }

        public void Insert(int index, T item)
        {
            _list.Insert(index, item);
            MarkChanged();
        }

        public bool Remove(T item)
        {
            var removed = _list.Remove(item);
            if (removed) MarkChanged();
            return removed;
        }

        public void RemoveAt(int index)
        {
            _list.RemoveAt(index);
            MarkChanged();
        }

        public void Clear()
        {
            _list.Clear();
            MarkChanged();
        }

        public void AddRange(IEnumerable<T> collection)
        {
            _list.AddRange(collection);
            MarkChanged();
        }

        public void RemoveAll(Predicate<T> match)
        {
            var removed = _list.RemoveAll(match);
            if (removed > 0) MarkChanged();
        }

        public void Sort()
        {
            _list.Sort();
            MarkChanged();
        }

        public void Sort(Comparison<T> comparison)
        {
            _list.Sort(comparison);
            MarkChanged();
        }

        public void Sort(IComparer<T>? comparer)
        {
            _list.Sort(comparer);
            MarkChanged();
        }

        public void Reverse()
        {
            _list.Reverse();
            MarkChanged();
        }

        // === Read-only operations (no marking) ===

        public int Count => _list.Count;
        public bool IsReadOnly => false;

        public bool Contains(T item) => _list.Contains(item);
        public int IndexOf(T item) => _list.IndexOf(item);
        public void CopyTo(T[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);
        public List<T>.Enumerator GetEnumerator() => _list.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();
    }
}
