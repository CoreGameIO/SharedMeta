using System;
using System.Threading.Tasks;
using SharedMeta.Core.Logging;
using UnityEngine.Networking;

namespace SharedMeta.Client
{
    /// <summary>
    /// UnityWebRequest-based byte downloader for <see cref="DownloadingConfigProvider{TConfig}"/>.
    /// Pass <see cref="DownloadAsync"/> as the <c>downloader</c> argument when constructing
    /// the provider — Unity projects can't use raw <see cref="System.Net.Http.HttpClient"/>
    /// reliably across all platforms (WebGL, IL2CPP), so this routes through Unity's HTTP stack.
    /// </summary>
    public static class UnityConfigDownloader
    {
        public static Task<byte[]> DownloadAsync(string url)
        {
            var tcs = new TaskCompletionSource<byte[]>();
            var request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SendWebRequest().completed += _ =>
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var bytes = request.downloadHandler.data;
                    MetaLog.Debug($"[UnityConfigDownloader] Downloaded {bytes.Length} bytes from {url}");
                    request.Dispose();
                    tcs.TrySetResult(bytes);
                }
                else
                {
                    var error = $"HTTP {request.responseCode}: {request.error}";
                    MetaLog.Warning($"[UnityConfigDownloader] Failed: {error}");
                    request.Dispose();
                    tcs.TrySetException(new Exception(error));
                }
            };

            return tcs.Task;
        }
    }
}
