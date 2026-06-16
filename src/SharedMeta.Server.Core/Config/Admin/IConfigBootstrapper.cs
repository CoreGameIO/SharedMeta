using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.1+ Project-supplied cold-start seed source. Two-phase contract so the
    /// framework can ask the version first, check the registry, and only materialize
    /// bytes when a publish is actually needed.
    /// <para>
    /// Built-in implementations:
    /// </para>
    /// <list type="bullet">
    /// <item><c>DefaultInstanceConfigBootstrapper</c> — pure in-memory: <c>Activator.CreateInstance</c> + <c>IMetaSerializer</c>. No filesystem.</item>
    /// <item><c>DirectoryConfigBootstrapper</c> — read-only filesystem scan over <c>{root}/{Type.Name}/{M.m.p}.bin</c>. Suitable for baked Docker images.</item>
    /// </list>
    /// <para>Project impls layer onto the same contract — e.g. an <c>EmbeddedResourceBootstrapper</c> or a CDN fetcher.</para>
    /// </summary>
    public interface IConfigBootstrapper
    {
        /// <summary>
        /// Which version does the project consider the default for <paramref name="configType"/>?
        /// Return <c>null</c> when no seed is available — the framework leaves the registry alone
        /// for that type (lazy seed via admin upload still works).
        /// <para>
        /// Called once per type per silo cold-start. Cheap implementations (literal constant,
        /// directory scan) are expected; this method may run on every silo start while
        /// <see cref="GetBytesAsync"/> only runs when a publish is required.
        /// </para>
        /// </summary>
        Task<MetaConfigVersion?> GetVersionAsync(Type configType, CancellationToken cancellationToken);

        /// <summary>
        /// Materialize the serialized bytes for <c>(configType, version)</c> plus audit metadata.
        /// Invoked only when the strategy (see <see cref="ConfigSeedStrategy"/>) decides a publish
        /// is needed — never speculatively. Return <c>null</c> to skip the publish (e.g. version
        /// promised by <see cref="GetVersionAsync"/> turned out to be unavailable).
        /// </summary>
        Task<ConfigBootstrapBytes?> GetBytesAsync(Type configType, MetaConfigVersion version, CancellationToken cancellationToken);
    }
}
