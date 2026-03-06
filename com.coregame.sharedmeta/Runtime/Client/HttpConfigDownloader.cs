using System;
using System.Net.Http;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Logging;

namespace SharedMeta.Client
{
    /// <summary>
    /// Downloads config bytes from a URL using HttpClient.
    /// Suitable for .NET clients and non-Unity environments.
    /// For Unity, use <see cref="UnityConfigDownloader"/> instead.
    /// </summary>
    public class HttpConfigDownloader : IMetaConfigDownloader
    {
        private readonly HttpClient _http;

        public HttpConfigDownloader()
        {
            _http = new HttpClient();
        }

        public HttpConfigDownloader(HttpClient httpClient)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<byte[]> DownloadAsync(string url)
        {
            MetaLog.Debug($"[HttpConfigDownloader] Downloading: {url}");
            var bytes = await _http.GetByteArrayAsync(url);
            MetaLog.Debug($"[HttpConfigDownloader] Downloaded {bytes.Length} bytes");
            return bytes;
        }
    }
}
