using System;
using System.Collections;
using System.Collections.Generic;

namespace SharedMeta.Core.Patch
{
    /// <summary>
    /// Wraps a T[] array with automatic change tracking for ServerPatch mode.
    /// Indexed set operations auto-mark the field as dirty when tracking is active.
    /// Arrays cannot be resized — Add/Insert/Remove throw NotSupportedException.
    /// </summary>
    public class PatchableArray<T> : IList<T>, IReadOnlyList<T>
    {
        private readonly T[] _array;
        private readonly PatchNode? _parentNode;
        private readonly int _fieldId;
        private readonly IMetaSerializer? _serializer;

        public PatchableArray(T[] array, PatchNode? parentNode, int fieldId, IMetaSerializer? serializer)
        {
            _array = array;
            _parentNode = parentNode;
            _fieldId = fieldId;
            _serializer = serializer;
        }

        /// <summary>Explicitly mark this array as dirty.</summary>
        public void SetDirty()
        {
            MarkChanged();
        }

        /// <summary>Access the underlying array directly.</summary>
        public T[] Inner => _array;

        private void MarkChanged()
        {
            if (_parentNode != null && _serializer != null)
                _parentNode.MarkChildTerminal(_fieldId, _serializer.Pack(_array));
        }

        // === Indexed access (set auto-marks dirty) ===

        public T this[int index]
        {
            get => _array[index];
            set { _array[index] = value; MarkChanged(); }
        }

        // === Read-only operations ===

        public int Count => _array.Length;
        public bool IsReadOnly => true; // IList<T> contract: can't add/remove

        public bool Contains(T item) => Array.IndexOf(_array, item) >= 0;
        public int IndexOf(T item) => Array.IndexOf(_array, item);
        public void CopyTo(T[] array, int arrayIndex) => _array.CopyTo(array, arrayIndex);

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _array.Length; i++)
                yield return _array[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // === Unsupported operations (arrays are fixed-size) ===

        public void Add(T item) => throw new NotSupportedException("Arrays are fixed-size.");
        public void Insert(int index, T item) => throw new NotSupportedException("Arrays are fixed-size.");
        public bool Remove(T item) => throw new NotSupportedException("Arrays are fixed-size.");
        public void RemoveAt(int index) => throw new NotSupportedException("Arrays are fixed-size.");
        public void Clear() => throw new NotSupportedException("Arrays are fixed-size.");
    }
}
