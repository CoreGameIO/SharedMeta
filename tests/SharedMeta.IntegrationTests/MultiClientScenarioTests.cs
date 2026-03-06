using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;
using Xunit.Abstractions;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Comprehensive multi-client integration test.
/// 4 clients (Alice, Bob, Charlie, Dave) working with personal and shared state.
/// Validates state convergence at every step.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class MultiClientScenarioTests
{
    private readonly TestClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MultiClientScenarioTests(TestClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact(Timeout = 60_000)]
    public async Task FullScenario_PersonalAndSharedState_AllClientsConverge()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        var sharedEntityId = $"shared_{testId}";

        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        // ═══════════════════════════════════════════════════════════════
        // Phase 1: Setup & Personal State Isolation
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 1: Setup & Personal State Isolation ===");

        await using var alice = new TestClientSetup(server, "alice");
        await using var bob = new TestClientSetup(server, "bob");
        await using var charlie = new TestClientSetup(server, "charlie");

        await alice.ConnectAsync();
        await bob.ConnectAsync();
        await charlie.ConnectAsync();

        var aliceResolver = alice.CreateResolver();
        var bobResolver = bob.CreateResolver();
        var charlieResolver = charlie.CreateResolver();

        // Personal entities
        var alicePersonalId = $"personal_alice_{testId}";
        var bobPersonalId = $"personal_bob_{testId}";
        var charliePersonalId = $"personal_charlie_{testId}";

        // Subscribe to personal entities
        var alicePersonalApi = await aliceResolver.GetServiceAsync<CounterServiceApiClient>(alicePersonalId);
        var bobPersonalApi = await bobResolver.GetServiceAsync<CounterServiceApiClient>(bobPersonalId);
        var charliePersonalApi = await charlieResolver.GetServiceAsync<CounterServiceApiClient>(charliePersonalId);

        // Subscribe all 3 to shared entity
        var aliceSharedApi = await aliceResolver.GetServiceAsync<CounterServiceApiClient>(sharedEntityId);
        var bobSharedApi = await bobResolver.GetServiceAsync<CounterServiceApiClient>(sharedEntityId);
        var charlieSharedApi = await charlieResolver.GetServiceAsync<CounterServiceApiClient>(sharedEntityId);

        // Step 1: Alice adds to personal entity
        await alicePersonalApi.AddValueAsync(100, 1);
        await alicePersonalApi.AddValueAsync(200, 2);

        var alicePersonalState = aliceResolver.GetState<CounterState>(alicePersonalId);
        Assert.Equal(300, alicePersonalState.Sum);
        Assert.Equal(2, alicePersonalState.Operations.Count);
        Assert.All(alicePersonalState.Operations, op => Assert.Equal("alice", op.CallerId));
        // Verify [MetaInit] ran during entity activation
        Assert.Equal(1, alicePersonalState.InitializedVersion);

        // Shared entity should be untouched
        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 0, expectedOpCount: 0, "after Alice personal ops");

        // Step 2: Bob adds to personal entity
        await bobPersonalApi.AddValueAsync(50, 1);
        await bobPersonalApi.AddValueAsync(75, 2);
        await bobPersonalApi.AddValueAsync(25, 3);

        var bobPersonalState = bobResolver.GetState<CounterState>(bobPersonalId);
        Assert.Equal(150, bobPersonalState.Sum);
        Assert.Equal(3, bobPersonalState.Operations.Count);
        Assert.All(bobPersonalState.Operations, op => Assert.Equal("bob", op.CallerId));

        // Step 3: Charlie adds to personal entity
        await charliePersonalApi.AddValueAsync(1000, 1);

        var charliePersonalState = charlieResolver.GetState<CounterState>(charliePersonalId);
        Assert.Equal(1000, charliePersonalState.Sum);
        Assert.Single(charliePersonalState.Operations);

        // Cross-validate: personal states are independent
        Assert.Equal(300, aliceResolver.GetState<CounterState>(alicePersonalId).Sum);
        Assert.Equal(150, bobResolver.GetState<CounterState>(bobPersonalId).Sum);
        Assert.Equal(1000, charlieResolver.GetState<CounterState>(charliePersonalId).Sum);

        _output.WriteLine("Phase 1 passed: personal states isolated");

        // ═══════════════════════════════════════════════════════════════
        // Phase 2: Shared State — Sequential Operations
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 2: Shared State — Sequential Operations ===");

        // Alice adds to shared
        await aliceSharedApi.AddValueAsync(10, 1);
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 10, expectedOpCount: 1, "after Alice shared op 1");

        // Bob adds to shared
        await bobSharedApi.AddValueAsync(20, 1);
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 30, expectedOpCount: 2, "after Bob shared op 1");

        // Charlie adds to shared
        await charlieSharedApi.AddValueAsync(30, 1);
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 60, expectedOpCount: 3, "after Charlie shared op 1");

        // Validate CallerIds are correct
        var sharedState = aliceResolver.GetState<CounterState>(sharedEntityId);
        Assert.Contains(sharedState.Operations, op => op.CallerId == "alice" && op.Value == 10);
        Assert.Contains(sharedState.Operations, op => op.CallerId == "bob" && op.Value == 20);
        Assert.Contains(sharedState.Operations, op => op.CallerId == "charlie" && op.Value == 30);

        _output.WriteLine("Phase 2 passed: sequential shared operations converge");

        // ═══════════════════════════════════════════════════════════════
        // Phase 3: Shared State — Multiple Operations per Client
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 3: Multiple Operations per Client ===");

        // Alice adds 3 more values
        await aliceSharedApi.AddValueAsync(5, 2);
        await aliceSharedApi.AddValueAsync(15, 3);
        await aliceSharedApi.AddValueAsync(25, 4);
        await Task.Delay(100);

        // Running total: 60 + 5 + 15 + 25 = 105
        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 105, expectedOpCount: 6, "after Alice batch");

        // Bob adds 2 more values
        await bobSharedApi.AddValueAsync(40, 2);
        await bobSharedApi.AddValueAsync(55, 3);
        await Task.Delay(100);

        // Running total: 105 + 40 + 55 = 200
        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 200, expectedOpCount: 8, "after Bob batch");

        // Verify alice's operations are all present
        sharedState = aliceResolver.GetState<CounterState>(sharedEntityId);
        var aliceOps = sharedState.Operations.Where(op => op.CallerId == "alice").ToList();
        var bobOps = sharedState.Operations.Where(op => op.CallerId == "bob").ToList();
        var charlieOps = sharedState.Operations.Where(op => op.CallerId == "charlie").ToList();
        Assert.Equal(4, aliceOps.Count);   // 10, 5, 15, 25
        Assert.Equal(3, bobOps.Count);     // 20, 40, 55
        Assert.Single(charlieOps);          // 30

        _output.WriteLine("Phase 3 passed: batch operations converge");

        // ═══════════════════════════════════════════════════════════════
        // Phase 4: Reset & Rebuild
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 4: Reset & Rebuild ===");

        await aliceSharedApi.ResetAsync();
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 0, expectedOpCount: 0, "after reset");

        // Bob adds after reset
        await bobSharedApi.AddValueAsync(7, 1);
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 7, expectedOpCount: 1, "after Bob post-reset op");

        // Charlie adds after reset
        await charlieSharedApi.AddValueAsync(3, 1);
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver }, sharedEntityId,
            expectedSum: 10, expectedOpCount: 2, "after Charlie post-reset op");

        _output.WriteLine("Phase 4 passed: reset and rebuild work");

        // ═══════════════════════════════════════════════════════════════
        // Phase 5: Late Joiner (Dave)
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 5: Late Joiner (Dave) ===");

        await using var dave = new TestClientSetup(server, "dave");
        await dave.ConnectAsync();
        var daveResolver = dave.CreateResolver();

        // Dave subscribes to shared entity — should see current state (Sum=10, 2 ops)
        var daveSharedApi = await daveResolver.GetServiceAsync<CounterServiceApiClient>(sharedEntityId);
        var daveSharedState = daveResolver.GetState<CounterState>(sharedEntityId);

        Assert.Equal(10, daveSharedState.Sum);
        Assert.Equal(2, daveSharedState.Operations.Count);
        _output.WriteLine($"Dave joined: sees Sum={daveSharedState.Sum}, ops={daveSharedState.Operations.Count}");

        // Dave subscribes to his personal entity
        var davePersonalId = $"personal_dave_{testId}";
        var davePersonalApi = await daveResolver.GetServiceAsync<CounterServiceApiClient>(davePersonalId);

        // Dave adds to shared
        await daveSharedApi.AddValueAsync(90, 1);
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver, daveResolver }, sharedEntityId,
            expectedSum: 100, expectedOpCount: 3, "after Dave shared op");

        // Dave adds to personal — only Dave sees it
        await davePersonalApi.AddValueAsync(500, 1);
        var davePersonalState = daveResolver.GetState<CounterState>(davePersonalId);
        Assert.Equal(500, davePersonalState.Sum);
        Assert.Single(davePersonalState.Operations);

        // Alice adds to shared — all 4 should see it
        await aliceSharedApi.AddValueAsync(50, 5);
        await Task.Delay(100);

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver, daveResolver }, sharedEntityId,
            expectedSum: 150, expectedOpCount: 4, "after Alice op with 4 clients");

        _output.WriteLine("Phase 5 passed: late joiner converges with existing clients");

        // ═══════════════════════════════════════════════════════════════
        // Phase 6: Concurrent Burst
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 6: Concurrent Burst ===");

        var preburstSum = aliceResolver.GetState<CounterState>(sharedEntityId).Sum;
        var preburstOps = aliceResolver.GetState<CounterState>(sharedEntityId).Operations.Count;

        const int opsPerClient = 5;
        var tasks = new List<Task>();

        // Alice: 1,2,3,4,5 (sum=15)
        for (int i = 1; i <= opsPerClient; i++)
        {
            var v = i;
            tasks.Add(aliceSharedApi.AddValueAsync(v, 100 + v));
        }

        // Bob: 10,20,30,40,50 (sum=150)
        for (int i = 1; i <= opsPerClient; i++)
        {
            var v = i * 10;
            tasks.Add(bobSharedApi.AddValueAsync(v, 100 + i));
        }

        // Charlie: 100,200,300,400,500 (sum=1500)
        for (int i = 1; i <= opsPerClient; i++)
        {
            var v = i * 100;
            tasks.Add(charlieSharedApi.AddValueAsync(v, 100 + i));
        }

        // Dave: 1000,2000,3000,4000,5000 (sum=15000)
        for (int i = 1; i <= opsPerClient; i++)
        {
            var v = i * 1000;
            tasks.Add(daveSharedApi.AddValueAsync(v, 100 + i));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(1000); // Extra time for concurrent broadcast propagation

        var expectedBurstSum = 15 + 150 + 1500 + 15000; // = 16665
        var expectedTotalSum = preburstSum + expectedBurstSum;
        var expectedTotalOps = preburstOps + 4 * opsPerClient;

        _output.WriteLine($"Pre-burst: Sum={preburstSum}, Ops={preburstOps}");
        _output.WriteLine($"Expected burst sum: {expectedBurstSum}, total: {expectedTotalSum}, ops: {expectedTotalOps}");

        AssertSharedState(new[] { aliceResolver, bobResolver, charlieResolver, daveResolver }, sharedEntityId,
            expectedSum: expectedTotalSum, expectedOpCount: expectedTotalOps, "after concurrent burst");

        _output.WriteLine("Phase 6 passed: concurrent burst converges");

        // ═══════════════════════════════════════════════════════════════
        // Phase 7: Personal States Remain Untouched
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 7: Personal State Independence ===");

        // Personal states should not have been affected by shared entity ops
        Assert.Equal(300, aliceResolver.GetState<CounterState>(alicePersonalId).Sum);
        Assert.Equal(2, aliceResolver.GetState<CounterState>(alicePersonalId).Operations.Count);

        Assert.Equal(150, bobResolver.GetState<CounterState>(bobPersonalId).Sum);
        Assert.Equal(3, bobResolver.GetState<CounterState>(bobPersonalId).Operations.Count);

        Assert.Equal(1000, charlieResolver.GetState<CounterState>(charliePersonalId).Sum);
        Assert.Single(charlieResolver.GetState<CounterState>(charliePersonalId).Operations);

        Assert.Equal(500, daveResolver.GetState<CounterState>(davePersonalId).Sum);
        Assert.Single(daveResolver.GetState<CounterState>(davePersonalId).Operations);

        // Add one more to each personal to verify they still work
        await alicePersonalApi.AddValueAsync(1, 3);
        await bobPersonalApi.AddValueAsync(1, 4);
        await charliePersonalApi.AddValueAsync(1, 2);
        await davePersonalApi.AddValueAsync(1, 2);

        Assert.Equal(301, aliceResolver.GetState<CounterState>(alicePersonalId).Sum);
        Assert.Equal(151, bobResolver.GetState<CounterState>(bobPersonalId).Sum);
        Assert.Equal(1001, charlieResolver.GetState<CounterState>(charliePersonalId).Sum);
        Assert.Equal(501, daveResolver.GetState<CounterState>(davePersonalId).Sum);

        _output.WriteLine("Phase 7 passed: personal states remain independent");

        // ═══════════════════════════════════════════════════════════════
        // Phase 8: Final Convergence Check
        // ═══════════════════════════════════════════════════════════════
        _output.WriteLine("=== Phase 8: Final Convergence Check ===");

        var allResolvers = new[] { aliceResolver, bobResolver, charlieResolver, daveResolver };
        var allClients = new[] { alice, bob, charlie, dave };
        var clientNames = new[] { "alice", "bob", "charlie", "dave" };

        // Shared state must be identical across all clients
        var referenceState = allResolvers[0].GetState<CounterState>(sharedEntityId);
        for (int i = 1; i < allResolvers.Length; i++)
        {
            var clientState = allResolvers[i].GetState<CounterState>(sharedEntityId);
            Assert.Equal(referenceState.Sum, clientState.Sum);
            Assert.Equal(referenceState.Operations.Count, clientState.Operations.Count);

            // Verify operations match by value (order may differ in concurrent scenario)
            var refValues = referenceState.Operations.Select(o => o.Value).OrderBy(v => v).ToList();
            var clientValues = clientState.Operations.Select(o => o.Value).OrderBy(v => v).ToList();
            Assert.Equal(refValues, clientValues);

            _output.WriteLine($"  {clientNames[i]}: Sum={clientState.Sum}, Ops={clientState.Operations.Count} — matches reference");
        }

        // Zero desync issues on all clients
        foreach (var (client, name) in allClients.Zip(clientNames))
        {
            if (client.DetectedIssues.Count > 0)
            {
                _output.WriteLine($"  {name} issues: {string.Join("; ", client.DetectedIssues)}");
            }
            Assert.Empty(client.DetectedIssues);
        }

        _output.WriteLine($"Final shared state: Sum={referenceState.Sum}, Operations={referenceState.Operations.Count}");
        _output.WriteLine("Phase 8 passed: all clients converge, zero desyncs");
        _output.WriteLine("=== ALL PHASES PASSED ===");
    }

    /// <summary>
    /// Assert that all resolvers see the same shared state with expected Sum and operation count.
    /// </summary>
    private void AssertSharedState(
        MetaServiceResolver[] resolvers,
        string entityId,
        long expectedSum,
        int expectedOpCount,
        string context)
    {
        for (int i = 0; i < resolvers.Length; i++)
        {
            var state = resolvers[i].GetState<CounterState>(entityId);

            if (state.Sum != expectedSum || state.Operations.Count != expectedOpCount)
            {
                _output.WriteLine(
                    $"[MISMATCH] resolver[{i}] {context}: Sum={state.Sum} (expected {expectedSum}), " +
                    $"Ops={state.Operations.Count} (expected {expectedOpCount})");
                _output.WriteLine(
                    $"  Operations: {string.Join(", ", state.Operations.Select(o => $"{o.CallerId}:{o.Value}"))}");
            }

            Assert.Equal(expectedSum, state.Sum);
            Assert.Equal(expectedOpCount, state.Operations.Count);
        }
    }
}
