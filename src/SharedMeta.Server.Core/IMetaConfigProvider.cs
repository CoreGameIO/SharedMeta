namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Server-side provider for static game configuration.
    /// Implement this interface and register in DI to provide config to entities.
    /// Config is sent to clients on subscribe and available via Context.Config.
    /// </summary>
    /// <typeparam name="TConfig">The config type (marked with [MetaConfig]).</typeparam>
    public interface IMetaConfigProvider<TConfig> where TConfig : class
    {
        /// <summary>
        /// Get the config for a given entity.
        /// Called during entity activation and on subscribe.
        /// </summary>
        /// <param name="entityId">The entity ID.</param>
        /// <returns>The config instance.</returns>
        TConfig GetConfig(string entityId);
    }
}
