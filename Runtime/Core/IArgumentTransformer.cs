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

    /// <summary>
    /// Config-aware transformer: typically sends a stable key and rebuilds the definition from a
    /// <see cref="ServiceConfigAttribute"/> config on the receiving side.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TConfig"/> must be declared by <c>[ServiceConfig]</c> on every service
    /// whose meta method takes the transformed type — the generator fails the build otherwise.
    /// Each side boxes and unboxes under its own resolved config, so keep the wire value stable
    /// across config versions (an id, not an index into a list that may be reordered).
    /// </remarks>
    /// <typeparam name="TComplex">Complex type (e.g., ItemDefinition)</typeparam>
    /// <typeparam name="TSimple">Simple serializable type (e.g., item id)</typeparam>
    /// <typeparam name="TConfig">Config type declared via <c>[ServiceConfig]</c></typeparam>
    public interface IConfigArgumentTransformer<TComplex, TSimple, TConfig>
        where TConfig : class
    {
        /// <summary>Box: Complex + Config → Simple.</summary>
        TSimple Box(TComplex value, TConfig config);

        /// <summary>Unbox: Simple + Config → Complex.</summary>
        TComplex Unbox(TSimple value, TConfig config);
    }

    /// <summary>
    /// Transformer that needs both the entity state and a <c>[ServiceConfig]</c> config — e.g. an
    /// owned instance rebuilt from state, joined with its definition from config. Same config
    /// rules as <see cref="IConfigArgumentTransformer{TComplex, TSimple, TConfig}"/>.
    /// </summary>
    public interface IStateConfigArgumentTransformer<TComplex, TSimple, TState, TConfig>
        where TState : ISharedState
        where TConfig : class
    {
        /// <summary>Box: Complex + State + Config → Simple.</summary>
        TSimple Box(TComplex value, TState state, TConfig config);

        /// <summary>Unbox: Simple + State + Config → Complex.</summary>
        TComplex Unbox(TSimple value, TState state, TConfig config);
    }
}
