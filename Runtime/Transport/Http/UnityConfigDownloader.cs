using System;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using UnityEngine.Networking;

namespace SharedMeta.Client
{
    /// <summary>
    /// Downloads config bytes using UnityWebRequest.
    /// Use this instead of HttpConfigDownloader in Unity projects.
    /// </summary>
    public class UnityConfigDownloader : IMetaConfigDownloader
    {
        public Task<byte[]> DownloadAsync(string url)
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
