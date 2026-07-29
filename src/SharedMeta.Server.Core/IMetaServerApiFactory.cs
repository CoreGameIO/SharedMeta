using Orleans;
using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// DI handle the generated <c>{Service}ServerApi</c> accessors bind to. Inject it into an admin
    /// grain, a background job or an ASP.NET endpoint and call
    /// <c>factory.GetServerApi&lt;IMyService&gt;(entityId)</c>.
    /// </summary>
    /// <remarks>
    /// Exists so the call site never has to hand a serializer over. Argument packing must use the
    /// serializer the service's own assembly declared, and under
    /// <c>[assembly: MetaSerializer(SerializerType.Generic)]</c> that needs a live
    /// <see cref="IMetaSerializer"/> — which is already a silo singleton, so requiring callers to
    /// pass it was asking them to re-resolve something DI had all along. Binding through this
    /// handle also keeps the accessor's shape identical whichever serializer a project declared.
    /// <para>
    /// The <see cref="IGrainFactory"/> extension overloads remain for callers that hold no service
    /// provider.
    /// </para>
    /// </remarks>
    public interface IMetaServerApiFactory
    {
        /// <summary>Grain factory used to address the target entity.</summary>
        IGrainFactory GrainFactory { get; }

        /// <summary>Silo-singleton serializer, used by services that declared the generic path.</summary>
        IMetaSerializer Serializer { get; }
    }

    /// <summary>
    /// Default <see cref="IMetaServerApiFactory"/> — a straight pair of DI singletons.
    /// </summary>
    public sealed class MetaServerApiFactory : IMetaServerApiFactory
    {
        public MetaServerApiFactory(IGrainFactory grainFactory, IMetaSerializer serializer)
        {
            GrainFactory = grainFactory;
            Serializer = serializer;
        }

        public IGrainFactory GrainFactory { get; }

        public IMetaSerializer Serializer { get; }
    }
}
