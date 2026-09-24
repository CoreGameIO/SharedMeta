namespace SharedMeta.Core
{
    /// <summary>
    /// Transforms complex types to/from simpler serializable types.
    /// Stateless - can use DI for configuration via constructor.
    /// </summary>
    /// <typeparam name="TComplex">Complex type (e.g., Character)</typeparam>
    /// <typeparam name="TSimple">Simple serializable type (e.g., int)</typeparam>
    public interface IArgumentTransformer<TComplex, TSimple>
    {
        /// <summary>Box: Complex → Simple for serialization.</summary>
        TSimple Box(TComplex value);
        
        /// <summary>Unbox: Simple → Complex after deserialization.</summary>
        TComplex Unbox(TSimple value);
    }

    /// <summary>
    /// State-aware transformer that receives state in methods.
    /// </summary>
    /// <typeparam name="TComplex">Complex type</typeparam>
    /// <typeparam name="TSimple">Simple serializable type</typeparam>
    /// <typeparam name="TState">State type for context</typeparam>
    public interface IStateArgumentTransformer<TComplex, TSimple, TState> 
        where TState : ISharedState
    {
        /// <summary>Box: Complex + State → Simple.</summary>
        TSimple Box(TComplex value, TState state);
        
        /// <summary>Unbox: Simple + State → Complex.</summary>
        TComplex Unbox(TSimple value, TState state);
    }
}
