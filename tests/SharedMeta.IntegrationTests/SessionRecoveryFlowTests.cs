using System;
using System.Threading.Tasks;
using SharedMeta.Core.Transport;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Session;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.24.0 grain-level session recovery flow. Drives <see cref="ISessionManager"/> directly
/// (no transport / handler) to keep scope tight around the Resume / StartNew protocol +
/// GracefulDisconnect guard.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SessionRecoveryFlowTests
{
    private readonly TestClusterFixture _fixture;

    public SessionRecoveryFlowTests(TestClusterFixture fixture) { _fixture = fixture; }

    private ISessionManager NewGrain(string namePrefix)
        => _fixture.GrainFactory.GetGrain<ISessionManager>($"{namePrefix}-{Guid.NewGuid():N}");

    [Fact(Timeout = 30_000)]
    public async Task StartNew_AllocatesFreshSession()
    {
        var grain = NewGrain("recovery-startnew");
        var sessionId = Guid.NewGuid();

        var result = await grain.ConnectAsync(sessionId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);

        Assert.True(result.Success);
        Assert.True(result.IsNewSession);
        Assert.Equal(SessionConnectFailureReason.None, result.FailureReason);
        Assert.Equal(sessionId, result.SessionId);  // server honours handler-allocated id
    }

    [Fact(Timeout = 30_000)]
    public async Task Resume_SameSession_Succeeds()
    {
        var grain = NewGrain("recovery-resume-happy");
        var sessionId = Guid.NewGuid();

        // First connect — StartNew binds the session.
        var first = await grain.ConnectAsync(sessionId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(first.Success);

        // Resume with the same id — happy path.
        var resume = await grain.ConnectAsync(sessionId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);
        Assert.True(resume.Success);
        Assert.False(resume.IsNewSession);
        Assert.Equal(SessionConnectFailureReason.None, resume.FailureReason);
        Assert.Equal(sessionId, resume.SessionId);
    }

    [Fact(Timeout = 30_000)]
    public async Task Resume_UnknownSession_ReturnsSessionUnknown()
    {
        var grain = NewGrain("recovery-resume-unknown");
        var staleId = Guid.NewGuid();

        // Never registered — Resume with a session id the grain has no record of.
        var resume = await grain.ConnectAsync(staleId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);

        Assert.False(resume.Success);
        Assert.Equal(SessionConnectFailureReason.SessionUnknown, resume.FailureReason);
    }

    [Fact(Timeout = 30_000)]
    public async Task Resume_MismatchedSessionId_ReturnsSessionUnknown()
    {
        var grain = NewGrain("recovery-resume-mismatch");
        var currentId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        Assert.NotEqual(currentId, staleId);

        // Bind a fresh session via StartNew so the grain has a known id.
        var first = await grain.ConnectAsync(currentId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(first.Success);

        // Try to Resume a DIFFERENT id — grain rejects.
        var resume = await grain.ConnectAsync(staleId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);
        Assert.False(resume.Success);
        Assert.Equal(SessionConnectFailureReason.SessionUnknown, resume.FailureReason);
    }

    [Fact(Timeout = 30_000)]
    public async Task StartNew_AfterSessionUnknown_ProducesFreshSession()
    {
        var grain = NewGrain("recovery-startnew-after-unknown");
        var oldId = Guid.NewGuid();

        // 1. Resume with unknown id → SessionUnknown
        var attempt = await grain.ConnectAsync(oldId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);
        Assert.False(attempt.Success);
        Assert.Equal(SessionConnectFailureReason.SessionUnknown, attempt.FailureReason);

        // 2. Fall back to StartNew — protocol guarantees this always works.
        var freshId = Guid.NewGuid();
        var fresh = await grain.ConnectAsync(freshId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(fresh.Success);
        Assert.True(fresh.IsNewSession);
        Assert.Equal(freshId, fresh.SessionId);

        // 3. Subsequent Resume with the fresh id succeeds.
        var resume = await grain.ConnectAsync(freshId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);
        Assert.True(resume.Success);
        Assert.False(resume.IsNewSession);
    }

    [Fact(Timeout = 30_000)]
    public async Task StartNew_ExistingSession_SupersedesAndStartsFresh()
    {
        var grain = NewGrain("recovery-startnew-supersede");
        var firstId = Guid.NewGuid();
        var first = await grain.ConnectAsync(firstId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(first.Success);

        // Second client (or same client after reset) calls StartNew with a different id —
        // the grain supersedes the old session unconditionally.
        var secondId = Guid.NewGuid();
        var second = await grain.ConnectAsync(secondId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(second.Success);
        Assert.True(second.IsNewSession);
        Assert.Equal(secondId, second.SessionId);

        // The old session is now stale — Resume on the original id returns SessionUnknown.
        var resumeOld = await grain.ConnectAsync(firstId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);
        Assert.False(resumeOld.Success);
        Assert.Equal(SessionConnectFailureReason.SessionUnknown, resumeOld.FailureReason);
    }

    [Fact(Timeout = 30_000)]
    public async Task GracefulDisconnect_StaleSessionId_LeavesCurrentSessionIntact()
    {
        var grain = NewGrain("recovery-graceful-stale");
        var currentId = Guid.NewGuid();
        var staleId = Guid.NewGuid();

        var current = await grain.ConnectAsync(currentId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(current.Success);

        // Old client's GracefulDisconnect arrives late, after a new session is bound.
        // The grain MUST ignore it — otherwise the live session's state would be wiped.
        await grain.GracefulDisconnectAsync(staleId);

        // Current session still exists.
        var resume = await grain.ConnectAsync(currentId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);
        Assert.True(resume.Success);
        Assert.Equal(currentId, resume.SessionId);
    }

    [Fact(Timeout = 30_000)]
    public async Task GracefulDisconnect_MatchingSessionId_ClearsState()
    {
        var grain = NewGrain("recovery-graceful-match");
        var sessionId = Guid.NewGuid();
        var first = await grain.ConnectAsync(sessionId, 0, SessionConnectMode.StartNew, 0, null, null, 0UL);
        Assert.True(first.Success);

        // Honest graceful disconnect from the same session.
        await grain.GracefulDisconnectAsync(sessionId);

        // After cleanup the grain's CurrentSessionId is empty; a Resume on the just-closed
        // id no longer matches → SessionUnknown.
        var resume = await grain.ConnectAsync(sessionId, 0, SessionConnectMode.Resume, 0, null, null, 0UL);
        Assert.False(resume.Success);
        Assert.Equal(SessionConnectFailureReason.SessionUnknown, resume.FailureReason);
    }
}


