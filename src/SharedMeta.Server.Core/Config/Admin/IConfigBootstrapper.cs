using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.28.0+ Typed project-supplied cold-start seed source. Two-phase contract so the
    /// framework can ask the version first, check the registry, and only materialize bytes
    /// when a publish is actually needed.
    /// <para>
    /// 0.28.0 — Generic methods replace the 0.27.x <c>Type configType</c> argument. Generator-emitted
    /// <see cref="IConfigCatalog"/> closes <c>TConfig</c> at compile time and dispatches into these
    /// methods per <c>[MetaConfig]</c> type, so implementers see the concrete type parameter and
    /// can use typed APIs (<c>new TConfig()</c>, <c>IMetaSerializer.PackForExternalUsage&lt;TConfig&gt;</c>)
    /// without reflection. Inside the method, a <c>typeof(TConfig) == typeof(MyConfig)</c> switch is
    /// the idiomatic per-config dispatch.
    /// </para>
    /// <para>Built-in implementations:</para>
    /// <list type="bullet">
    /// <item><c>DirectoryConfigBootstrapper</c> — read-only filesystem scan over <c>{root}/{Type.Name}/{M.m.p}.bin</c>. Suitable for baked Docker images.</item>
    /// </list>
    /// <para>Project impls layer onto the same contract — e.g. an <c>EmbeddedResourceBootstrapper</c>,
    /// a CDN fetcher, or the wizard-emitted default-instance starter.</para>
    /// </summary>
    public interface IConfigBootstrapper
    {
        /// <summary>
        /// Which version does the project consider the default for <typeparamref name="TConfig"/>?
        /// Return <c>null</c> when no seed is available — the framework leaves the registry alone
        /// for that type (lazy seed via admin upload still works).
        /// <para>
        /// Called once per type per silo cold-start. Cheap implementations (literal constant,
        /// directory scan) are expected; this method may run on every silo start while
        /// <see cref="GetBytesAsync{TConfig}"/> only runs when a publish is required.
        /// </para>
        /// </summary>
        Task<MetaConfigVersion?> GetVersionAsync<TConfig>(CancellationToken cancellationToken) where TConfig : class;

        /// <summary>
        /// Materialize the serialized bytes for <c>(TConfig, version)</c> plus audit metadata.
        /// Invoked only when the strategy (see <see cref="ConfigSeedStrategy"/>) decides a publish
        /// is needed — never speculatively. Return <c>null</c> to skip the publish (e.g. version
        /// promised by <see cref="GetVersionAsync{TConfig}"/> turned out to be unavailable).
        /// </summary>
        Task<ConfigBootstrapBytes?> GetBytesAsync<TConfig>(MetaConfigVersion version, CancellationToken cancellationToken) where TConfig : class;
    }
}
