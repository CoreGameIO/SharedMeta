using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Policy point for server-side config version selection. Owns two related decisions:
    /// (a) which client app version to assume for server-internal callers that have no real
    ///     originating client (timers, background jobs, server-only services); and
    /// (b) which config version a fresh entity should adopt on first activation.
    ///
    /// Register a single implementation in DI. Required when the project uses any
    /// <c>IMetaConfigProvider&lt;&gt;</c> or any state declared with
    /// <c>[EntityScope(EntityScope.Global)]</c>. Without registration, the framework throws
    /// at startup with a hint to register an implementation.
    /// </summary>
    public interface IConfigVersionResolver
    {
        /// <summary>
        /// The "current default" client app version (e.g. <c>"2.0.0"</c>) used by:
        /// <list type="bullet">
        /// <item>server-internal callers that have no real originating client</item>
        /// <item><see cref="EntityScope.Global"/> entities — every call on these entities
        ///       resolves config versions through this value (regardless of any incoming
        ///       <c>CallerClientVersion</c>)</item>
        /// <item>fresh-entity activation when <see cref="ResolveVersion"/> chooses to defer
        ///       to the default rather than pick a custom branch</item>
        /// </list>
        /// Must return a non-empty version string. Typically wired to the latest deployed
        /// client build via the host's release pipeline.
        /// </summary>
        string CurrentClientVersion { get; }

        /// <summary>
        /// Determine the config version for a fresh entity. Called once when the entity
        /// activates without a persisted config version (e.g. first-ever subscribe on a
        /// new <see cref="EntityScope.Private"/> profile, or first-subscriber pin on a new
        /// <see cref="EntityScope.Shared"/> session).
        /// </summary>
        /// <param name="stateTypeName">The state type name.</param>
        /// <param name="entityId">The entity ID.</param>
        /// <param name="defaultVersion">
        /// The version derived from <see cref="CurrentClientVersion"/> via the config class's
        /// <c>[MetaConfigVersion]</c> rules. Implementations may return this unchanged for
        /// the default behaviour, or pick a different branch for A/B tests / staged rollouts.
        /// </param>
        /// <returns>The version this entity should adopt for its first session.</returns>
        MetaConfigVersion ResolveVersion(string stateTypeName, string entityId, MetaConfigVersion defaultVersion);
    }
}
