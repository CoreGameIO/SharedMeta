using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Resolves which config version an entity should use.
    /// Register in DI to customize version selection (A/B tests, gradual rollouts, etc.).
    /// Default behavior: use IMetaConfigProvider.CurrentVersion.
    /// </summary>
    public interface IConfigVersionResolver
    {
        /// <summary>
        /// Determine the config version for an entity.
        /// Called once when the entity activates without a persisted config version.
        /// </summary>
        /// <param name="stateTypeName">The state type name.</param>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="currentVersion">The provider's current (default) version.</param>
        /// <returns>The version this entity should use.</returns>
        MetaConfigVersion ResolveVersion(string stateTypeName, string entityId, MetaConfigVersion currentVersion);
    }
}
