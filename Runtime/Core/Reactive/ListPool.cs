using System.Collections.Generic;

namespace SharedMeta.Core.Reactive
{
    /// <summary>
    /// Simple pool for List&lt;T&gt; instances. Rent returns a cleared list, Return puts it back.
    /// Thread-safe via lock. Designed for use with ChangeTracker change buffers.
    /// </summary>
    public static class ListPool<T>
    {
        private static readonly Stack<List<T>> _pool = new();
        private static readonly object _lock = new();

        public static List<T> Rent()
        {
            lock (_lock)
            {
                if (_pool.Count > 0)
                {
                    var list = _pool.Pop();
                    list.Clear();
                    return list;
                }
            }
            return new List<T>();
        }

        public static void Return(List<T> list)
        {
            if (list == null) return;
            list.Clear();
            lock (_lock)
            {
                _pool.Push(list);
            }
        }
    }
}
