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
        /// <b>Strict contract (0.21.0+):</b> <paramref name="clientAppVersion"/> must be
        /// non-empty. Null/empty throws — every code path that reaches here is expected to
        /// know which client version it's resolving for. Server-internal callers (timers,
        /// triggers, background jobs, server-only services) must substitute
        /// <see cref="IConfigVersionResolver.CurrentClientVersion"/> explicitly before
        /// invoking. <see cref="EntityScope.Global"/> entities substitute it inside the
        /// framework's per-call dispatch path. <see cref="EntityScope.Private"/> /
        /// <see cref="EntityScope.Shared"/> entities use the pinned version once a real
        /// subscriber has joined; cold-call dispatch substitutes <c>CurrentClientVersion</c>
        /// inside the generated <c>GetCachedConfigForClient</c>.
        /// </para>
        /// <para>
        /// When no <c>[MetaConfigVersion]</c> rule matches the supplied version, returns
        /// <c>default(MetaConfigVersion)</c> (treat as "no branch routing needed"). This
        /// keeps projects that don't yet declare rules on their config classes working — the
        /// framework still gets a non-throwing result for downstream <see cref="GetConfig"/>.
        /// </para>
        /// </summary>
        MetaConfigVersion ResolveForClient(string? clientAppVersion, MetaConfigVersionResolver? resolver)
        {
            if (string.IsNullOrEmpty(clientAppVersion))
                throw new System.InvalidOperationException(
                    $"IMetaConfigProvider<{typeof(TConfig).Name}>.ResolveForClient: clientAppVersion is required. " +
                    $"Server-internal callers must substitute IConfigVersionResolver.CurrentClientVersion before calling.");

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
