using SharedMeta.Client;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core.Transport;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Background-reconnect auth recovery: an access token that expires while the app is suspended must
/// be re-acquired and the handshake retried, rather than dead-ending the reconnect. Before 0.37.2
/// the recovery hook was wired only to the cold-connect path.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ReconnectAuthRecoveryTests
{
    private readonly TestClusterFixture _fixture;

    public ReconnectAuthRecoveryTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Passes everything through to a real in-process connection, but can script the next
    /// SessionConnect to fail — either with a structured reason (0.37.2+ servers) or by throwing
    /// (pre-0.37.2 servers, and transports that raise a 401 client-side).
    /// </summary>
    private sealed class ScriptedAuthConnection : IConnection
    {
        private readonly IConnection _inner;
        private SessionConnectFailureReason? _nextFailure;
        private string? _nextThrow;

        public ScriptedAuthConnection(IConnection inner) => _inner = inner;

        public int SessionConnectCalls { get; private set; }
        public int RedialCount { get; private set; }

        public void FailNextWith(SessionConnectFailureReason reason) => _nextFailure = reason;
        public void ThrowOnNext(string message) => _nextThrow = message;

        public Task<ConnectionSessionConnectResult> SessionConnectAsync(
            string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0,
            string? clientAppVersion = null, ulong clientSignatureHash = 0,
            SessionConnectMode mode = SessionConnectMode.StartNew, long lastCompletedRequestId = 0,
            List<SubscriptionClaim>? claimedSubscriptions = null)
        {
            SessionConnectCalls++;

            if (_nextThrow is { } message)
            {
                _nextThrow = null;
                throw new InvalidOperationException(message);
            }

            if (_nextFailure is { } reason)
            {
                _nextFailure = null;
                return Task.FromResult(new ConnectionSessionConnectResult
                {
                    Success = false,
                    Error = reason == SessionConnectFailureReason.AuthenticationRequired
                        ? "Authentication is required: the connection presented no valid access token."
                        : $"rejected: {reason}",
                    FailureReason = reason,
                });
            }

            return _inner.SessionConnectAsync(playerId, sessionId, lastAcknowledgedSequence,
                clientAppVersion, clientSignatureHash, mode, lastCompletedRequestId, claimedSubscriptions);
        }

        public Task ConnectAsync()
        {
            RedialCount++;
            return _inner.ConnectAsync();
        }

        public string ConnectionId => _inner.ConnectionId;
        public bool IsConnected => _inner.IsConnected;
        public void Dispose() => _inner.Dispose();
        public Task DisconnectAsync() => _inner.DisconnectAsync();
        public Task GracefulDisconnectAsync() => _inner.GracefulDisconnectAsync();
        public Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(Guid sessionId, MetaClientSignature signature)
            => _inner.RegisterClientSignatureAsync(sessionId, signature);
        public Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
            => _inner.SubscribeAsync(entityId, stateTypeName);
        public Task<bool> UnsubscribeAsync(string entityId) => _inner.UnsubscribeAsync(entityId);
        public Task<SessionResponse> RpcCallAsync(RpcCallRequest request) => _inner.RpcCallAsync(request);
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

    private async Task<(MetaClient Client, ScriptedAuthConnection Conn, List<string> Reauths)>
        ConnectAsync(string playerId, bool reacquireSucceeds = true, bool withHandler = true)
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var conn = new ScriptedAuthConnection(server.CreateConnection());
        var reauths = new List<string>();

        var options = new MetaClientOptions
        {
            PlayerId = playerId,
            ClientAppVersion = "1.0.0",
            ClientSignature = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ClientSignature,
        };
        if (withHandler)
        {
            options.OnConnectAuthFailedAsync = ex =>
            {
                reauths.Add(ex.Message);
                return Task.FromResult(reacquireSucceeds);
            };
        }

        // No service registration — these tests exercise the handshake/recovery path only.
        var client = new MetaClient(conn, new MemoryPackMetaSerializer(), options);
        await client.ConnectAsync();
        return (client, conn, reauths);
    }

    [Fact(Timeout = 60_000)]
    public async Task AuthenticationRequired_OnReconnect_ReacquiresAndRetries()
    {
        var (client, conn, reauths) = await ConnectAsync("reauth-" + Guid.NewGuid().ToString("N")[..8]);
        var redialsBefore = conn.RedialCount;

        conn.FailNextWith(SessionConnectFailureReason.AuthenticationRequired);
        await client.ResumeSessionAsync();

        Assert.Single(reauths);
        Assert.Contains("Authentication", reauths[0]);
        // SignalR reads its token during the handshake, so recovery must re-dial the transport —
        // invalidating the token source alone would retry with the same refused credential.
        Assert.Equal(redialsBefore + 1, conn.RedialCount);
        Assert.True(client.Dispatcher.IsSessionConnected);
    }

    [Fact(Timeout = 60_000)]
    public async Task LegacyThrownAuthError_IsTreatedAsAuthenticationRequired()
    {
        // Pre-0.37.2 servers threw HubException("Authentication is required") instead of answering.
        var (client, conn, reauths) = await ConnectAsync("legacy-" + Guid.NewGuid().ToString("N")[..8]);

        conn.ThrowOnNext("An unexpected error occurred invoking 'SessionConnect' on the server. " +
                         "HubException: Authentication is required");
        await client.ResumeSessionAsync();

        Assert.Single(reauths);
        Assert.True(client.Dispatcher.IsSessionConnected);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReacquireDeclined_DoesNotRetry()
    {
        var (client, conn, reauths) = await ConnectAsync(
            "declined-" + Guid.NewGuid().ToString("N")[..8], reacquireSucceeds: false);
        var callsBefore = conn.SessionConnectCalls;

        conn.FailNextWith(SessionConnectFailureReason.AuthenticationRequired);
        await client.ResumeSessionAsync();

        Assert.Single(reauths);
        // Exactly one handshake attempt: the rejected one. No retry after a declined re-acquire.
        Assert.Equal(callsBefore + 1, conn.SessionConnectCalls);
        Assert.False(client.Dispatcher.IsSessionConnected);
    }

    [Fact(Timeout = 60_000)]
    public async Task NoRecoveryHandler_FailsWithoutLooping()
    {
        var (client, conn, reauths) = await ConnectAsync(
            "nohandler-" + Guid.NewGuid().ToString("N")[..8], withHandler: false);
        var callsBefore = conn.SessionConnectCalls;

        conn.FailNextWith(SessionConnectFailureReason.AuthenticationRequired);
        await client.ResumeSessionAsync();

        Assert.Empty(reauths);
        Assert.Equal(callsBefore + 1, conn.SessionConnectCalls);
        Assert.False(client.Dispatcher.IsSessionConnected);
    }

    [Fact(Timeout = 60_000)]
    public async Task IdentityUnknown_IsNotRetried()
    {
        // The account is gone, not the credential. Re-acquiring would produce a different PlayerId,
        // which the live session cannot adopt — every subscription is keyed to the old one.
        var (client, conn, reauths) = await ConnectAsync("ghost-" + Guid.NewGuid().ToString("N")[..8]);
        var callsBefore = conn.SessionConnectCalls;
        ConnectionStatus? lastStatus = null;
        client.OnConnectionStatusChanged += (s, _) => lastStatus = s;

        conn.FailNextWith(SessionConnectFailureReason.IdentityUnknown);
        await client.ResumeSessionAsync();

        Assert.Empty(reauths);
        Assert.Equal(callsBefore + 1, conn.SessionConnectCalls);
        Assert.Equal(ConnectionStatus.Failed, lastStatus);
    }
}
