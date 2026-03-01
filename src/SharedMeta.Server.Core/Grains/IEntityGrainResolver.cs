using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Resolves entity grain references by state type name.
    /// Generated implementation uses a compile-time switch instead of runtime reflection.
    /// </summary>
    public interface IEntityGrainResolver
    {
        /// <summary>
        /// Get entity grain reference by state type name and entity ID.
        /// </summary>
        /// <param name="grainFactory">Orleans grain factory</param>
        /// <param name="stateTypeName">State type name (short or fully qualified)</param>
        /// <param name="entityId">Entity ID (grain key)</param>
        /// <returns>Grain reference, or null if state type is unknown</returns>
        IEntityGrainBase? GetEntityGrain(IGrainFactory grainFactory, string stateTypeName, string entityId);

        /// <summary>
        /// Get entity grain reference by service interface name and entity ID.
        /// Used for cross-entity calls where the target is identified by service name.
        /// </summary>
        /// <param name="grainFactory">Orleans grain factory</param>
        /// <param name="serviceName">Service interface name (e.g., "IProfileService")</param>
        /// <param name="entityId">Entity ID (grain key)</param>
        /// <returns>Grain reference, or null if service name is unknown</returns>
        IEntityGrainBase? GetEntityGrainByService(IGrainFactory grainFactory, string serviceName, string entityId);
    }
}
