#if UNITY_2018_1_OR_NEWER
using System;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Client
{
    /// <summary>
    /// 0.26.7+ Sugar around <see cref="DownloadingConfigProvider{TConfig}"/> registration.
    /// Wraps the four-line "build URL resolver from the MetaClient, plumb downloader + serializer,
    /// register on the resolver" boilerplate in one typed call. Lives in
    /// <c>SharedMeta.Client.Config</c> asmdef alongside <see cref="UnityConfigDownloader"/> so
    /// Unity projects get a parameterless default — no matter which meta transport they use.
    /// </summary>
    public static class DownloadingConfigProviderExtensions
    {
        /// <summary>
        /// Register a <see cref="DownloadingConfigProvider{TConfig}"/> on this client's resolver
        /// for the given <typeparamref name="TConfig"/>, keyed by the owning
        /// <typeparamref name="TState"/>. URL resolver delegates to
        /// <c>IConnection.GetConfigDownloadUrlAsync(stateTypeName, version)</c>; downloader
        /// defaults to <see cref="UnityConfigDownloader.DownloadAsync"/>.
        /// <para>
        /// Call before <see cref="IMetaServiceResolver.RegisterAllServices"/> (so it clobbers the
        /// auto-registered <c>StaticConfigProvider&lt;TConfig&gt;</c>), or after — the resolver
        /// supports overwrite either way.
        /// </para>
        /// </summary>
        /// <param name="client">MetaClient hosting the connection used for URL resolution.</param>
        /// <param name="downloader">Byte-fetcher. Default <c>null</c> → <see cref="UnityConfigDownloader.DownloadAsync"/>.</param>
        /// <param name="cache">Optional cross-session cache. <c>new FileConfigCache&lt;TConfig&gt;(path, serializer)</c> is a common choice.</param>
        public static void RegisterDownloadingConfigProvider<TConfig, TState>(
            this MetaClient client,
            Func<string, Task<byte[]>>? downloader = null,
            IClientMetaConfigCache<TConfig>? cache = null)
            where TConfig : class
            where TState : ISharedState
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            downloader ??= UnityConfigDownloader.DownloadAsync;
            var stateTypeName = typeof(TState).FullName
                ?? throw new InvalidOperationException(
                    $"State type {typeof(TState).Name} has no FullName — cannot resolve config download URL.");

            client.Resolver.RegisterConfigProvider<TConfig>(
                new DownloadingConfigProvider<TConfig>(
                    urlResolver: client.ConfigDownloadUrlResolver(stateTypeName),
                    downloader:  downloader,
                    serializer:  client.Serializer,
                    cache:       cache));
        }
    }
}
#endif
