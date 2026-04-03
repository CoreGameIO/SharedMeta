using Orleans;
using SharedMeta.Auth;
using SharedMeta.IntegrationTests.Infrastructure;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for the auth system: device login, platform login, link/unlink.
/// Uses Orleans TestCluster with in-memory grain storage.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class AuthTests
{
    private readonly IGrainFactory _grainFactory;

    public AuthTests(TestClusterFixture fixture)
    {
        _grainFactory = fixture.GrainFactory;
    }

    // ========================
    // Device Login
    // ========================

    [Fact(Timeout = 30_000)]
    public async Task DeviceLogin_CreatesNewPlayer()
    {
        var grain = _grainFactory.GetGrain<IAuthGrain>("device-test-new-" + Guid.NewGuid());
        var result = await grain.LoginAsync();

        Assert.True(result.IsNewPlayer);
        Assert.NotEmpty(result.PlayerId);
    }

    [Fact(Timeout = 30_000)]
    public async Task DeviceLogin_ReturnsSamePlayer_OnSecondLogin()
    {
        var deviceId = "device-test-same-" + Guid.NewGuid();
        var grain = _grainFactory.GetGrain<IAuthGrain>(deviceId);

        var first = await grain.LoginAsync();
        var second = await grain.LoginAsync();

        Assert.True(first.IsNewPlayer);
        Assert.False(second.IsNewPlayer);
        Assert.Equal(first.PlayerId, second.PlayerId);
    }

    // ========================
    // Platform Login
    // ========================

    [Fact(Timeout = 30_000)]
    public async Task PlatformLogin_CreatesNewPlayer()
    {
        var authKey = "google:test-user-" + Guid.NewGuid();
        var grain = _grainFactory.GetGrain<IAuthGrain>(authKey);

        var result = await grain.LoginAsync();

        Assert.True(result.IsNewPlayer);
        Assert.NotEmpty(result.PlayerId);
    }

    [Fact(Timeout = 30_000)]
    public async Task PlatformLogin_ReturnsSamePlayer_OnSecondLogin()
    {
        var authKey = "steam:test-steam-" + Guid.NewGuid();
        var grain = _grainFactory.GetGrain<IAuthGrain>(authKey);

        var first = await grain.LoginAsync();
        var second = await grain.LoginAsync();

        Assert.Equal(first.PlayerId, second.PlayerId);
        Assert.False(second.IsNewPlayer);
    }

    // ========================
    // Link
    // ========================

    [Fact(Timeout = 30_000)]
    public async Task Link_AssociatesPlatformWithExistingPlayer()
    {
        // Create player via device login
        var deviceId = "device-link-test-" + Guid.NewGuid();
        var deviceGrain = _grainFactory.GetGrain<IAuthGrain>(deviceId);
        var loginResult = await deviceGrain.LoginAsync();
        var playerId = loginResult.PlayerId;

        // Link a platform to this player
        var platformKey = "google:link-test-" + Guid.NewGuid();
        var platformGrain = _grainFactory.GetGrain<IAuthGrain>(platformKey);
        var linkResult = await platformGrain.LinkAsync(playerId);

        Assert.True(linkResult.Success);

        // Login via platform should return same player
        var platformLogin = await platformGrain.LoginAsync();
        Assert.Equal(playerId, platformLogin.PlayerId);
        Assert.False(platformLogin.IsNewPlayer);
    }

    [Fact(Timeout = 30_000)]
    public async Task Link_FailsIfAlreadyLinkedToDifferentPlayer()
    {
        // Create two different players
        var device1 = "device-conflict-1-" + Guid.NewGuid();
        var device2 = "device-conflict-2-" + Guid.NewGuid();
        var player1 = (await _grainFactory.GetGrain<IAuthGrain>(device1).LoginAsync()).PlayerId;
        var player2 = (await _grainFactory.GetGrain<IAuthGrain>(device2).LoginAsync()).PlayerId;

        // Link platform to player1
        var platformKey = "apple:conflict-test-" + Guid.NewGuid();
        var platformGrain = _grainFactory.GetGrain<IAuthGrain>(platformKey);
        var link1 = await platformGrain.LinkAsync(player1);
        Assert.True(link1.Success);

        // Try to link same platform to player2 — should fail
        var link2 = await platformGrain.LinkAsync(player2);
        Assert.False(link2.Success);
        Assert.Equal(player1, link2.ExistingPlayerId);
    }

    [Fact(Timeout = 30_000)]
    public async Task Link_SucceedsIfAlreadyLinkedToSamePlayer()
    {
        var deviceId = "device-idempotent-" + Guid.NewGuid();
        var playerId = (await _grainFactory.GetGrain<IAuthGrain>(deviceId).LoginAsync()).PlayerId;

        var platformKey = "steam:idempotent-test-" + Guid.NewGuid();
        var platformGrain = _grainFactory.GetGrain<IAuthGrain>(platformKey);

        var link1 = await platformGrain.LinkAsync(playerId);
        var link2 = await platformGrain.LinkAsync(playerId);

        Assert.True(link1.Success);
        Assert.True(link2.Success); // idempotent
    }

    // ========================
    // Unlink
    // ========================

    [Fact(Timeout = 30_000)]
    public async Task Unlink_RemovesAuthKey()
    {
        // Create player with device + platform
        var deviceId = "device-unlink-" + Guid.NewGuid();
        var deviceGrain = _grainFactory.GetGrain<IAuthGrain>(deviceId);
        var playerId = (await deviceGrain.LoginAsync()).PlayerId;

        var platformKey = "google:unlink-test-" + Guid.NewGuid();
        var platformGrain = _grainFactory.GetGrain<IAuthGrain>(platformKey);
        await platformGrain.LinkAsync(playerId);

        // Unlink platform
        var unlinkResult = await platformGrain.UnlinkAsync(playerId);
        Assert.True(unlinkResult);

        // Platform grain should now be empty
        var platformPlayerId = await platformGrain.GetPlayerIdAsync();
        Assert.Null(platformPlayerId);
    }

    [Fact(Timeout = 30_000)]
    public async Task Unlink_FailsForWrongPlayer()
    {
        var deviceId = "device-unlink-wrong-" + Guid.NewGuid();
        var playerId = (await _grainFactory.GetGrain<IAuthGrain>(deviceId).LoginAsync()).PlayerId;

        var platformKey = "steam:unlink-wrong-" + Guid.NewGuid();
        var platformGrain = _grainFactory.GetGrain<IAuthGrain>(platformKey);
        await platformGrain.LinkAsync(playerId);

        // Try to unlink with different player — should fail
        var result = await platformGrain.UnlinkAsync("totally-different-player");
        Assert.False(result);

        // Should still be linked
        var stillLinked = await platformGrain.GetPlayerIdAsync();
        Assert.Equal(playerId, stillLinked);
    }

    // ========================
    // Auth Index
    // ========================

    [Fact(Timeout = 30_000)]
    public async Task AuthIndex_TracksAllLinkedKeys()
    {
        var deviceId = "device-index-" + Guid.NewGuid();
        var deviceGrain = _grainFactory.GetGrain<IAuthGrain>(deviceId);
        var playerId = (await deviceGrain.LoginAsync()).PlayerId;

        // Link two platforms
        var googleKey = "google:index-test-" + Guid.NewGuid();
        var steamKey = "steam:index-test-" + Guid.NewGuid();
        await _grainFactory.GetGrain<IAuthGrain>(googleKey).LinkAsync(playerId);
        await _grainFactory.GetGrain<IAuthGrain>(steamKey).LinkAsync(playerId);

        // Index should have 3 keys: device + google + steam
        var index = _grainFactory.GetGrain<IAuthIndexGrain>(playerId);
        var keys = await index.GetKeysAsync();

        Assert.Contains(deviceId, keys);
        Assert.Contains(googleKey, keys);
        Assert.Contains(steamKey, keys);
        Assert.Equal(3, keys.Count);
    }

    [Fact(Timeout = 30_000)]
    public async Task AuthIndex_UpdatesOnUnlink()
    {
        var deviceId = "device-index-unlink-" + Guid.NewGuid();
        var playerId = (await _grainFactory.GetGrain<IAuthGrain>(deviceId).LoginAsync()).PlayerId;

        var platformKey = "apple:index-unlink-" + Guid.NewGuid();
        await _grainFactory.GetGrain<IAuthGrain>(platformKey).LinkAsync(playerId);

        // Verify 2 keys
        var index = _grainFactory.GetGrain<IAuthIndexGrain>(playerId);
        var beforeKeys = await index.GetKeysAsync();
        Assert.Equal(2, beforeKeys.Count);

        // Unlink platform
        await _grainFactory.GetGrain<IAuthGrain>(platformKey).UnlinkAsync(playerId);

        // Should be 1 key
        var afterKeys = await index.GetKeysAsync();
        Assert.Single(afterKeys);
        Assert.Contains(deviceId, afterKeys);
    }

    // ========================
    // Full Flow: Device → Link Platform → Unlink Device → Login via Platform
    // ========================

    [Fact(Timeout = 30_000)]
    public async Task FullFlow_DeviceToLinkToUnlinkDevice()
    {
        // 1. Start with device login
        var deviceId = "device-fullflow-" + Guid.NewGuid();
        var deviceGrain = _grainFactory.GetGrain<IAuthGrain>(deviceId);
        var playerId = (await deviceGrain.LoginAsync()).PlayerId;

        // 2. Link Google
        var googleKey = "google:fullflow-" + Guid.NewGuid();
        var googleGrain = _grainFactory.GetGrain<IAuthGrain>(googleKey);
        var linkResult = await googleGrain.LinkAsync(playerId);
        Assert.True(linkResult.Success);

        // 3. Unlink device (player still accessible via Google)
        var unlinkResult = await deviceGrain.UnlinkAsync(playerId);
        Assert.True(unlinkResult);

        // 4. Device login now creates a NEW player (old mapping cleared)
        var newDeviceLogin = await deviceGrain.LoginAsync();
        Assert.True(newDeviceLogin.IsNewPlayer);
        Assert.NotEqual(playerId, newDeviceLogin.PlayerId);

        // 5. Google login still returns the original player
        var googleLogin = await googleGrain.LoginAsync();
        Assert.Equal(playerId, googleLogin.PlayerId);
        Assert.False(googleLogin.IsNewPlayer);

        // 6. Index for original player has only Google key now
        var index = _grainFactory.GetGrain<IAuthIndexGrain>(playerId);
        var keys = await index.GetKeysAsync();
        Assert.Single(keys);
        Assert.Contains(googleKey, keys);
    }
}
