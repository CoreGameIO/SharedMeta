using System;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Server-side provider for static game configuration. Implement and register in DI
    /// (one provider per <typeparamref name="TConfig"/>) to materialize config bytes for
    /// any requested <see cref="MetaConfigVersion"/>. The framework resolves *which*
    /// version applies per call from the caller's client app version (via
    /// <see cref="MetaConfigVersionAttribute"/> rules on the config class) and then calls
    /// <see cref="GetConfig"/> / <see cref="GetConfigAsync"/> for the bytes.
    ///
    /// <para>
    /// <b>0.21.0:</b> The pre-0.21.0 <c>CurrentVersion</c> property has been removed. Per-call
    /// resolution is always driven by the caller's client app version (or, for server-internal
    /// callers, by <see cref="IConfigVersionResolver.CurrentClientVersion"/>). There is no
    /// implicit "latest known" fallback — calls without a resolvable client version throw.
    /// </para>
    /// </summary>
    /// <typeparam name="TConfig">The config type (marked with [MetaConfig]).</typeparam>
    public interface IMetaConfigProvider<TConfig> where TConfig : class
    {
        /// <summary>
        /// Get config for a specific version (synchronous).
        /// Called during entity activation and on subscribe.
        /// </summary>
        /// <param name="version">The config version.</param>
        /// <returns>The config instance.</returns>
        TConfig GetConfig(MetaConfigVersion version);

        /// <summary>
        /// Async config materialization. Default implementation delegates to the synchronous
        /// <see cref="GetConfig"/> for backward compatibility — overrides may perform real
        /// I/O (database, blob storage, remote service) without blocking the entity grain.
        /// <para>
        /// Used by the generated sibling-service infrastructure (0.20.0+) to resolve a
        /// callee's typed <c>Config</c> for the current <c>CallerClientVersion</c> before
        /// dispatch — see <see cref="MetaServiceImplAttribute"/>-generated
        /// <c>Get{Iface}SiblingAsync</c> accessors and the self-detect branch of
        /// <c>Get{Iface}(entityId)</c>. Result caching is handled by the generator (per
        /// service per <c>CallerClientVersion</c> per outer-call).
        /// </para>
        /// </summary>
        Task<TConfig> GetConfigAsync(MetaConfigVersion version)
            => Task.FromResult(GetConfig(version));

        /// <summary>
        /// Resolve the latest available config version within a specific Major.Minor branch.
        /// Called when a client connects and the matching <see cref="MetaConfigVersionAttribute"/>
        /// rule specifies <c>*</c> for the Patch component — meaning "latest patch in this branch".
        ///
        /// Example: pattern "1.x.*" → caller resolves to the newest 1.3.N via
        /// <c>ResolveLatestMatching(1, 3)</c>.
        ///
        /// The default implementation returns <c>new MetaConfigVersion(major, minor, 0)</c> —
        /// i.e. "no patch tracking, treat the branch as a single 0-patch version". Override
        /// in providers that manage multiple patch versions to return the highest patch known
        /// for the branch.
        /// </summary>
        MetaConfigVersion ResolveLatestMatching(int major, int minor)
            => new MetaConfigVersion(major, minor, 0);

        /// <summary>
        /// Resolve the config version for a specific connecting client's app version using
        /// the <see cref="MetaConfigVersionAttribute"/> rules on the config class.
        ///
        /// <para>
        /// <b>Permissive default in 0.21.0; strict in Phase 5-7:</b> when
        /// <paramref name="clientAppVersion"/> is null/empty OR no
        /// <c>[MetaConfigVersion]</c> rule matches, the default impl returns
        /// <c>default(MetaConfigVersion)</c> (0.0.0). This preserves the pre-0.21.0 "no
        /// version assigned" semantics for existing code paths. Later phases tighten this
        /// at the <c>EntityGrain</c> handler boundary based on <see cref="EntityScope"/>:
        /// Private/Shared require a real client version (or first-subscriber pin), Global
        /// substitutes <see cref="IConfigVersionResolver.CurrentClientVersion"/>.
        /// </para>
        /// </summary>
        MetaConfigVersion ResolveForClient(string? clientAppVersion, MetaConfigVersionResolver? resolver)
        {
            if (string.IsNullOrEmpty(clientAppVersion))
                return default;

            resolver ??= MetaConfigVersionResolver.ForType(typeof(TConfig));
            if (resolver == null)
                return default;

            var request = resolver.Resolve(clientAppVersion!);
            if (request == null)
                return default;

            return request.PatchIsLatest
                ? ResolveLatestMatching(request.Major, request.Minor)
                : new MetaConfigVersion(request.Major, request.Minor, request.PatchMin);
        }

        /// <summary>
        /// Get the download URL for a specific config version.
        /// Called by the transport layer when client requests a config download.
        /// Return null if client is expected to have config bundled.
        /// </summary>
        /// <param name="version">The config version requested by the client.</param>
        string? GetDownloadUrl(MetaConfigVersion version) => null;
    }
}
