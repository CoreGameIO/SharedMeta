using SharedMeta.Core;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using SharedMeta.Test.Meta1.Server;
using SharedMeta.Test.Server;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.21.0 — integration tests for the three <see cref="EntityScope"/> behaviours:
/// <list type="bullet">
///   <item>Private: cross-entity call from a higher-version caller doesn't migrate the
///         target's schema and doesn't change the dispatched config — the pin wins.</item>
///   <item>Shared: first subscriber's pin; subsequent joiners with a different patch on
///         the same Major.Minor are downgraded to the pinned patch; Major.Minor mismatch
///         rejects the subscribe.</item>
///   <item>Global: subscribe permitted only when the joiner's resolved config covers the
///         schema the server is operating under (driven by
///         <see cref="IConfigVersionResolver.CurrentClientVersion"/>).</item>
/// </list>
/// </summary>
[Collection(TestClusterCollection.Name)]
public class EntityScopeTests
{
    private readonly TestClusterFixture _fixture;

    public EntityScopeTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    private InProcessServer CreateServer() => new InProcessServer(_fixture.CreateHandlerFactory());

    private static string UniqueId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    // ═════════════════════════════════════════════════════════════════════════════
    //  1) Private: cross-entity call from a higher-version caller doesn't move the
    //     target's schema and dispatched config stays pinned at owner's version.
    //
    //  Setup: a MigrationTestState entity (Private, default scope) owned by player P.
    //  P subscribes with clientAppVersion "1.0.0" → pin = config 1.0 → state migrates
    //  to schema 1 (Value=1). Then we synthesize a cross-entity-style call into P's
    //  entity with CallerClientVersion="3.0.0" — same shape the framework would emit
    //  on a real cross-entity hop from a 3.x player's grain. Expectation:
    //    • State.Value stays at 1 (pin locks migration on Private regardless of caller).
    //    • Query result reflects pin's config branch.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(Timeout = 60_000)]
    public async Task Private_CrossEntityCallFromHigherVersionClient_DoesNotMigrateTarget()
    {
        var ownerId = UniqueId("scope-priv-");

        // Owner SUBSCRIBES at "1.0.0" (not just Query — Subscribe is what triggers
        // EstablishConfigPinsFromClientVersion in EntityGrain.SubscribeAsync). Pin = 1.0.
        // State migrates to schema 1 (Value=1).
        var server = CreateServer();
        await using var ownerClient = new TestClientSetup(server, ownerId, clientAppVersion: "1.0.0");
        await ownerClient.ConnectAsync();
        var ownerApi = await ownerClient.CreateResolver().GetServiceAsync<MigrationTestServiceApiClient>(ownerId);
        var ownerQapi = new MigrationTestServiceQueryApi(ownerClient.Connection, ownerClient.Serializer).EntityApi(ownerId);
        Assert.Equal(1, await ownerQapi.GetValueAsync());

        // Synthesize a cross-entity-style call into owner's entity with CallerClientVersion="3.0.0"
        // — mirrors what EntityGrain.EntityCallHandler emits when a 3.x player's grain reaches
        // into a 1.x player's profile. The pin established by owner's subscribe must lock both
        // config dispatch AND migration; the target's schema must not advance.
        var resolver = new GeneratedEntityGrainResolver();
        var entityGrain = resolver.GetEntityGrain(_fixture.GrainFactory, typeof(MigrationTestState).FullName!, ownerId);
        Assert.NotNull(entityGrain);

        var queryCall = new RpcCall
        {
            ServiceName = "IMigrationTestService",
            MethodName = "GetValue",
            Payload = Array.Empty<byte>(),
            CallerId = "cross-3.0",
            CallerClientVersion = "3.0.0",
        };
        var queryResp = await entityGrain!.HandleQueryAsync(queryCall);
        Assert.True(queryResp.Success, queryResp.Error);

        // Pin still holds; no migration ran on the 3.0 call. State.Value stays at 1.
        Assert.Equal(1, await ownerQapi.GetValueAsync());
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  2) Shared: first subscriber pins a patch; subsequent joiner at a different
    //     patch (same Major.Minor) joins successfully and runs under the pinned patch.
    //
    //  Setup: SharedScopeState entity. Client A subscribes with "1.0.0" → pin = 1.0.0.
    //  Client B subscribes with "1.0.5" (rule maps Client="1.0.5" → Config="1.0.5") to
    //  the SAME entity. Joiner's resolved patch (1.0.5) differs from pin (1.0.0) but
    //  Major.Minor matches → joiner downgrades to pin's patch.
    //
    //  Verification: after both subscribed, B issues a Server-mode RecordConfig call
    //  on the entity. The recorded Config tuple must reflect the PINNED patch (1.0.0),
    //  not B's own resolved patch (1.0.5).
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(Timeout = 60_000)]
    public async Task Shared_NewSubscriberAtDifferentPatch_RunsUnderInitialPin()
    {
        var entityId = UniqueId("scope-shared-patch-");
        var aId = UniqueId("a-");
        var bId = UniqueId("b-");
        var server = CreateServer();

        // Client A: first subscriber pins config @ 1.0.0.
        await using var clientA = new TestClientSetup(server, aId, clientAppVersion: "1.0.0");
        await clientA.ConnectAsync();
        var aApi = await clientA.CreateResolver().GetServiceAsync<SharedScopeServiceApiClient>(entityId);

        // Client B: joins at a different patch on the same Major.Minor. Server-side validation
        // must accept (patch differences tolerated, joiner downgrades to pin's patch).
        await using var clientB = new TestClientSetup(server, bId, clientAppVersion: "1.0.5");
        await clientB.ConnectAsync();
        var bApi = await clientB.CreateResolver().GetServiceAsync<SharedScopeServiceApiClient>(entityId);

        // B invokes RecordConfig under their session — server resolves config via pin → 1.0.0.
        await bApi.RecordConfigAsync();

        // Query the recorded config from either client (pin makes both see the same).
        var qapi = new SharedScopeServiceQueryApi(clientB.Connection, clientB.Serializer).EntityApi(entityId);
        var last = await qapi.GetLastConfigAsync();
        Assert.Equal(1, last.Major);
        Assert.Equal(0, last.Minor);
        Assert.Equal(0, last.Patch);   // pin's patch, NOT B's 1.0.5
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  3) Shared: subscriber on an incompatible Major.Minor is rejected.
    //
    //  Setup: SharedScopeState entity. Client A subscribes with "1.0.0" → pin = 1.0.
    //  Client C subscribes with "2.0.0" (resolves to 2.0.0). Pin Major.Minor=1.0,
    //  joiner Major.Minor=2.0 → mismatch → ValidateClientCompatibleWithPins returns
    //  false → EntityGrain throws EntityAccessDeniedException → client sees it as
    //  a subscribe failure ("Cannot join this shared session...").
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(Timeout = 60_000)]
    public async Task Shared_IncompatibleMajorMinorJoiner_RejectsSubscribe()
    {
        var entityId = UniqueId("scope-shared-mismatch-");
        var aId = UniqueId("a-");
        var cId = UniqueId("c-");
        var server = CreateServer();

        await using var clientA = new TestClientSetup(server, aId, clientAppVersion: "1.0.0");
        await clientA.ConnectAsync();
        // A's subscribe pins the entity at config 1.0.
        await clientA.CreateResolver().GetServiceAsync<SharedScopeServiceApiClient>(entityId);

        await using var clientC = new TestClientSetup(server, cId, clientAppVersion: "2.0.0");
        await clientC.ConnectAsync();

        // C's subscribe must fail — Major.Minor mismatch against the pin.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await clientC.CreateResolver().GetServiceAsync<SharedScopeServiceApiClient>(entityId));
        // The wrapped reason carries "Cannot join this shared session" from EntityGrain — assert
        // we get a descriptive message rather than a silent NRE / generic transport error.
        Assert.Contains("shared session", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  4) Global: subscriber whose resolved config covers the current schema floor
    //     subscribes successfully; calls dispatch under CurrentClientVersion.
    //
    //  Setup: GlobalScopeState declares [MetaStateVersion(2, "2.0")] — schema 2 requires
    //  GlobalScopeConfig >= 2.0. Set the test resolver's CurrentClientVersion to "2.0.0";
    //  on first subscribe the server migrates state to schema 2 (state.Value=2) and pins
    //  Context.Config to 2.0 for dispatch. Client subscribes with "2.0.0" → compat gate
    //  passes (joiner's resolved 2.0 ≥ schema-2 floor's 2.0). [MetaInit] recorded config
    //  Major=2 during the schema 2 step.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(Timeout = 60_000)]
    public async Task Global_SupportedClient_SubscribesAndRunsUnderCurrentClientVersion()
    {
        var entityId = UniqueId("scope-global-ok-");
        var playerId = UniqueId("p-");
        var server = CreateServer();

        var prevResolverVersion = TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion;
        TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = "2.0.0";
        try
        {
            await using var client = new TestClientSetup(server, playerId, clientAppVersion: "2.0.0");
            await client.ConnectAsync();
            await client.CreateResolver().GetServiceAsync<GlobalScopeServiceApiClient>(entityId);

            // State migrated to schema 2 under CurrentClientVersion=2.0.0. Value=2; init recorded
            // config (2, 0) on the schema-2 step.
            var qapi = new GlobalScopeServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);
            Assert.Equal(2, await qapi.GetValueAsync());
            var initCfg = await qapi.GetInitConfigAsync();
            Assert.Equal(2, initCfg.Major);
            Assert.Equal(0, initCfg.Minor);
        }
        finally { TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = prevResolverVersion; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  5) Global: subscriber whose resolved config can't cover the current schema floor
    //     is rejected at the compat gate with a "your app is too old" exception.
    //
    //  Setup: server resolver at "2.0.0" → state migrated to schema 2 (requires config 2.0).
    //  Client at "1.0.0" subscribes — joiner's resolved config = 1.0 < schema-2 floor 2.0
    //  → IsClientConfigCompatible returns false → EntityAccessDeniedException with
    //  "app version is too old / please update" message.
    // ═════════════════════════════════════════════════════════════════════════════
    [Fact(Timeout = 60_000)]
    public async Task Global_UnsupportedClient_RejectsWithUpdateMessage()
    {
        var entityId = UniqueId("scope-global-reject-");
        var primingId = UniqueId("prime-");
        var legacyId = UniqueId("legacy-");
        var server = CreateServer();

        var prevResolverVersion = TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion;
        TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = "2.0.0";
        try
        {
            // Prime the entity: first subscribe (with a supported 2.0 client) migrates state to schema 2.
            await using var primer = new TestClientSetup(server, primingId, clientAppVersion: "2.0.0");
            await primer.ConnectAsync();
            await primer.CreateResolver().GetServiceAsync<GlobalScopeServiceApiClient>(entityId);

            // Legacy client at 1.0.0 attempts to subscribe — server's compat gate rejects.
            await using var legacy = new TestClientSetup(server, legacyId, clientAppVersion: "1.0.0");
            await legacy.ConnectAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await legacy.CreateResolver().GetServiceAsync<GlobalScopeServiceApiClient>(entityId));
            // The server's reject message goes through SessionManagerGrain → SubscribeResponse.Error
            // → ClientDispatcher wraps as "Failed to subscribe to entity '{id}': {error}". The inner
            // error text from EntityGrain mentions "app version is too old / Please update your app".
            // Test verifies the user-actionable substring is preserved through the propagation chain.
            Assert.Contains("update", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TestServerConfiguration.ConfigVersionResolver.CurrentClientVersion = prevResolverVersion; }
    }
}
