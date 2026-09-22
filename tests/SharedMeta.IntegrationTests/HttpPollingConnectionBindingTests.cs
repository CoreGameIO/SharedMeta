using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Transport.HttpPolling;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// On HTTP polling the connection id is client-chosen and rides in a plain <c>X-Connection-Id</c>
/// header, and no endpoint past the handshake re-checks the token. Without a binding to the
/// authenticated subject that id is the only per-request credential: anything that leaks one
/// (logs, a proxy, a shared device) hands over the session, and a handshake replayed onto someone
/// else's live id rebinds their handler and puts both callers on one poll queue.
///
/// SignalR does not have the problem — its connection id is server-assigned and tied to the
/// socket. These tests pin the polling equivalent.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class HttpPollingConnectionBindingTests
{
    private readonly TestClusterFixture _fixture;

    public HttpPollingConnectionBindingTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpPollingConnectionManager CreateManager()
        => new(_fixture.CreateHandlerFactory(), _fixture.LoggerFactory);

    [Fact]
    public void FirstHandshake_BindsConnectionToSubject()
    {
        using var mgr = CreateManager();

        var state = mgr.GetOrCreateConnection("conn-bind-1", "alice");

        Assert.NotNull(state);
        Assert.Equal("alice", state!.OwnerSubject);
    }

    /// <summary>
    /// The attack: Mallory learns Alice's connection id and replays the handshake on it. Before
    /// the binding this rebound Alice's live handler to Mallory's identity.
    /// </summary>
    [Fact]
    public void HandshakeOnAnotherSubjectsConnection_IsRejected()
    {
        using var mgr = CreateManager();
        mgr.GetOrCreateConnection("conn-bind-2", "alice");

        var hijacked = mgr.GetOrCreateConnection("conn-bind-2", "mallory");

        Assert.Null(hijacked);
        // Alice's connection must survive the attempt untouched.
        Assert.Equal("alice", mgr.GetConnection("conn-bind-2")!.OwnerSubject);
    }

    [Fact]
    public void OwnerCanKeepUsingItsOwnConnection()
    {
        using var mgr = CreateManager();
        mgr.GetOrCreateConnection("conn-bind-3", "alice");

        var again = mgr.GetOrCreateConnection("conn-bind-3", "alice");

        Assert.NotNull(again);
        Assert.True(again!.MatchesOwner("alice"));
    }

    /// <summary>
    /// A stolen id presented without any token must not work either — that is the plain
    /// header-replay case, and it is what every endpoint past the handshake sees.
    /// </summary>
    [Fact]
    public void AnonymousRequestOnBoundConnection_IsRejected()
    {
        using var mgr = CreateManager();
        mgr.GetOrCreateConnection("conn-bind-4", "alice");

        Assert.False(mgr.GetConnection("conn-bind-4")!.MatchesOwner(null));
    }

    /// <summary>
    /// Anonymous deployments must keep working. With <c>RequireAuthentication = false</c> there is
    /// no authenticated identity to protect — the framework already lets such clients connect with
    /// any PlayerId, so binding would only break them without adding a guarantee.
    /// </summary>
    [Fact]
    public void AnonymousConnection_AcceptsAnyCaller()
    {
        using var mgr = CreateManager();

        var state = mgr.GetOrCreateConnection("conn-bind-5", subject: null);

        Assert.NotNull(state);
        Assert.Null(state!.OwnerSubject);
        Assert.True(state.MatchesOwner(null));
        Assert.True(state.MatchesOwner("anyone"));
    }
}
