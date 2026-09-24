using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// <c>[RequirePermission]</c> — account-level entitlements gating a method. The enforcement point is
/// the generated provider override, which runs before arguments are deserialized and before the
/// service body, so a refused call cannot have touched state.
///
/// The caller's set is resolved once when the session connects and stamped by the transport handler
/// onto every call, so a gated method costs a string comparison rather than a read of the store. A
/// change while the player is connected arrives as a push that refreshes both the connection's copy
/// and the client's — <c>RevokeWhileConnected_AppliesToTheLiveSession</c> pins that down, and
/// <c>StoreWriteThatSkipsThePush_AppliesOnNextConnect</c> records what it costs when the push is
/// bypassed.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class RequirePermissionTests
{
    private readonly TestClusterFixture _fixture;

    public RequirePermissionTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A client that knows its set refuses locally, before anything is sent. Same verdict the
    /// server would give, one round trip cheaper.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task UngrantedCaller_IsRefused_AndStateUntouched()
    {
        var (entityId, api, resolver, setup) = await ClientAsync("perm_deny");
        await using var _ = setup;

        await api.AddValueAsync(7, 1);   // ungated, establishes a non-default state

        var ex = await Assert.ThrowsAsync<MetaPermissionDeniedException>(() => api.CheatSetSumAsync(9999));
        Assert.Equal("CheatSetSum", ex.Method);
        Assert.Equal(new[] { "Cheat" }, ex.Required);

        // The gate runs ahead of the body, so the cheat cannot have landed a partial mutation.
        Assert.Equal(7, resolver.GetState<CounterState>(entityId).Sum);
    }

    /// <summary>The client learns its set as part of connecting — no extra call to ask for it.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ConnectReportsPermissions()
    {
        var playerId = "perm_connect_" + Guid.NewGuid().ToString("N");
        await GrantAsync(playerId, "Cheat");

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var setup = new TestClientSetup(server, playerId);
        await using var _ = setup;
        await setup.ConnectAsync();

        var held = setup.MetaClient.Dispatcher.Permissions;
        Assert.NotNull(held);
        Assert.True(held!.Has("Cheat"));
        Assert.False(held.Has("Admin"));
    }

    [Fact(Timeout = 60_000)]
    public async Task GrantedCaller_IsAdmitted()
    {
        var (entityId, api, resolver, setup) = await ClientAsync("perm_grant", "Cheat");
        await using var _ = setup;

        await api.CheatSetSumAsync(4242);

        Assert.Equal(4242, resolver.GetState<CounterState>(entityId).Sum);
    }

    /// <summary>Any one of the declared names admits the call — they are alternatives, not a set to satisfy.</summary>
    [Fact(Timeout = 60_000)]
    public async Task SecondOfTwoPermissions_Admits()
    {
        // AdminBump accepts Admin or Support.
        var (entityId, api, resolver, setup) = await ClientAsync("perm_anyof", "Support");
        await using var _ = setup;

        await api.AdminBumpAsync(5);

        Assert.Equal(5, resolver.GetState<CounterState>(entityId).Sum);
    }

    /// <summary>A grant for one method does not open another with a different requirement.</summary>
    [Fact(Timeout = 60_000)]
    public async Task PermissionsDoNotLeakBetweenMethods()
    {
        var (_, api, _, setup) = await ClientAsync("perm_leak", "Cheat");
        await using var _d = setup;

        await api.CheatSetSumAsync(1);
        await Assert.ThrowsAnyAsync<Exception>(() => api.AdminBumpAsync(1));
    }

    /// <summary>
    /// The server's own gate, reached past the client's: a forged packet built straight on the
    /// transport skips the generated method and its local check. The handler stamps the caller's
    /// permissions from what it resolved at connect, overwriting anything the packet claimed, so
    /// there is nothing for a modified client to set.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ForgedCall_IsRefusedByServer()
    {
        var (entityId, api, resolver, setup) = await ClientAsync("perm_forged");
        await using var _d = setup;

        await api.AddValueAsync(5, 1);   // consumes requestId 1 and gives the state a value

        var forged = new SharedMeta.Core.Transport.RpcCallRequest
        {
            EntityId = entityId,
            StateTypeName = typeof(CounterState).FullName!,
            RequestId = 2,
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_CheatSetSum_v0,
            Payload = setup.MetaClient.Serializer.Pack(9999),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        };

        var response = await setup.MetaClient.Connection.RpcCallAsync(forged);

        var op = response.Operations.FirstOrDefault(o => o.RequestId == 2);
        var error = response.Error ?? op.ErrorMessage;
        Assert.NotNull(error);
        Assert.Contains("permission", error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, resolver.GetState<CounterState>(entityId).Sum);
    }

    /// <summary>
    /// A revocation through the entitlements service applies to the session already running: the
    /// push refreshes the connection's stamped set, so the next call is judged by the new rights
    /// without a reconnect.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RevokeWhileConnected_AppliesToTheLiveSession()
    {
        var (entityId, api, resolver, setup) = await ClientAsync("perm_revoke", "Cheat");
        await using var _d = setup;

        await api.CheatSetSumAsync(1);

        var pushed = new TaskCompletionSource<PlayerPermissions>();
        setup.MetaClient.Dispatcher.PermissionsChanged += p => pushed.TrySetResult(p);

        await Entitlements().RevokeAsync(setup.PlayerId, new[] { "Cheat" });
        var set = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(set.Has("Cheat"));

        await Assert.ThrowsAsync<MetaPermissionDeniedException>(() => api.CheatSetSumAsync(2));
        Assert.Equal(1, resolver.GetState<CounterState>(entityId).Sum);
    }

    /// <summary>
    /// The cost of stamping the set on the call instead of reading it per gate, stated as a test: a
    /// write that bypasses the push — straight into the grain, or from a silo whose notification was
    /// lost — leaves the running session on its previous rights. The next connect reads the store
    /// and picks the change up.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StoreWriteThatSkipsThePush_AppliesOnNextConnect()
    {
        var playerId = "perm_stale_" + Guid.NewGuid().ToString("N");
        await GrantAsync(playerId, "Cheat");

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var entityId = $"perm_stale_{Guid.NewGuid():N}";

        var first = new TestClientSetup(server, playerId);
        await first.ConnectAsync();
        var api = await first.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await api.CheatSetSumAsync(1);

        // Straight to the grain: no push, so neither the connection nor the client hears about it.
        await _fixture.GrainFactory.GetGrain<IPlayerEntitlementsGrain>(playerId)
            .RevokeAsync(new List<string> { "Cheat" });

        await api.CheatSetSumAsync(2);   // still admitted — this session was stamped with "Cheat"
        await first.DisposeAsync();

        var second = new TestClientSetup(server, playerId);
        await using var _s = second;
        await second.ConnectAsync();
        var api2 = await second.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        Assert.False(second.MetaClient.Dispatcher.Permissions!.Has("Cheat"));
        await Assert.ThrowsAsync<MetaPermissionDeniedException>(() => api2.CheatSetSumAsync(3));
    }

    /// <summary>
    /// A grant through the entitlements service reaches the connected client, so its gate starts
    /// admitting calls it refused a moment earlier without a reconnect.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task GrantWhileConnected_IsPushedToTheClient()
    {
        var (entityId, api, resolver, setup) = await ClientAsync("perm_push");
        await using var _d = setup;

        await Assert.ThrowsAsync<MetaPermissionDeniedException>(() => api.CheatSetSumAsync(1));

        var pushed = new TaskCompletionSource<PlayerPermissions>();
        setup.MetaClient.Dispatcher.PermissionsChanged += p => pushed.TrySetResult(p);

        await Entitlements().GrantAsync(setup.PlayerId, new[] { "Cheat" });

        var set = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(set.Has("Cheat"));

        await api.CheatSetSumAsync(77);
        Assert.Equal(77, resolver.GetState<CounterState>(entityId).Sum);
    }

    /// <summary>An ungated method stays reachable for a caller holding nothing at all.</summary>
    [Fact(Timeout = 60_000)]
    public async Task UngatedMethods_AreUnaffected()
    {
        var (entityId, api, resolver, setup) = await ClientAsync("perm_ungated");
        await using var _ = setup;

        await api.AddValueAsync(3, 1);
        await api.AddValueAsync(4, 2);

        Assert.Equal(7, resolver.GetState<CounterState>(entityId).Sum);
    }

    /// <summary>Granting the same permission twice must not keep bumping the generation.</summary>
    [Fact(Timeout = 60_000)]
    public async Task RepeatedGrant_DoesNotBumpGeneration()
    {
        var playerId = "perm_gen_" + Guid.NewGuid().ToString("N");
        var grain = _fixture.GrainFactory.GetGrain<IPlayerEntitlementsGrain>(playerId);

        var first = await grain.GrantAsync(new List<string> { "Cheat" });
        var second = await grain.GrantAsync(new List<string> { "Cheat" });

        Assert.Equal(first.Generation, second.Generation);
        Assert.True(second.Has("Cheat"));

        var revoked = await grain.RevokeAsync(new List<string> { "Cheat" });
        Assert.True(revoked.Generation > second.Generation);
        Assert.False(revoked.Has("Cheat"));
    }

    /// <summary>The same entitlements service the silo uses — writes through it also push.</summary>
    private SharedMeta.Server.Permissions.IPlayerEntitlements Entitlements()
        => new SharedMeta.Server.Core.Permissions.GrainPlayerEntitlements(_fixture.GrainFactory);

    private Task GrantAsync(string playerId, params string[] permissions)
        => _fixture.GrainFactory.GetGrain<IPlayerEntitlementsGrain>(playerId)
            .GrantAsync(new List<string>(permissions));

    /// <param name="granted">
    /// Permissions granted BEFORE connecting. Order matters: the client receives its set as part of
    /// connecting, so a grain-level grant afterwards would leave this client refusing locally with a
    /// set it has no reason to doubt.
    /// </param>
    private async Task<(string entityId, CounterServiceApiClient api,
        SharedMeta.Client.MetaServiceResolver resolver, TestClientSetup setup)> ClientAsync(
        string prefix, params string[] granted)
    {
        var playerId = prefix + "_" + Guid.NewGuid().ToString("N");
        if (granted.Length > 0) await GrantAsync(playerId, granted);

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var setup = new TestClientSetup(server, playerId);
        await setup.ConnectAsync();

        var entityId = $"{prefix}_{Guid.NewGuid():N}";
        var resolver = setup.CreateResolver();
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        return (entityId, api, resolver, setup);
    }
}
