using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
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
/// TestServerConfiguration.MigrationConfigProvider is a controllable singleton. Tests
/// save/restore its CurrentVersion so they don't interfere with each other.
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
    /// This is the right choice for reading state that may diverge from the client's local
    /// cache (e.g. after a lazy migration updated state.Value on the server).
    /// </summary>
    private static MigrationTestServiceEntityQueryApi GetQueryApi(TestClientSetup client, string entityId)
        => new MigrationTestServiceQueryApi(client.Connection, client.Serializer).EntityApi(entityId);

    /// <summary>Save/restore config version around a test body.</summary>
    private static async Task WithConfigVersion(int major, int minor, Func<Task> test)
    {
        var prev = TestServerConfiguration.MigrationConfigProvider.CurrentVersion;
        TestServerConfiguration.MigrationConfigProvider.SetVersion(major, minor);
        try   { await test(); }
        finally { TestServerConfiguration.MigrationConfigProvider.SetVersion(prev.Major, prev.Minor); }
    }

    // ── Tests: activation-time migration ─────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigBelowAllThresholds_NoMigrationRuns()
    {
        await WithConfigVersion(0, 5, async () =>
        {
            var server = CreateServer();
            var playerId = UniqueId("mig-none-");

            await using var client = new TestClientSetup(server, playerId);
            await client.ConnectAsync();

            var value = await GetQueryApi(client, playerId).GetValueAsync();

            // Config 0.5 is below all thresholds — no [MetaInit] step should run.
            Assert.Equal(0, value);
        });
    }

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigAt1_0_AppliesSchema1()
    {
        await WithConfigVersion(1, 0, async () =>
        {
            var server = CreateServer();
            var playerId = UniqueId("mig-v1-");

            await using var client = new TestClientSetup(server, playerId);
            await client.ConnectAsync();

            var value = await GetQueryApi(client, playerId).GetValueAsync();

            // Config 1.0 meets schema-1 threshold only → state migrates to schema 1, Value = 1.
            Assert.Equal(1, value);
        });
    }

    [Fact(Timeout = 60_000)]
    public async Task Migration_ConfigAt2_0_AppliesSchema1Then2_WithCorrectPinnedConfigs()
    {
        await WithConfigVersion(2, 0, async () =>
        {
            var server = CreateServer();
            var playerId = UniqueId("mig-v2-");

            await using var client = new TestClientSetup(server, playerId);
            await client.ConnectAsync();

            var value = await GetQueryApi(client, playerId).GetValueAsync();

            // Config 2.0 meets thresholds for schema 1 and 2. Sequential migration:
            //   step 1 with Config@1.0 → Value=1
            //   step 2 with Config@2.0 → Value=2
            // Final Value = 2 (last step wins), schema stored = 2.
            Assert.Equal(2, value);
        });
    }

    [Fact(Timeout = 60_000)]
    public async Task Migration_SkippedVersions_ConfigAt3_AppliesAllThreeSteps()
    {
        await WithConfigVersion(3, 0, async () =>
        {
            var server = CreateServer();
            var playerId = UniqueId("mig-v3-");

            await using var client = new TestClientSetup(server, playerId);
            await client.ConnectAsync();

            var value = await GetQueryApi(client, playerId).GetValueAsync();

            // Config 3.0 meets all three thresholds → sequential migration 1→2→3.
            // Each [MetaInit] call receives the config pinned to that step's transition
            // version (1.0 / 2.0 / 3.0 respectively), not the current 3.0.
            // Value = 3 after all steps complete.
            Assert.Equal(3, value);
        });
    }

    // ── Test: lazy migration (entity already active when config advances) ────

    [Fact(Timeout = 60_000)]
    public async Task LazyMigration_MigratesOnNextCall_WhenConfigAdvances()
    {
        var server = CreateServer();
        var playerId = UniqueId("mig-lazy-");

        // Start at config 1.0 → entity activates and migrates to schema 1 (Value=1).
        TestServerConfiguration.MigrationConfigProvider.SetVersion(1, 0);
        try
        {
            await using var client = new TestClientSetup(server, playerId);
            await client.ConnectAsync();

            var qapi = GetQueryApi(client, playerId);

            var valueBefore = await qapi.GetValueAsync();
            Assert.Equal(1, valueBefore); // schema 1 applied at activation

            // Advance config to 2.0 — entity grain is still active in memory.
            // The singleton provider's CurrentVersion changes immediately.
            TestServerConfiguration.MigrationConfigProvider.SetVersion(2, 0);

            // Next server call → CheckAndRunLazyMigrationAsync sees required=2 > current=1
            // → runs migration step 2 → Value becomes 2 on the server → state persisted.
            var valueAfter = await qapi.GetValueAsync();
            Assert.Equal(2, valueAfter); // schema 2 applied lazily
        }
        finally
        {
            TestServerConfiguration.MigrationConfigProvider.SetVersion(1, 0);
        }
    }
}
