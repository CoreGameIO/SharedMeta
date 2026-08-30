using System;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Client
{
    /// <summary>
    /// Client-side config provider — single point of materialization for <typeparamref name="TConfig"/>.
    /// Replaces the pre-0.14.0 chain of <c>ConfigCache</c> + <c>ConfigDownloader</c> +
    /// <c>ConfigDownloadUrlFactory</c> + <c>MetaServiceConfig.ConfigFactory</c>.
    /// <para>
    /// Register one provider per config type via
    /// <see cref="IMetaServiceResolver.RegisterConfigProvider{TConfig}"/> before subscribing
    /// to entities that use this config. The resolver invokes
    /// <see cref="GetConfigAsync"/> with the server-pinned <see cref="MetaConfigVersion"/>
    /// from the subscribe response.
    /// </para>
    /// <para>
    /// Built-in implementations: <see cref="StaticConfigProvider{TConfig}"/> for a preloaded
    /// instance, <see cref="DownloadingConfigProvider{TConfig}"/> for server-pushed bytes,
    /// <see cref="CompositeConfigProvider{TConfig}"/> for primary-with-fallback chains.
    /// </para>
    /// </summary>
    public interface IClientMetaConfigProvider<TConfig> where TConfig : class
    {
        /// <summary>
        /// Materialize the config for the requested version.
        /// </summary>
        Task<TConfig> GetConfigAsync(MetaConfigVersion version);
    }

    /// <summary>
    /// Optional persistent cache for <see cref="DownloadingConfigProvider{TConfig}"/> —
    /// avoids re-downloading the same version on every session start.
    /// </summary>
    public interface IClientMetaConfigCache<TConfig> where TConfig : class
    {
        TConfig? TryGet(MetaConfigVersion version);
        void Put(MetaConfigVersion version, TConfig config);
    }

    /// <summary>
    /// Opt-in capability: a config cache that can be wiped. Implemented by
    /// <see cref="FileConfigCache{TConfig}"/>. Surfaced to the app through
    /// <c>MetaClient.ClearConfigCaches()</c> as a debug/dev command — after a config is
    /// re-published under the same version (typical dev workflow), clearing the cache forces
    /// a fresh download on the next subscribe. Separate non-generic marker (not a method on
    /// <see cref="IClientMetaConfigCache{TConfig}"/>) so existing cache implementations keep
    /// compiling unchanged and we avoid default-interface-methods (Unity IL2CPP friendliness).
    /// </summary>
    public interface IClearableConfigCache
    {
        void Clear();
    }

    /// <summary>
    /// Opt-in capability: a config provider whose backing cache can be wiped. Implemented by
    /// <see cref="DownloadingConfigProvider{TConfig}"/> and <see cref="CompositeConfigProvider{TConfig}"/>.
    /// The resolver collects these at registration and invokes them from
    /// <c>IMetaServiceResolver.ClearConfigCaches()</c>. Cache-less providers (e.g.
    /// <see cref="StaticConfigProvider{TConfig}"/>) simply don't implement it.
    /// </summary>
    public interface IClearableConfigProvider
    {
        void ClearCache();
    }

    /// <summary>
    /// Returns a fixed preloaded instance regardless of the requested version. Useful when
    /// the client already has the config in hand (loaded from disk, bundled with the app,
    /// fetched outside of SharedMeta) — versioning is handled implicitly by the caller's
    /// own preload logic.
    /// </summary>
    public sealed class StaticConfigProvider<TConfig> : IClientMetaConfigProvider<TConfig>
        where TConfig : class
    {
        private readonly TConfig _config;

        public StaticConfigProvider(TConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public Task<TConfig> GetConfigAsync(MetaConfigVersion version) => Task.FromResult(_config);
    }

    public static class StaticConfigProvider {
        public static StaticConfigProvider<TConfig> Create<TConfig>(TConfig config) where TConfig : class
            => new StaticConfigProvider<TConfig>(config);
    }

    /// <summary>
    /// Materializes a config by fetching it from the server. The URL resolver maps a
    /// <see cref="MetaConfigVersion"/> to a download URL (typically a wrapper around
    /// <c>IConnection.GetConfigDownloadUrlAsync</c>); the bytes are deserialized via the
    /// supplied <see cref="IMetaSerializer"/>. An optional <see cref="IClientMetaConfigCache{TConfig}"/>
    /// avoids re-downloading the same version on subsequent sessions.
    /// </summary>
    public sealed class DownloadingConfigProvider<TConfig> : IClientMetaConfigProvider<TConfig>, IClearableConfigProvider
        where TConfig : class
    {
        private readonly ConfigProvider.UrlResolverDelegate _urlResolver;
        private readonly Func<string, Task<byte[]>> _downloader;
        private readonly IMetaSerializer _serializer;
        private readonly IClientMetaConfigCache<TConfig>? _cache;

        public DownloadingConfigProvider(
            ConfigProvider.UrlResolverDelegate urlResolver,
            Func<string, Task<byte[]>> downloader,
            IMetaSerializer serializer,
            IClientMetaConfigCache<TConfig>? cache = null)
        {
            _urlResolver = urlResolver ?? throw new ArgumentNullException(nameof(urlResolver));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _cache = cache;
        }

        public async Task<TConfig> GetConfigAsync(MetaConfigVersion version)
        {
            var cached = _cache?.TryGet(version);
            if (cached != null) return cached;

            var url = await _urlResolver(typeof(TConfig).FullName ?? "", version).ConfigureAwait(false);
            if (string.IsNullOrEmpty(url))
                throw new InvalidOperationException(
                    $"DownloadingConfigProvider<{typeof(TConfig).Name}>: URL resolver returned no URL for version {version.Major}.{version.Minor}.");

            var bytes = await _downloader(url!).ConfigureAwait(false);
            var config = _serializer.Unpack<TConfig>(bytes);
            if (config == null)
                throw new InvalidOperationException(
                    $"DownloadingConfigProvider<{typeof(TConfig).Name}>: serializer returned null for downloaded bytes from {url}.");

            _cache?.Put(version, config);
            return config;
        }

        /// <summary>Wipe the backing cache (if it supports clearing). No-op otherwise.</summary>
        public void ClearCache() => (_cache as IClearableConfigCache)?.Clear();
    }

    public static class ConfigProvider
    {
        public delegate Task<string?> UrlResolverDelegate(string configTypeName, MetaConfigVersion version);

        public static DownloadingConfigProvider<TConfig> Downloading<TConfig>(
            UrlResolverDelegate urlResolver
            , Func<string, Task<byte[]>> downloader
            , IMetaSerializer serializer
            , IClientMetaConfigCache<TConfig>? cache = null
            ) where TConfig : class
        {
            return new DownloadingConfigProvider<TConfig>(urlResolver, downloader, serializer, cache);
        }
    }

    /// <summary>
    /// Tries <paramref name="primary"/> first; on exception, falls back to <paramref name="fallback"/>.
    /// Typical use: <c>new CompositeConfigProvider(downloading, static)</c> — try the network,
    /// fall back to a bundled snapshot if the server is unreachable.
    /// </summary>
    public sealed class CompositeConfigProvider<TConfig> : IClientMetaConfigProvider<TConfig>, IClearableConfigProvider
        where TConfig : class
    {
        private readonly IClientMetaConfigProvider<TConfig> _primary;
        private readonly IClientMetaConfigProvider<TConfig> _fallback;
        private readonly Action<Exception>? _onPrimaryFailed;

        public CompositeConfigProvider(
            IClientMetaConfigProvider<TConfig> primary,
            IClientMetaConfigProvider<TConfig> fallback,
            Action<Exception>? onPrimaryFailed = null)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
            _onPrimaryFailed = onPrimaryFailed;
        }

        public async Task<TConfig> GetConfigAsync(MetaConfigVersion version)
        {
            try
            {
                return await _primary.GetConfigAsync(version).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _onPrimaryFailed?.Invoke(ex);
                return await _fallback.GetConfigAsync(version).ConfigureAwait(false);
            }
        }

        /// <summary>Forward the wipe to whichever of primary/fallback can be cleared.</summary>
        public void ClearCache()
        {
            (_primary as IClearableConfigProvider)?.ClearCache();
            (_fallback as IClearableConfigProvider)?.ClearCache();
        }
    }
}
