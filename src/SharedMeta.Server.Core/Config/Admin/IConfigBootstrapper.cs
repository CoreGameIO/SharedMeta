using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Project-supplied cold-start seed source. Called by the framework's
    /// <c>ConfigBootstrapHostedService</c> for every discovered <c>[MetaConfig]</c> type
    /// on silo startup. Return <c>null</c> if no seed is available — the framework will
    /// leave the registry untouched for that type (lazy seed via admin upload remains
    /// the fallback).
    /// <para>
    /// Typical project implementations:
    /// </para>
    /// <list type="bullet">
    /// <item><c>ManifestBootstrapper</c> — reads a <c>manifest.json</c> + per-config bytes baked into the docker image.</item>
    /// <item><c>EmbeddedResourceBootstrapper</c> — pulls bytes from <c>Assembly.GetManifestResourceStream</c>.</item>
    /// <item><c>HttpSeedBootstrapper</c> — fetches from an internal config CDN.</item>
    /// </list>
    /// <para>
    /// The framework calls <see cref="IConfigRegistry.PublishAsync"/> only if the
    /// registry has no <see cref="SharedMeta.Core.MetaConfigVersion"/> matching the
    /// returned seed — re-seeding the same version is a no-op so this is safe to call
    /// on every cold-start.
    /// </para>
    /// </summary>
    public interface IConfigBootstrapper
    {
        /// <summary>
        /// Materialize the seed bytes for <paramref name="configType"/> (a class annotated
        /// <c>[MetaConfig]</c>). Return <c>null</c> when no baked seed exists — the framework
        /// will skip the publish for this type.
        /// </summary>
        Task<ConfigBootstrapSeed?> LoadAsync(Type configType, CancellationToken cancellationToken);
    }
}
