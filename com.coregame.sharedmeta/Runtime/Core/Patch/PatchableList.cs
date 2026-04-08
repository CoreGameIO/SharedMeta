using System;
using System.Collections;
using System.Collections.Generic;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Wraps a List&lt;T&gt; with automatic change tracking. Records mutations as
    /// granular structural ops on a parent <see cref="PatchNode"/> (Phase 2: granular
    /// list patches), so a single mutation no longer dumps the entire collection.
    ///
    /// Used for list fields whose element type is **not** sub-wrappable (no
    /// <c>[Id]</c>/<c>[MemoryPackOrder]</c> properties on the element). For element-
    /// sub-wrappable lists the generator emits a specialized
    /// <c>{Element}PatchableList</c> nested class that additionally hands out
    /// per-element <c>{Element}PatchWrapper</c> instances bound to element children.
    ///
    /// All mutating operations are O(1) on the patch tree (one structural op append).
    /// Sort / Reverse / RemoveAll fall back to <see cref="PatchListOpKind.FullReplace"/>.
    /// </summary>
    public class PatchableList<T> : IList<T>, IReadOnlyList<T>
    {
        private readonly List<T> _list;
        private readonly PatchNode? _collectionNode;
        private readonly IMetaSerializer? _serializer;

        public PatchableList(List<T> list, PatchNode? parentNode, int fieldId, IMetaSerializer? serializer)
        {
            _list = list;
            _serializer = serializer;
            // The collection's own node lives under the parent, addressed by fieldId.
            // We capture it at construction so subsequent ops can append structural ops
            // directly without re-resolving via the parent.
            _collectionNode = parentNode?.GetOrCreateFieldChild(fieldId);
        }

        /// <summary>
        /// Explicitly mark this collection as dirty by emitting a FullReplace op.
        /// Useful after mutating through Inner or external references.
        /// </summary>
        public void SetDirty()
        {
            EmitFullReplace();
        }

        /// <summary>Access the underlying List directly.</summary>
        public List<T> Inner => _list;

        /// <summary>Implicit conversion from List for assignment compatibility in PatchWrapper setters.</summary>
        public static implicit operator PatchableList<T>(List<T> list) => new(list, null, 0, null);

        // === Op emission ===

        private bool TrackingEnabled => _collectionNode != null && _serializer != null;

        private void EmitInsert(int index, T item)
        {
            if (!TrackingEnabled) return;
            _collectionNode!.AddStructuralOp(new PatchListOp
            {
                Kind = PatchListOpKind.Insert,
                Index = index,
                ElementBytes = _serializer!.Pack(item),
            });
        }

        private void EmitRemoveAt(int index)
        {
            if (!TrackingEnabled) return;
            _collectionNode!.AddStructuralOp(new PatchListOp
            {
                Kind = PatchListOpKind.RemoveAt,
                Index = index,
            });
        }

        private void EmitSet(int index, T item)
        {
            if (!TrackingEnabled) return;
            _collectionNode!.AddStructuralOp(new PatchListOp
            {
                Kind = PatchListOpKind.Set,
                Index = index,
                ElementBytes = _serializer!.Pack(item),
            });
        }

        private void EmitClear()
        {
            if (!TrackingEnabled) return;
            _collectionNode!.ClearCollectionState();
            _collectionNode.AddStructuralOp(new PatchListOp { Kind = PatchListOpKind.Clear });
        }

        private void EmitFullReplace()
        {
            if (!TrackingEnabled) return;
            _collectionNode!.ClearCollectionState();
            _collectionNode.AddStructuralOp(new PatchListOp
            {
                Kind = PatchListOpKind.FullReplace,
                ElementBytes = _serializer!.Pack(_list),
            });
        }

        // === Mutating operations (auto-record granular ops) ===

        public T this[int index]
        {
            get => _list[index];
            set
            {
                _list[index] = value;
                EmitSet(index, value);
            }
        }

        public void Add(T item)
        {
            _list.Add(item);
            EmitInsert(_list.Count - 1, item);
        }

        public void Insert(int index, T item)
        {
            _list.Insert(index, item);
            EmitInsert(index, item);
        }

        public bool Remove(T item)
        {
            var idx = _list.IndexOf(item);
            if (idx < 0) return false;
            _list.RemoveAt(idx);
            EmitRemoveAt(idx);
            return true;
        }

        public void RemoveAt(int index)
        {
            _list.RemoveAt(index);
            EmitRemoveAt(index);
        }

        public void Clear()
        {
            _list.Clear();
            EmitClear();
        }

        public void AddRange(IEnumerable<T> collection)
        {
            _list.AddRange(collection);
            EmitFullReplace();
        }

        public void RemoveAll(Predicate<T> match)
        {
            var removed = _list.RemoveAll(match);
            if (removed > 0) EmitFullReplace();
        }

        public void Sort()
        {
            _list.Sort();
            EmitFullReplace();
        }

        public void Sort(Comparison<T> comparison)
        {
            _list.Sort(comparison);
            EmitFullReplace();
        }

        public void Sort(IComparer<T>? comparer)
        {
            _list.Sort(comparer);
            EmitFullReplace();
        }

        public void Reverse()
        {
            _list.Reverse();
            EmitFullReplace();
        }

        public void InsertRange(int index, IEnumerable<T> collection)
        {
            _list.InsertRange(index, collection);
            EmitFullReplace();
        }

        public void RemoveRange(int index, int count)
        {
            _list.RemoveRange(index, count);
            EmitFullReplace();
        }

        public void Reverse(int index, int count)
        {
            _list.Reverse(index, count);
            EmitFullReplace();
        }

        public void Sort(int index, int count, IComparer<T>? comparer)
        {
            _list.Sort(index, count, comparer);
            EmitFullReplace();
        }

        // === Read-only operations (no recording) ===

        public int Count => _list.Count;
        public bool IsReadOnly => false;
        public int Capacity { get => _list.Capacity; set => _list.Capacity = value; }

        public bool Contains(T item) => _list.Contains(item);
        public int IndexOf(T item) => _list.IndexOf(item);
        public void CopyTo(T[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);

        public T? Find(Predicate<T> match) => _list.Find(match);
        public T? FindLast(Predicate<T> match) => _list.FindLast(match);
        public List<T> FindAll(Predicate<T> match) => _list.FindAll(match);
        public int FindIndex(Predicate<T> match) => _list.FindIndex(match);
        public int FindIndex(int startIndex, Predicate<T> match) => _list.FindIndex(startIndex, match);
        public int FindIndex(int startIndex, int count, Predicate<T> match) => _list.FindIndex(startIndex, count, match);
        public int FindLastIndex(Predicate<T> match) => _list.FindLastIndex(match);
        public int FindLastIndex(int startIndex, Predicate<T> match) => _list.FindLastIndex(startIndex, match);
        public int FindLastIndex(int startIndex, int count, Predicate<T> match) => _list.FindLastIndex(startIndex, count, match);
        public bool Exists(Predicate<T> match) => _list.Exists(match);
        public bool TrueForAll(Predicate<T> match) => _list.TrueForAll(match);
        public void ForEach(Action<T> action) => _list.ForEach(action);
        public List<T> GetRange(int index, int count) => _list.GetRange(index, count);
        public int BinarySearch(T item) => _list.BinarySearch(item);
        public int BinarySearch(T item, IComparer<T>? comparer) => _list.BinarySearch(item, comparer);
        public int BinarySearch(int index, int count, T item, IComparer<T>? comparer) => _list.BinarySearch(index, count, item, comparer);
        public List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter) => _list.ConvertAll(converter);
        public T[] ToArray() => _list.ToArray();
        public int LastIndexOf(T item) => _list.LastIndexOf(item);
        public int LastIndexOf(T item, int index) => _list.LastIndexOf(item, index);

        public List<T>.Enumerator GetEnumerator() => _list.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();
    }
}
