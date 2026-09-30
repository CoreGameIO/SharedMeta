using SharedMeta.Client;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The client decides what a failed RPC means — keep it for a resend after re-handshake, fail it,
/// or end the session — from the response's error kind. It used to match words in the server's
/// free-text message, so rewording a message changed recovery.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class RpcErrorKindTests
{
    private readonly TestClusterFixture _fixture;

    public RpcErrorKindTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task SessionNotConnected_KeepsCallPending_WhateverTheWording()
    {
        var (client, conn, api) = await ConnectAsync();
        await using var _ = client;

        conn.RespondNextRpcWith(new SessionResponse
        {
            Error = "no session is bound to this connection",
            ErrorKind = SessionErrorKind.SessionNotConnected,
        });
        var call = api.BumpReplaceAsync(1);
        await Task.Delay(300);

        Assert.False(call.IsCompleted, "A transient not-connected response failed the call instead of keeping it for resend.");
        Assert.Equal(1, ((ClientDispatcher)client.Dispatcher).PendingRequestCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task SessionSuperseded_EndsSession_WhateverTheWording()
    {
        var (client, conn, api) = await ConnectAsync();
        await using var _ = client;

        var superseded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnSessionSuperseded += reason => superseded.TrySetResult(reason);
        conn.RespondNextRpcWith(new SessionResponse
        {
            Error = "another connection took over",
            ErrorKind = SessionErrorKind.SessionSuperseded,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.BumpReplaceAsync(1));
        await superseded.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<(MetaClient Client, ScriptedRpcConnection Conn, ForcePatchFixtureServiceApiClient Api)> ConnectAsync()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var conn = new ScriptedRpcConnection(server.CreateConnection());
        var client = new MetaClient(conn, new SharedMeta.Serialization.MemoryPack.MemoryPackMetaSerializer(), new MetaClientOptions
        {
            PlayerId = $"errkind-{Guid.NewGuid():N}",
            ClientAppVersion = "1.0.0",
            ClientSignature = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ClientSignature,
        });
        client.Resolver.RegisterAllServices();
        await client.ConnectAsync();
        var api = await client.Resolver.GetServiceAsync<ForcePatchFixtureServiceApiClient>($"errkind_{Guid.NewGuid():N}");
        return (client, conn, api);
    }

    /// <summary>Real in-process connection whose next RPC can be answered with a scripted response.</summary>
    private sealed class ScriptedRpcConnection : IConnection
    {
        private readonly IConnection _inner;
        private SessionResponse? _nextRpc;

        public ScriptedRpcConnection(IConnection inner) => _inner = inner;

        public void RespondNextRpcWith(SessionResponse response) => _nextRpc = response;

        public Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            if (Interlocked.Exchange(ref _nextRpc, null) is { } scripted)
                return Task.FromResult(scripted);
            return _inner.RpcCallAsync(request);
        }

        public Task<ConnectionSessionConnectResult> SessionConnectAsync(
            string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0,
            string? clientAppVersion = null, ulong clientSignatureHash = 0,
            SessionConnectMode mode = SessionConnectMode.StartNew, long lastCompletedRequestId = 0,
            List<SubscriptionClaim>? claimedSubscriptions = null)
            => _inner.SessionConnectAsync(playerId, sessionId, lastAcknowledgedSequence,
                clientAppVersion, clientSignatureHash, mode, lastCompletedRequestId, claimedSubscriptions);

        public Task ConnectAsync() => _inner.ConnectAsync();
        public string ConnectionId => _inner.ConnectionId;
        public bool IsConnected => _inner.IsConnected;
        public void Dispose() => _inner.Dispose();
        public Task DisconnectAsync() => _inner.DisconnectAsync();
        public Task GracefulDisconnectAsync() => _inner.GracefulDisconnectAsync();
        public Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(Guid sessionId, MetaClientSignature signature)
            => _inner.RegisterClientSignatureAsync(sessionId, signature);
        public Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, ushort stateTypeId)
            => _inner.SubscribeAsync(entityId, stateTypeId);
        public Task<bool> UnsubscribeAsync(string entityId, ushort stateTypeId) => _inner.UnsubscribeAsync(entityId, stateTypeId);
        public Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request) => _inner.QueryCallAsync(request);
        public Task SignalCallAsync(SignalCallRequest request) => _inner.SignalCallAsync(request);
        public Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request) => _inner.SetDebugOptionsAsync(request);
        public Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request)
            => _inner.SendDesyncReportAsync(request);
        public Task AcknowledgeSequenceAsync(long sequenceNumber) => _inner.AcknowledgeSequenceAsync(sequenceNumber);
        public Task<string?> GetConfigDownloadUrlAsync(string configTypeName, SharedMeta.Core.MetaConfigVersion version)
            => _inner.GetConfigDownloadUrlAsync(configTypeName, version);

        public event Action<SessionResponse>? OnBatch
        {
            add => _inner.OnBatch += value;
            remove => _inner.OnBatch -= value;
        }
        public event Action<SessionNotice>? OnNotice
        {
            add => _inner.OnNotice += value;
            remove => _inner.OnNotice -= value;
        }
        public event Action<string>? OnSessionTerminated
        {
            add => _inner.OnSessionTerminated += value;
            remove => _inner.OnSessionTerminated -= value;
        }
        public event Action<string>? OnRequireSessionReconnect
        {
            add => _inner.OnRequireSessionReconnect += value;
            remove => _inner.OnRequireSessionReconnect -= value;
        }
        public event Action<TransportDisconnectReason>? OnDisconnected
        {
            add => _inner.OnDisconnected += value;
            remove => _inner.OnDisconnected -= value;
        }
        public event Action? OnReconnecting
        {
            add => _inner.OnReconnecting += value;
            remove => _inner.OnReconnecting -= value;
        }
        public event Action? OnReconnected
        {
            add => _inner.OnReconnected += value;
            remove => _inner.OnReconnected -= value;
        }
    }
}
