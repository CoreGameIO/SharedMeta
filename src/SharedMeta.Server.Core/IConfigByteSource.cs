using System;
using System.Collections.Generic;
using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// 0.27.0+ Compile-time discovered config type entry. Emitted by the generator from
    /// every <c>[MetaConfig]</c> class plus its owning <c>[MetaService(StateType = …)]</c>
    /// state. Consumed by the admin / bootstrap stack to iterate without reflection.
    /// </summary>
    public sealed class ConfigTypeEntry
    {
        /// <summary>Stable identifier — defaults to <see cref="System.Type.FullName"/>.</summary>
        public required string Name { get; init; }

        /// <summary>Short display name — defaults to <see cref="System.Type.Name"/>.</summary>
        public required string DisplayName { get; init; }

        /// <summary><c>[MetaConfig]</c>-annotated config DTO class.</summary>
        public required Type ConfigType { get; init; }

        /// <summary>State that owns this config (target of <c>[MetaConfig(StateType = …)]</c>).</summary>
        public required Type StateType { get; init; }
    }

    /// <summary>
    /// 0.26.2+: Non-generic byte source for config download endpoints. Resolves
    /// <c>(stateTypeName, version)</c> to serialized bytes of the corresponding
    /// <see cref="IMetaConfigProvider{TConfig}"/>'s config, picking the right
    /// <c>TConfig</c> based on a generator-emitted state→config map.
    /// <para>
    /// Generated as <c>GeneratedConfigByteSource</c> per server-meta-configuration.
    /// Hosts can override by registering their own <see cref="IConfigByteSource"/>
    /// before <c>Build()</c> — the generated default is wired with
    /// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Func{System.IServiceProvider,TService})"/>.
    /// </para>
    /// <para>
    /// Pair with <c>app.MapMetaConfigDownload()</c> (non-generic overload, from
    /// <c>SharedMeta.Server.MetaConfigHttpExtensions</c>): one endpoint serves all
    /// state types declared in the assembly without per-TConfig wiring.
    /// </para>
    /// </summary>
    public interface IConfigByteSource
    {
        /// <summary>
        /// Materialize the config registered for <paramref name="stateTypeName"/>
        /// at <paramref name="version"/> and serialize it via
        /// <paramref name="serializer"/>. Returns <c>null</c> when the state type
        /// is unknown or no provider is registered for its config type.
        /// </summary>
        /// <param name="serializer">Serializer used for the wire format — should match the client's.</param>
        /// <param name="stateTypeName">Simple or fully-qualified state type name (the path-segment from the download URL).</param>
        /// <param name="version">Requested config version.</param>
        byte[]? GetBytes(IMetaSerializer serializer, string stateTypeName, MetaConfigVersion version);

        /// <summary>
        /// 0.27.0+ Every <c>[MetaConfig]</c> type the generator discovered for this silo,
        /// keyed by <see cref="ConfigTypeEntry.Name"/>. Consumed by the admin grain
        /// (<c>SharedMeta.Server.Core.Config.Admin.IConfigAdminGrain</c>) and the bootstrap
        /// hosted service to iterate configs without touching reflection.
        /// </summary>
        IReadOnlyList<ConfigTypeEntry> Configs { get; }
    }
}
