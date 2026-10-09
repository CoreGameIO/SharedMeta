using System.Collections.Generic;

namespace SharedMeta.Core.Reactive
{
    /// <summary>
    /// Simple pool for reusable class instances (wrapper views).
    /// Thread-safe via lock.
    /// </summary>
    public static class ObjectPool<T> where T : class, new()
    {
        private static readonly Stack<T> _pool = new();
        private static readonly object _lock = new();

        public static T Rent()
        {
            lock (_lock)
            {
                if (_pool.Count > 0)
                    return _pool.Pop();
            }
            return new T();
        }

        public static void Return(T obj)
        {
            if (obj == null) return;
            lock (_lock)
            {
                _pool.Push(obj);
            }
        }
    }
}
