using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Core;
using SharedMeta.Server.Core;

namespace SharedMeta.Server
{
    /// <summary>
    /// 0.26.2+: Two paired helpers that wire a host-managed HTTP endpoint for SharedMeta
    /// versioned config delivery and a matching <see cref="IConfigDownloadUrlResolver"/>
    /// that emits URLs targeting the same route. Eliminates the boilerplate where every
    /// host repeats ~25 lines of <c>MapGet("/meta/config/{stateType}/{version}", ...)</c>
    /// plus a custom resolver that builds the URL by hand.
    /// </summary>
    public static class MetaConfigHttpExtensions
    {
        /// <summary>
        /// Register an <see cref="IConfigDownloadUrlResolver"/> that builds absolute URLs in the
        /// form <c>{publicBaseUrl}{routePrefix}/{stateTypeName}/{major}.{minor}.{patch}</c> —
        /// symmetric with <see cref="MapMetaConfigDownload{TConfig}"/>. The resolver ignores
        /// what's actually registered as <see cref="IMetaConfigProvider{TConfig}"/> and assumes
        /// the host serves bytes for every requested version (typical when paired with
        /// <see cref="MapMetaConfigDownload{TConfig}"/>).
        /// <para>
        /// Because of the generator-emitted <c>TryAddSingleton</c> for the default resolver
        /// (0.26.2+), calling this method any time before <c>Build()</c> wins without
        /// <c>RemoveAll</c> or ordering tricks.
        /// </para>
        /// </summary>
        /// <param name="publicBaseUrl">Absolute base URL clients can reach — e.g. "https://api.example.com".</param>
        /// <param name="routePrefix">Route prefix used by the paired endpoint. Leading slash optional. Defaults to "/meta/config".</param>
        public static IServiceCollection AddMetaConfigPublicUrl(
            this IServiceCollection services,
            string publicBaseUrl,
            string routePrefix = "/meta/config")
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (string.IsNullOrWhiteSpace(publicBaseUrl))
                throw new ArgumentException("publicBaseUrl must be a non-empty absolute URL.", nameof(publicBaseUrl));

            services.AddSingleton<IConfigDownloadUrlResolver>(
                new PublicUrlConfigDownloadResolver(publicBaseUrl, routePrefix));
            return services;
        }

