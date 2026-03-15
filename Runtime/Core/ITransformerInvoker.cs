using System;

namespace SharedMeta.Core
{
    /// <summary>
    /// Non-generic interface for invoking transformers without reflection.
    /// Used by TransformerRegistry to store typed invokers.
    /// </summary>
    public interface ITransformerInvoker
    {
        /// <summary>Box: Complex → Simple for serialization.</summary>
        object Box(object value, object? state);

        /// <summary>Unbox: Simple → Complex after deserialization.</summary>
        object Unbox(object value, object? state);

        /// <summary>The complex type this transformer handles.</summary>
        Type ComplexType { get; }

        /// <summary>The simple (serializable) type.</summary>
        Type SimpleType { get; }

        /// <summary>Whether this transformer requires state.</summary>
        bool RequiresState { get; }
    }

    /// <summary>
    /// Typed invoker for IStateArgumentTransformer (requires state).
    /// Eliminates reflection by using generic constraints.
    /// </summary>
    public class StateTransformerInvoker<TComplex, TSimple, TState, TTransformer> : ITransformerInvoker
        where TTransformer : IStateArgumentTransformer<TComplex, TSimple, TState>, new()
        where TState : ISharedState
    {
        private readonly TTransformer _transformer = new();

        public Type ComplexType => typeof(TComplex);
        public Type SimpleType => typeof(TSimple);
        public bool RequiresState => true;

        public object Box(object value, object? state)
        {
            if (state == null)
                throw new InvalidOperationException($"Transformer {typeof(TTransformer).Name} requires state but none was provided");
            return _transformer.Box((TComplex)value, (TState)state)!;
        }

        public object Unbox(object value, object? state)
        {
            if (state == null)
                throw new InvalidOperationException($"Transformer {typeof(TTransformer).Name} requires state but none was provided");
            return _transformer.Unbox((TSimple)value, (TState)state)!;
        }
    }

    /// <summary>
    /// Typed invoker for IArgumentTransformer (stateless).
    /// Eliminates reflection by using generic constraints.
    /// </summary>
    public class SimpleTransformerInvoker<TComplex, TSimple, TTransformer> : ITransformerInvoker
        where TTransformer : IArgumentTransformer<TComplex, TSimple>, new()
    {
        private readonly TTransformer _transformer = new();

        public Type ComplexType => typeof(TComplex);
        public Type SimpleType => typeof(TSimple);
        public bool RequiresState => false;

        public object Box(object value, object? state)
        {
            return _transformer.Box((TComplex)value)!;
        }

        public object Unbox(object value, object? state)
        {
            return _transformer.Unbox((TSimple)value)!;
        }
    }
}
