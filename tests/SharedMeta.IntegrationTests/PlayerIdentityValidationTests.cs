using Orleans;
using SharedMeta.Auth;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// SessionConnect identity gate: a token whose account no longer exists must be rejected instead
/// of silently materialising entity state under a PlayerId nobody can log in as again.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class PlayerIdentityValidationTests
{
    private readonly TestClusterFixture _fixture;

    public PlayerIdentityValidationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Knows exactly the ids it was handed; everything else is a ghost.</summary>
    private sealed class FakeIdentityValidator : IPlayerIdentityValidator
    {
        private readonly HashSet<string> _known;
        public int Calls { get; private set; }

        public FakeIdentityValidator(params string[] known) => _known = new HashSet<string>(known);

        public Task<bool> ExistsAsync(string playerId)
        {
            Calls++;
            return Task.FromResult(_known.Contains(playerId));
        }
    }

    private static SessionConnectRequest ConnectRequest(string playerId) => new()
    {
        PlayerId = playerId,
        ClientVersion = "1.0.0",
        Mode = SessionConnectMode.StartNew,
    };

    private sealed class NullBroadcastSender : IBroadcastSender
    {
        public void SendBroadcast(SessionResponse message) { }
        public void SendSessionTerminated(string reason) { }
        public void SendEntityDeactivating(string entityId) { }
        public void Reset() { }
    }

    private IMetaConnectionHandler CreateHandler(
        MetaTransportOptions? options,
        IPlayerIdentityValidator? validator)
        => _fixture.CreateHandlerFactory(options, validator)
            .Create("conn-" + Guid.NewGuid().ToString("N")[..8], new NullBroadcastSender());

    [Fact(Timeout = 30_000)]
    public async Task UnknownIdentity_Rejected_WithIdentityUnknownReason()
    {
        var ghost = "ghost-" + Guid.NewGuid().ToString("N")[..8];
        var validator = new FakeIdentityValidator(); // knows nobody
        var handler = CreateHandler(new MetaTransportOptions { RequireAuthentication = true }, validator);

        var response = await handler.SessionConnectAsync(ConnectRequest(ghost));

        Assert.False(response.Success);
        Assert.Equal(SessionConnectFailureReason.IdentityUnknown, response.FailureReason);
        Assert.Equal(1, validator.Calls);
        // Client-side auth-failure detection keys off this word to route to a full re-login
        // rather than a plain connect retry.
        Assert.Contains("Authentication", response.Error);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnknownIdentity_LeavesNoServerSideTraceOfTheGhost()
    {
        var ghost = "ghost-notrace-" + Guid.NewGuid().ToString("N")[..8];
        var handler = CreateHandler(
            new MetaTransportOptions { RequireAuthentication = true }, new FakeIdentityValidator());

        await handler.SessionConnectAsync(ConnectRequest(ghost));

        // The per-player version grain persists on first touch, so it doubles as a probe that the
        // identity gate runs before anything downstream writes state for the rejected id.
        var recorded = await _fixture.GrainFactory
            .GetGrain<IPlayerVersionGrain>(ghost).GetMaxClientVersionAsync();
        Assert.Null(recorded);
    }

    [Fact(Timeout = 30_000)]
    public async Task KnownIdentity_Connects()
    {
        var player = "known-" + Guid.NewGuid().ToString("N")[..8];
        var handler = CreateHandler(
            new MetaTransportOptions { RequireAuthentication = true }, new FakeIdentityValidator(player));

        var response = await handler.SessionConnectAsync(ConnectRequest(player));

        Assert.True(response.Success);
        Assert.Equal(SessionConnectFailureReason.None, response.FailureReason);
    }

    [Fact(Timeout = 30_000)]
    public async Task WithoutRequireAuthentication_GateIsSkipped()
    {
        // PlayerId is client-supplied in this mode — there is no auth store to check it against,
        // so the validator must not be consulted at all.
        var player = "anon-" + Guid.NewGuid().ToString("N")[..8];
        var validator = new FakeIdentityValidator();
        var handler = CreateHandler(new MetaTransportOptions(), validator);

        var response = await handler.SessionConnectAsync(ConnectRequest(player));

        Assert.True(response.Success);
        Assert.Equal(0, validator.Calls);
    }

    [Fact(Timeout = 30_000)]
    public async Task ValidatePlayerIdentityFalse_GateIsSkipped()
    {
        var player = "optout-" + Guid.NewGuid().ToString("N")[..8];
        var validator = new FakeIdentityValidator();
        var handler = CreateHandler(
            new MetaTransportOptions { RequireAuthentication = true, ValidatePlayerIdentity = false },
            validator);

        var response = await handler.SessionConnectAsync(ConnectRequest(player));

        Assert.True(response.Success);
        Assert.Equal(0, validator.Calls);
    }

    [Fact(Timeout = 30_000)]
    public async Task NoValidatorRegistered_GateIsSkipped()
    {
        var player = "novalidator-" + Guid.NewGuid().ToString("N")[..8];
        var handler = CreateHandler(
            new MetaTransportOptions { RequireAuthentication = true }, validator: null);

        var response = await handler.SessionConnectAsync(ConnectRequest(player));

        Assert.True(response.Success);
    }

    [Fact(Timeout = 60_000)]
    public async Task RejectedConnect_SurfacesAsAuthFailureToTheClient()
    {
        var ghost = "ghost-e2e-" + Guid.NewGuid().ToString("N")[..8];
        var server = new InProcessServer(_fixture.CreateHandlerFactory(
            new MetaTransportOptions { RequireAuthentication = true }, new FakeIdentityValidator()));

        await using var client = new TestClientSetup(server, ghost);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
        Assert.Contains("Authentication", ex.Message);
    }

    // ========================
    // AuthIndex-backed validator
    // ========================

    [Fact(Timeout = 30_000)]
    public async Task AuthIndexValidator_KnowsAPlayerCreatedByLogin()
    {
        var deviceId = "identity-device-" + Guid.NewGuid();
        var login = await _fixture.GrainFactory.GetGrain<IAuthGrain>(deviceId).LoginAsync();
        var validator = new AuthIndexPlayerIdentityValidator(_fixture.GrainFactory);

        Assert.True(await validator.ExistsAsync(login.PlayerId));
    }

    [Fact(Timeout = 30_000)]
    public async Task AuthIndexValidator_RejectsPlayerWithNoAuthKeys()
    {
        var validator = new AuthIndexPlayerIdentityValidator(_fixture.GrainFactory);

        Assert.False(await validator.ExistsAsync("never-logged-in-" + Guid.NewGuid()));
        Assert.False(await validator.ExistsAsync(""));
    }

    [Fact(Timeout = 30_000)]
    public async Task AuthIndexValidator_RejectsPlayerAfterItsLastKeyIsUnlinked()
    {
        // Mirrors the production failure: the account is gone (here via reset-device) while a
        // signed token for it is still inside its lifetime.
        var deviceId = "identity-reset-" + Guid.NewGuid();
        var authGrain = _fixture.GrainFactory.GetGrain<IAuthGrain>(deviceId);
        var login = await authGrain.LoginAsync();
        var validator = new AuthIndexPlayerIdentityValidator(_fixture.GrainFactory);
        Assert.True(await validator.ExistsAsync(login.PlayerId));

        await authGrain.ForceUnlinkAsync();

        Assert.False(await validator.ExistsAsync(login.PlayerId));
    }
}
