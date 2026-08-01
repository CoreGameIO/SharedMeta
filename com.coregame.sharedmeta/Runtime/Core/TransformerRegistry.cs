using System;
using System.Collections.Generic;

namespace SharedMeta.Core
{
    /// <summary>
    /// Per-type singleton holder for argument transformers. Generated box/unbox call sites go
    /// through here, so the transformation a parameter gets is fixed at compile time and costs
    /// one static field read — no reflection, no registry lookup, and nothing that can differ
    /// between client and server at runtime.
    /// </summary>
    /// <remarks>
    /// Transformers must be stateless: one instance is shared for the process lifetime and is
    /// called concurrently from every grain and every client call.
    /// </remarks>
    /// <typeparam name="T">Transformer type. Needs a public parameterless constructor.</typeparam>
    public static class MetaTransformer<T> where T : new()
    {
        /// <summary>The shared instance.</summary>
        public static readonly T Instance = new T();
    }

    /// <summary>
    /// Registry for argument transformers. Maps complex types to their transformer invokers.
    /// </summary>
    /// <remarks>
    /// Not on the generated path. Which parameter gets transformed is decided at generation time,
    /// because the writer and the reader of a payload must reach the same answer and a registry
    /// populated in one process says nothing about the other. Populating this changes nothing;
    /// kept so existing setup code still compiles.
    /// </remarks>
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
