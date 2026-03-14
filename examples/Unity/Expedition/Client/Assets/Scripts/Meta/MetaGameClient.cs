using System;
using UnityEngine;
using SharedMeta.Client;
using SharedMeta.Client.Auth;
using SharedMeta.Core.Auth;
using SharedMeta.Client.Network;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;
using SharedMeta.Serialization.MemoryPack;
using Expedition.Shared.Client;

/// <summary>
/// SharedMeta client MonoBehaviour.
/// Manages MetaClient lifecycle: connection, broadcast processing, cleanup.
/// </summary>
public class MetaGameClient : MonoBehaviour
{
    [Header("Connection")]
    public string serverUrl = "http://localhost:5000/meta";

    public MetaClient Client { get; private set; }

    private async void Start()
    {
        MetaLog.SetLogger(new UnityMetaLogger());

        // Authenticate — reuses cached token if still valid
        // Auth URL uses server root (serverUrl points to hub path /meta)
        var baseUrl = serverUrl.EndsWith("/meta") ? serverUrl.Substring(0, serverUrl.Length - 5) : serverUrl;
        var tokenStorage = new PlayerPrefsTokenStorage();
        var login = await MetaAuth.EnsureAuthenticatedAsync(
            baseUrl.TrimEnd('/') + "/meta/auth",
            SystemInfo.deviceUniqueIdentifier,
            tokenStorage);
        var accessToken = login.Token;
        var playerId = login.PlayerId;

        var connection = new SignalRConnection(serverUrl, accessToken);

        var serializer = new MemoryPackMetaSerializer();

        Client = new MetaClient(connection, serializer, new MetaClientOptions
        {
            PlayerId = playerId,
        });

        Client.Resolver.RegisterAllServices();

        // Config download: enable file caching and download for server-provided configs.
        // var resolver = (MetaServiceResolver)Client.Resolver;
        // resolver.ConfigCache = new FileConfigCache(Application.persistentDataPath + "/config-cache", serializer);
        // resolver.ConfigDownloader = new UnityConfigDownloader();

        Debug.Log("[SharedMeta] Connecting...");
        await Client.ConnectAsync();
        Debug.Log($"[SharedMeta] Connected! PlayerId: {Client.PlayerId}");

        // UserOwned service — convenience method (no entityId needed)
        var profile = await Client.GetExpeditionProfileServiceAsync();
        await profile.UpdateEnergyAsync();
        Debug.Log($"Profile: Energy={profile.State.Energy}, Money={profile.State.Money}");
    }

    private void Update()
    {
        Client?.Dispatcher?.ProcessPendingBroadcasts();
    }

    private void OnDestroy()
    {
        Client?.Dispose();
    }
}
