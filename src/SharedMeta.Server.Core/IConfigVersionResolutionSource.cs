using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Non-generic version-resolution source for <c>[StatelessMetaService]</c> config lookups —
    /// the entity-less counterpart of <see cref="IConfigByteSource"/>. Resolves
    /// <c>(configTypeName, clientAppVersion)</c> to the <see cref="MetaConfigVersion"/> that
    /// applies, by routing to the matching <see cref="IMetaConfigProvider{TConfig}"/> and calling
    /// its <see cref="IMetaConfigProvider{TConfig}.ResolveForClient"/>.
    /// <para>
    /// Generated as <c>GeneratedStatelessConfigVersionSource</c> — one branch per
    /// <c>[StatelessMetaService(typeof(TConfig))]</c> declared in the assembly. Hosts can
    /// override by registering their own <see cref="IConfigVersionResolutionSource"/> before
    /// <c>Build()</c>.
    /// </para>
    /// <para>
    /// Consumed by the transport-level <c>ResolveStatelessConfigVersion</c> RPC (SignalR
    /// <c>MetaHub</c> / HttpPolling endpoint) so a <c>MetaClient</c> can resolve a stateless
    /// service's config version without subscribing to any entity.
    /// </para>
    /// </summary>
    public interface IConfigVersionResolutionSource
    {
        /// <summary>
        /// Resolve the config version for <paramref name="configTypeName"/> given
        /// <paramref name="clientAppVersion"/>. Returns <c>null</c> when no
        /// <c>[StatelessMetaService]</c> is registered for that config type name.
        /// </summary>
        MetaConfigVersion? ResolveVersion(string configTypeName, string clientAppVersion);
    }
}
