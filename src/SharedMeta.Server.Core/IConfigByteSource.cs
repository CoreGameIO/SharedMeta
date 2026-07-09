using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// 0.26.2+: Non-generic byte source for config download endpoints. Resolves
    /// <c>(stateTypeName, version)</c> to serialized bytes of the corresponding
    /// <see cref="IMetaConfigProvider{TConfig}"/>'s config, picking the right
    /// <c>TConfig</c> based on a generator-emitted state→config map.
    /// <para>
    /// Generated as <c>GeneratedConfigByteSource</c> per server-meta-configuration.
    /// Hosts can override by registering their own <see cref="IConfigByteSource"/>
    /// before <c>Build()</c>.
    /// </para>
    /// <para>
    /// Pair with <c>app.MapMetaConfigDownload()</c> (non-generic overload, from
    /// <c>SharedMeta.Server.MetaConfigHttpExtensions</c>): one endpoint serves all
    /// state types declared in the assembly without per-TConfig wiring.
    /// </para>
    /// <para>
    /// 0.28.0: catalog enumeration moved off this interface — <c>Configs</c> property
    /// + <c>ConfigTypeEntry</c> were removed. Use
    /// <see cref="SharedMeta.Server.Core.Config.Admin.IConfigCatalog"/> for
    /// per-type dispatch with typed <c>TConfig</c> callbacks.
    /// </para>
    /// </summary>
    public interface IConfigByteSource
    {
        /// <summary>
        /// Materialize the config registered
        /// </summary>
        /// <param name="serializer">Serializer used for the wire format — should match the client's.</param>
        /// <param name="configTypeName">Full type name of the requested config.</param>
        /// <param name="version">Requested config version.</param>
        byte[]? GetBytes(IMetaSerializer serializer, string configTypeName, MetaConfigVersion version);

        Task<byte[]?> GetBytesAsync(IMetaSerializer serializer, string configTypeName, MetaConfigVersion version);
    }
}
