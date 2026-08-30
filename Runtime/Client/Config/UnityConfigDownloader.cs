#if UNITY_2018_1_OR_NEWER
using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace SharedMeta.Client
{
    /// <summary>
    /// UnityWebRequest-based byte downloader for <see cref="DownloadingConfigProvider{TConfig}"/>.
    /// Pass <see cref="DownloadAsync"/> as the <c>downloader</c> argument when constructing
    /// the provider — Unity projects can't use raw <see cref="System.Net.Http.HttpClient"/>
    /// reliably across all platforms (WebGL, IL2CPP), so this routes through Unity's HTTP stack.
    ///
    /// <para>
    /// 0.26.7+ Moved out of <c>SharedMeta.Transport.Http</c> asmdef into the dedicated
    /// <c>SharedMeta.Client.Config</c> asmdef so projects using SignalR / BestHTTP transports
    /// don't have to pull in the HTTP-polling asmdef (and its Newtonsoft.Json constraint) just
    /// to use the downloader.
    /// </para>
    ///
    /// <see cref="UnityWebRequest.Get"/> and <see cref="DownloadHandlerBuffer"/> require the
    /// Unity main thread. Async config resolution may resume on a threadpool thread (after a
    /// <c>ConfigureAwait(false)</c> upstream), so this downloader marshals all UnityWebRequest
    /// construction onto the main-thread <see cref="SynchronizationContext"/> captured at
    /// runtime initialization.
    /// </summary>
    public static class UnityConfigDownloader
    {
        private static SynchronizationContext? _mainThreadContext;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CaptureMainThreadContext()
        {
            // SubsystemRegistration runs on the Unity main thread before any scene loads,
            // so SynchronizationContext.Current is the UnitySynchronizationContext we need.
            _mainThreadContext = SynchronizationContext.Current;
        }

        public static Task<byte[]> DownloadAsync(string url)
        {
            var tcs = new TaskCompletionSource<byte[]>();

            void Run()
            {
                try
                {
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
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            // If we're already on the captured main-thread context, run inline; otherwise post.
            // Falling back to inline (without context) preserves existing behaviour for callers
            // already on the main thread before RuntimeInitializeOnLoadMethod fired.
            if (_mainThreadContext == null || _mainThreadContext == SynchronizationContext.Current)
                Run();
            else
                _mainThreadContext.Post(_ => Run(), null);

            return tcs.Task;
        }
    }
}
#endif
