using System.Threading;

namespace SharedMeta.Core
{
    /// <summary>
    /// Provides access to the current MetaContext via AsyncLocal.
    /// </summary>
    public static class MetaContextAccessor
    {
        private static readonly AsyncLocal<MetaContext?> _current = new AsyncLocal<MetaContext?>();

        /// <summary>
        /// Gets or sets the current context.
        /// </summary>
        public static MetaContext? Current
        {
            get => _current.Value;
            set => _current.Value = value;
        }

        /// <summary>
        /// Helper to get typed context. Throws if missing or wrong type.
        /// </summary>
        public static MetaContext<TState> Get<TState>()
        {
            var ctx = Current;
            if (ctx == null)
            {
                throw new System.InvalidOperationException("No active MetaContext found. Are you calling this from within a MetaService method?");
            }

            if (ctx is MetaContext<TState> typed)
            {
                return typed;
            }

            throw new System.InvalidOperationException($"Current MetaContext is not of type MetaContext<{typeof(TState).Name}>.");
        }
    }
}