        /// <summary>
        /// Map <c>GET {routePrefix}/{stateType}/{version}</c> that streams the serialized config
        /// bytes for the requested version, auto-routing across all <c>[MetaConfig]</c> types
        /// declared in the assembly. Uses the generator-emitted <see cref="IConfigByteSource"/>
        /// to pick the right <see cref="IMetaConfigProvider{TConfig}"/> by <c>stateType</c>;
        /// works out of the box for projects that mix several distinct config types.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Pair with <see cref="AddMetaConfigPublicUrl"/> for the matching URL resolver. Both
        /// helpers register on ASP.NET DI (<c>builder.Services</c>) — silo-DI registrations
        /// (<c>siloBuilder.ConfigureServices</c> / <c>services.ConfigureMeta</c>) are invisible
        /// to <c>MetaHub</c> and minimal endpoints. The <see cref="IMetaConfigProvider{TConfig}"/>
        /// for every declared config type must be reachable from ASP.NET DI for this endpoint
        /// to materialize bytes.
        /// </para>
        /// <para>
        /// Use the generic overload <see cref="MapMetaConfigDownload{TConfig}"/> only when you
        /// want one endpoint serving a single TConfig under its own dedicated route.
        /// </para>
        /// </remarks>
        /// <param name="routePrefix">Route prefix; pair with the same value passed to <see cref="AddMetaConfigPublicUrl"/>. Defaults to "/meta/config".</param>
        public static IEndpointConventionBuilder MapMetaConfigDownload(
            this IEndpointRouteBuilder endpoints,
            string routePrefix = "/meta/config")
        {
            if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
            var route = NormalizeRoute(routePrefix) + "/{stateType}/{version}";

            return endpoints.MapGet(route, (
                string stateType,
                string version,
                IConfigByteSource source,
                IMetaSerializer serializer) =>
            {
                MetaConfigVersion v;
                try
                {
                    v = MetaConfigVersion.Parse(version);
                }
                catch (Exception)
                {
                    return Results.BadRequest($"Invalid version: '{version}'. Expected M.m.p.");
                }

                byte[]? bytes;
                try
                {
                    bytes = source.GetBytes(serializer, stateType, v);
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        $"Config materialization failed for state '{stateType}' v{v.Major}.{v.Minor}.{v.Patch}: {ex.Message}",
                        statusCode: 500);
                }

                if (bytes is null)
                {
                    return Results.NotFound($"No config registered for state '{stateType}'.");
                }

                return Results.Bytes(bytes, "application/octet-stream");
            });
        }

        /// <summary>
        /// Map <c>GET {routePrefix}/{stateType}/{version}</c> for a single specific
        /// <typeparamref name="TConfig"/>. The endpoint resolves
        /// <see cref="IMetaConfigProvider{TConfig}"/> from DI and serializes via
        /// <see cref="IMetaSerializer"/> — both must be registered on
        /// <c>builder.Services</c> (not just in <c>ConfigureMeta</c>'s silo DI).
        /// <paramref name="routePrefix"/> must be unique per TConfig to avoid routing
        /// collisions; prefer the non-generic overload
        /// <see cref="MapMetaConfigDownload(IEndpointRouteBuilder, string)"/> for multi-config
        /// projects.
        /// </summary>
        /// <typeparam name="TConfig">Config type registered via <see cref="IMetaConfigProvider{TConfig}"/>.</typeparam>
        /// <param name="routePrefix">Route prefix; pair with the same value passed to <see cref="AddMetaConfigPublicUrl"/>. Defaults to "/meta/config".</param>
        public static IEndpointConventionBuilder MapMetaConfigDownload<TConfig>(
            this IEndpointRouteBuilder endpoints,
            string routePrefix = "/meta/config")
            where TConfig : class
        {
            if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
            var route = NormalizeRoute(routePrefix) + "/{stateType}/{version}";

            return endpoints.MapGet(route, (
                string stateType,
                string version,
                IMetaConfigProvider<TConfig> provider,
                IMetaSerializer serializer) =>
            {
                MetaConfigVersion v;
                try
                {
                    v = MetaConfigVersion.Parse(version);
                }
                catch (Exception)
                {
                    return Results.BadRequest($"Invalid version: '{version}'. Expected M.m.p.");
                }

                TConfig config;
                try
                {
                    config = provider.GetConfig(v);
                }
                catch (Exception ex)
                {
                    return Results.Problem(
                        $"Config materialization failed for v{v.Major}.{v.Minor}.{v.Patch}: {ex.Message}",
                        statusCode: 500);
                }

                // PackForExternalUsage returns a fresh GC-managed byte[] (not a slice over a
                // grain-scoped scratch buffer) — required when bytes leave the current method
                // boundary, as they do here on the HTTP response stream.
                var bytes = serializer.PackForExternalUsage(config);
                return Results.Bytes(bytes, "application/octet-stream");
            });
        }

        private static string NormalizeRoute(string routePrefix)
        {
            if (string.IsNullOrWhiteSpace(routePrefix)) return "/meta/config";
            var trimmed = routePrefix.Trim().TrimEnd('/');
            return trimmed.StartsWith("/", StringComparison.Ordinal) ? trimmed : "/" + trimmed;
        }

        private sealed class PublicUrlConfigDownloadResolver : IConfigDownloadUrlResolver
        {
            private readonly string _publicBaseUrl;
            private readonly string _routePrefix;

            public PublicUrlConfigDownloadResolver(string publicBaseUrl, string routePrefix)
            {
                _publicBaseUrl = publicBaseUrl.TrimEnd('/');
                _routePrefix = NormalizeRoute(routePrefix);
            }

            public string? GetDownloadUrl(string stateTypeName, MetaConfigVersion version)
                => $"{_publicBaseUrl}{_routePrefix}/{stateTypeName}/{version.Major}.{version.Minor}.{version.Patch}";
        }
    }
}
