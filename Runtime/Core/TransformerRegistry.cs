using System;
using System.Collections.Generic;

namespace SharedMeta.Core
{
    /// <summary>
    /// Registry for argument transformers.
    /// Maps complex types to their transformer invokers for automatic boxing/unboxing.
    /// Uses typed invokers - no reflection at runtime.
    /// </summary>
    public class TransformerRegistry
    {
        private readonly Dictionary<Type, ITransformerInvoker> _invokers = new();

        /// <summary>
        /// Register a state-aware transformer with typed invoker.
        /// No reflection at runtime.
        /// </summary>
        public void Register<TComplex, TSimple, TState, TTransformer>()
            where TTransformer : IStateArgumentTransformer<TComplex, TSimple, TState>, new()
            where TState : ISharedState
        {
            _invokers[typeof(TComplex)] = new StateTransformerInvoker<TComplex, TSimple, TState, TTransformer>();
        }

        /// <summary>
        /// Register a stateless transformer with typed invoker.
        /// No reflection at runtime.
        /// </summary>
        public void RegisterSimple<TComplex, TSimple, TTransformer>()
            where TTransformer : IArgumentTransformer<TComplex, TSimple>, new()
        {
            _invokers[typeof(TComplex)] = new SimpleTransformerInvoker<TComplex, TSimple, TTransformer>();
        }

        /// <summary>
        /// Get the typed invoker for a complex type.
        /// </summary>
        public ITransformerInvoker? GetInvoker(Type complexType)
        {
            return _invokers.TryGetValue(complexType, out var invoker) ? invoker : null;
        }

        /// <summary>
        /// Check if a transformer is registered for a complex type.
        /// </summary>
        public bool HasTransformer(Type complexType)
        {
            return _invokers.ContainsKey(complexType);
        }

        /// <summary>
        /// Get the simple (serializable) type for a complex type.
        /// </summary>
        public Type? GetSimpleType(Type complexType)
        {
            return _invokers.TryGetValue(complexType, out var invoker) ? invoker.SimpleType : null;
        }

        /// <summary>
        /// Get all registered complex types.
        /// </summary>
        public IEnumerable<Type> GetRegisteredTypes()
        {
            return _invokers.Keys;
        }
    }
}
