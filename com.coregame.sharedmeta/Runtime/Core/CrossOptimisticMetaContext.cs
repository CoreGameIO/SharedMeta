using System;
using System.Collections.Generic;
using System.Threading.Tasks;

#pragma warning disable CS8603 // Possible null reference return

namespace SharedMeta.Core
{
    /// <summary>
    /// Minimal MetaContext for CrossOptimistic local execution on cross-entity state.
    /// Used by generated LocalEntityCaller to set MetaContextAccessor.Current
    /// with the target entity's state during local cross-entity calls.
    /// Lives in SharedMeta.Core so shared projects can reference it.
    /// </summary>
    public class CrossOptimisticMetaContext<TState> : MetaContext<TState> where TState : class, ISharedState, new()
    {
        private readonly TState _state;
        private readonly Dictionary<Type, object> _wrapperCache = new();

        public CrossOptimisticMetaContext(TState state)
        {
            _state = state;
        }

        public override object StateObject => _state;

        public override IMetaSerializer MetaSerializer => null!;

        public override Task<TEntityState?> GetState<TEntityState>(string entityId) where TEntityState : class
        {
            throw new NotSupportedException(
                "GetState is not supported in CrossOptimistic context. " +
                "Use Server or Optimistic mode for cross-entity state reading.");
        }

        public override void Observe<TInterface>(string id)
        {
            // Not supported in cross-entity context
        }

        public override TInterface GetExternal<TInterface>()
        {
            throw new NotImplementedException("GetExternal not supported in CrossOptimistic context.");
        }

        public override bool TryGetCached(Type key, out object? value)
        {
            return _wrapperCache.TryGetValue(key, out value);
        }

        public override void CacheService(Type key, object wrapper)
        {
            _wrapperCache[key] = wrapper;
        }

        public override TService ResolveService<TService>()
        {
            throw new InvalidOperationException($"Cannot resolve {typeof(TService).Name} in CrossOptimistic context.");
        }
    }
}
