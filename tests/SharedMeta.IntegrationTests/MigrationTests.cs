using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using SharedMeta.Test.Server;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for [MetaStateVersion] config-driven state schema migration.
///
/// MigrationTestState declares three breakpoints:
///   schema 1: MigrationConfig >= 1.0
///   schema 2: MigrationConfig >= 2.0
///   schema 3: MigrationConfig >= 3.0
///
/// MigrationTestService.[MetaInit] sets state.Value = the schema version just applied
/// (1, 2, or 3) so tests can assert the final schema by reading GetValue().
///
/// <para>
/// <b>0.21.0:</b> migration is driven per-client by <c>MetaClientOptions.ClientAppVersion</c>.
/// Each test connects with a specific client app version that resolves to a specific
/// <c>MigrationConfig</c> branch (via <c>[MetaConfigVersion]</c> rules on the config class):
///   <c>"0.5.0"</c> → 0.5 → below all thresholds (no migration),
///   <c>"1.0.0"</c> → 1.0 → schema 1,
///   <c>"2.0.0"</c> → 2.0 → schema 1 then 2,
///   <c>"3.0.0"</c> → 3.0 → schema 1 then 2 then 3.
/// </para>
/// </summary>
[Collection(TestClusterCollection.Name)]
public class MigrationTests
{
    private readonly TestClusterFixture _fixture;

    public MigrationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private InProcessServer CreateServer() =>
        new InProcessServer(_fixture.CreateHandlerFactory());

    private static string UniqueId(string prefix) =>
        prefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Returns the Query API for MigrationTestService. Query methods execute on the server
    /// and return the result directly — no client-side replay, no desync comparison.
    /// </summary>
    private static MigrationTestServiceEntityQueryApi GetQueryApi(TestClientSetup client, string entityId)
        => new MigrationTestServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

    // ── Tests: activation-time migration ─────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigBelowAllThresholds_NoMigrationRuns()
    {
        var server = CreateServer();
        var playerId = UniqueId("mig-none-");

        // clientAppVersion "0.5.0" resolves (via [MetaConfigVersion(Client="0.x.*", Config="0.x.*")])
        // to config 0.5 — below schema-1's 1.0 threshold. No [MetaInit] step should run.
        await using var client = new TestClientSetup(server, playerId, clientAppVersion: "0.5.0");
        await client.ConnectAsync();

        var value = await GetQueryApi(client, playerId).GetValueAsync();
        Assert.Equal(0, value);
    }

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigAt1_0_AppliesSchema1()
    {
        var server = CreateServer();
        var playerId = UniqueId("mig-v1-");

        await using var client = new TestClientSetup(server, playerId, clientAppVersion: "1.0.0");
        await client.ConnectAsync();

        var value = await GetQueryApi(client, playerId).GetValueAsync();

        // Config 1.0 meets schema-1 threshold only → state migrates to schema 1, Value = 1.
        Assert.Equal(1, value);
    }

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigAt2_0_AppliesSchema1Then2_WithCorrectPinnedConfigs()
    {
        var server = CreateServer();
        var playerId = UniqueId("mig-v2-");

        await using var client = new TestClientSetup(server, playerId, clientAppVersion: "2.0.0");
        await client.ConnectAsync();

        var value = await GetQueryApi(client, playerId).GetValueAsync();

        // Config 2.0 meets thresholds for schema 1 and 2. Sequential migration:
        //   step 1 with Config@1.0 → Value=1
        //   step 2 with Config@2.0 → Value=2
        // Final Value = 2 (last step wins), schema stored = 2.
        Assert.Equal(2, value);
    }

    [Fact(Timeout = 60_000)]
    public async Task Migration_SkippedVersions_ConfigAt3_AppliesAllThreeSteps()
    {
        var server = CreateServer();
        var playerId = UniqueId("mig-v3-");

        await using var client = new TestClientSetup(server, playerId, clientAppVersion: "3.0.0");
        await client.ConnectAsync();

        var value = await GetQueryApi(client, playerId).GetValueAsync();

        // Config 3.0 meets all three thresholds → sequential migration 1→2→3.
        // Each [MetaInit] call receives the config pinned to that step's transition
        // version (1.0 / 2.0 / 3.0 respectively), not the current 3.0.
        // Value = 3 after all steps complete.
        Assert.Equal(3, value);
    }

    // ── Test: lazy migration when a newer client reconnects to a Private entity ──
    //
    // 0.21.0 model: Private entities pin config on first subscribe; pin survives the
    // grain's active lifetime and is dropped on Orleans idle-deactivation. The pre-0.21.0
    // "advance provider.CurrentVersion mid-session" scenario doesn't apply (no ambient
    // current version anymore). Equivalent test under the new model: the SAME player
    // reconnects with a NEWER client version after grain idle-deactivation — the pin is
    // re-established at the new version and migration runs to the new schema. We can't
    // force grain deactivation from a test, so we use a fresh server (which gives a fresh
    // grain activation per InProcessConnection) for the second connect.

    [Fact(Timeout = 60_000)]
    public async Task ReconnectWithNewerClient_MigratesToNewSchema()
    {
        var playerId = UniqueId("mig-reconnect-");

        // Phase 1: first connect with clientAppVersion 1.0.0 → schema 1.
        {
            var server = CreateServer();
            await using var client = new TestClientSetup(server, playerId, clientAppVersion: "1.0.0");
            await client.ConnectAsync();
            Assert.Equal(1, await GetQueryApi(client, playerId).GetValueAsync());
        }

        // Phase 2: same player reconnects with newer client (2.0.0). Fresh server / grain
        // activation rebuilds the pin from this client → migration step 2 runs → Value=2.
        {
            var server = CreateServer();
            await using var client = new TestClientSetup(server, playerId, clientAppVersion: "2.0.0");
            await client.ConnectAsync();
            Assert.Equal(2, await GetQueryApi(client, playerId).GetValueAsync());
        }
    }

    // ── Test: admin force-migrate (0.21.0 ForceMigrateToFloorAsync) ─────────────
    //
    // Admin scenario: drop support for an old config branch by force-migrating every
    // entity below a floor to that floor's required schema. Iterating entity IDs is
    // project-side (player DB / storage); the per-entity API is on IEntityGrain.

    [Fact(Timeout = 60_000)]
    public async Task ForceMigrateToFloor_AdvancesEntityFromOldClientToNewFloor()
    {
        var playerId = UniqueId("mig-force-");

        // Phase 1: real client connects at 1.0.0 → schema 1.
        {
            var server = CreateServer();
            await using var client = new TestClientSetup(server, playerId, clientAppVersion: "1.0.0");
            await client.ConnectAsync();
            Assert.Equal(1, await GetQueryApi(client, playerId).GetValueAsync());
        }

        // Phase 2: admin invokes ForceMigrateToFloorAsync("3.0.0") with no subscriber. State
        // advances 1 → 2 → 3, persisted. Use the test-cluster's GrainFactory directly.
        {
            var entityGrain = _fixture.GrainFactory.GetGrain<IEntityGrain<MigrationTestState>>(playerId);
            var migrated = await entityGrain.ForceMigrateToFloorAsync("3.0.0");
            Assert.True(migrated, "force-migrate should report state advancement");
        }

        // Phase 3: read state with a fresh client at 3.0.0 to assert post-force state.
        {
            var server = CreateServer();
            await using var client = new TestClientSetup(server, playerId, clientAppVersion: "3.0.0");
            await client.ConnectAsync();
            Assert.Equal(3, await GetQueryApi(client, playerId).GetValueAsync());
        }
    }
}
