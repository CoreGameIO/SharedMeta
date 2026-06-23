using System;
using System.Threading.Tasks;
using Orleans;
using SharedMeta.Auth;
using SharedMeta.IntegrationTests.Infrastructure;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for the per-player refresh-token grain: issue, rotation, reuse detection,
/// expiry, and revocation. Uses the Orleans TestCluster with in-memory grain storage.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class RefreshTokenTests
{
    private static readonly TimeSpan Life = TimeSpan.FromDays(30);
    private readonly IGrainFactory _grainFactory;

    public RefreshTokenTests(TestClusterFixture fixture)
    {
        _grainFactory = fixture.GrainFactory;
    }

    private IRefreshTokenGrain Grain(string playerId) => _grainFactory.GetGrain<IRefreshTokenGrain>(playerId);

    [Fact(Timeout = 30_000)]
    public async Task Rotate_ValidToken_IssuesNewAndInvalidatesOld()
    {
        var playerId = "rt-rotate-" + Guid.NewGuid();
        var grain = Grain(playerId);

        var t0 = await grain.IssueAsync("device-a", Life);
        Assert.False(string.IsNullOrEmpty(t0));

        var r1 = await grain.RotateAsync(t0, Life);
        Assert.Equal(RefreshOutcome.Valid, r1.Outcome);
        Assert.False(string.IsNullOrEmpty(r1.NewRefreshToken));
        Assert.NotEqual(t0, r1.NewRefreshToken);
        Assert.Equal("device-a", r1.AuthKey);

        // The rotated-out token is no longer the current one — but replaying it trips reuse detection
        // (covered separately). The freshly issued token is valid for the next rotation.
        var r2 = await grain.RotateAsync(r1.NewRefreshToken!, Life);
        Assert.Equal(RefreshOutcome.Valid, r2.Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Rotate_ReplayUsedToken_RevokesWholeFamily()
    {
        var playerId = "rt-reuse-" + Guid.NewGuid();
        var grain = Grain(playerId);

        var t0 = await grain.IssueAsync("device-a", Life);
        var r1 = await grain.RotateAsync(t0, Life);
        Assert.Equal(RefreshOutcome.Valid, r1.Outcome);
        var t1 = r1.NewRefreshToken!;

        // Replay the already-used t0 → reuse detected.
        var replay = await grain.RotateAsync(t0, Life);
        Assert.Equal(RefreshOutcome.Reused, replay.Outcome);

        // Family is revoked: the legitimately-rotated t1 is now dead too.
        var after = await grain.RotateAsync(t1, Life);
        Assert.Equal(RefreshOutcome.Invalid, after.Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Rotate_UnknownToken_ReturnsInvalid()
    {
        var playerId = "rt-unknown-" + Guid.NewGuid();
        var grain = Grain(playerId);

        // Well-formed but never issued.
        var fake = $"{playerId}.{Guid.NewGuid():N}.abcdefghijklmnop";
        var result = await grain.RotateAsync(fake, Life);
        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);

        // Malformed.
        var bad = await grain.RotateAsync("not-a-token", Life);
        Assert.Equal(RefreshOutcome.Invalid, bad.Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task Issue_ExpiredFamily_RotateReturnsInvalid()
    {
        var playerId = "rt-expired-" + Guid.NewGuid();
        var grain = Grain(playerId);

        // Already-expired lifetime → the session is pruned on the next access.
        var t0 = await grain.IssueAsync("device-a", TimeSpan.FromSeconds(-1));
        var result = await grain.RotateAsync(t0, Life);
        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task MultipleSessions_AreIndependent()
    {
        var playerId = "rt-multi-" + Guid.NewGuid();
        var grain = Grain(playerId);

        var deviceToken = await grain.IssueAsync("device-a", Life);
        var phoneToken = await grain.IssueAsync("device-b", Life);

        // Rotating one session doesn't affect the other.
        var rDevice = await grain.RotateAsync(deviceToken, Life);
        Assert.Equal(RefreshOutcome.Valid, rDevice.Outcome);

        var rPhone = await grain.RotateAsync(phoneToken, Life);
        Assert.Equal(RefreshOutcome.Valid, rPhone.Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task RevokeByAuthKey_KillsThatCredentialsSessions()
    {
        var playerId = "rt-revoke-key-" + Guid.NewGuid();
        var grain = Grain(playerId);

        var deviceToken = await grain.IssueAsync("device-a", Life);
        var googleToken = await grain.IssueAsync("google:123", Life);

        await grain.RevokeByAuthKeyAsync("device-a");

        Assert.Equal(RefreshOutcome.Invalid, (await grain.RotateAsync(deviceToken, Life)).Outcome);
        Assert.Equal(RefreshOutcome.Valid, (await grain.RotateAsync(googleToken, Life)).Outcome);
    }

    [Fact(Timeout = 30_000)]
    public async Task RevokeAll_KillsEverySession()
    {
        var playerId = "rt-revoke-all-" + Guid.NewGuid();
        var grain = Grain(playerId);

        var a = await grain.IssueAsync("device-a", Life);
        var b = await grain.IssueAsync("device-b", Life);

        await grain.RevokeAllAsync();

        Assert.Equal(RefreshOutcome.Invalid, (await grain.RotateAsync(a, Life)).Outcome);
        Assert.Equal(RefreshOutcome.Invalid, (await grain.RotateAsync(b, Life)).Outcome);
    }
}
