using System.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;
using Xunit.Abstractions;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Allocation-focused load benchmarks. Run with:
///   dotnet test --filter "FullyQualifiedName~AllocationBenchmarks" --logger:"console;verbosity=detailed"
///
/// Uses in-process transport so the numbers reflect only server-side + client-dispatcher work
/// (no socket / serializer-on-wire noise). Reported figures are the steady-state allocation
/// rate; warmup iterations exclude initialization costs (subscribe, signature handshake).
///
/// What each test exercises:
///   * Rpc_Hot_Path           — N RPCs against one subscribed entity, single client.
///                              Measures the per-RPC server-side allocation rate.
///   * Broadcast_Fanout       — N RPCs with K co-subscribers (broadcast fan-out per RPC).
///                              Measures the per-broadcast-subscriber allocation rate.
///   * Resume_Reclaim         — repeated session Resume cycles with a fixed claim set.
///                              Measures the cost of the new subscription-reclaim flow.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class AllocationBenchmarks
{
    private readonly TestClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AllocationBenchmarks(TestClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact(Skip = "Benchmark — run explicitly via --filter \"FullyQualifiedName~AllocationBenchmarks\"", Timeout = 120_000)]
    public async Task Rpc_Hot_Path()
    {
        const int warmupRpcs = 100;
        const int measuredRpcs = 1_000;

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();
        var entityId = $"counter_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Warmup: avoid measuring JIT + first-touch path-pool allocations.
        for (int i = 0; i < warmupRpcs; i++)
            await api.AddValueAsync(1, i + 1);

        var stats = await MeasureAsync($"RPC hot path × {measuredRpcs:N0}", async () =>
        {
            for (int i = 0; i < measuredRpcs; i++)
                await api.AddValueAsync(1, warmupRpcs + i + 1);
        });

        ReportRate(stats, measuredRpcs, "RPC");
    }

    [Fact(Skip = "Benchmark — run explicitly via --filter \"FullyQualifiedName~AllocationBenchmarks\"", Timeout = 180_000)]
    public async Task Broadcast_Fanout()
    {
        const int warmupRpcs = 50;
        const int measuredRpcs = 1_000;
        const int coSubscribers = 5; // 1 caller + 5 broadcast-receivers per RPC

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var caller = new TestClientSetup(server, playerId: "caller");
        await caller.ConnectAsync();
        var callerResolver = caller.CreateResolver();
        var entityId = $"counter_{Guid.NewGuid():N}";
        var callerApi = await callerResolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Spin up N co-subscribers on the same entity so each RPC produces N broadcast fan-outs.
        var observers = new List<TestClientSetup>(coSubscribers);
        for (int i = 0; i < coSubscribers; i++)
        {
            var obs = new TestClientSetup(server, playerId: $"observer_{i}");
            await obs.ConnectAsync();
            var obsResolver = obs.CreateResolver();
            await obsResolver.GetServiceAsync<CounterServiceApiClient>(entityId);
            observers.Add(obs);
        }

        try
        {
            for (int i = 0; i < warmupRpcs; i++)
                await callerApi.AddValueAsync(1, i + 1);

            var stats = await MeasureAsync($"RPC + {coSubscribers}× broadcast × {measuredRpcs:N0}", async () =>
            {
                for (int i = 0; i < measuredRpcs; i++)
                    await callerApi.AddValueAsync(1, warmupRpcs + i + 1);
            });

            ReportRate(stats, measuredRpcs, "RPC");
            ReportRate(stats, measuredRpcs * coSubscribers, "broadcast-delivery");
        }
        finally
        {
            foreach (var obs in observers)
                await obs.DisposeAsync();
        }
    }

    [Fact(Skip = "Benchmark — run explicitly via --filter \"FullyQualifiedName~AllocationBenchmarks\"", Timeout = 120_000)]
    public async Task Resume_Reclaim()
    {
        const int warmupResumes = 5;
        const int measuredResumes = 200;

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();
        var entityId = $"counter_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await api.AddValueAsync(1, 1); // produces some state so reclaim has something to do

        var dispatcher = client.MetaClient.Dispatcher;
        var sessionId = dispatcher.SessionId;

        for (int i = 0; i < warmupResumes; i++)
            await dispatcher.ConnectSessionAsync(sessionId, dispatcher.LastAcknowledgedSequence, "1.0.0");

        var stats = await MeasureAsync($"Resume reclaim cycles × {measuredResumes:N0}", async () =>
        {
            for (int i = 0; i < measuredResumes; i++)
                await dispatcher.ConnectSessionAsync(sessionId, dispatcher.LastAcknowledgedSequence, "1.0.0");
        });

        ReportRate(stats, measuredResumes, "Resume");
    }

    // ─────────────────────────────────────────────────────────────────

    private record MeasureStats(long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, TimeSpan Elapsed);

    private async Task<MeasureStats> MeasureAsync(string label, Func<Task> action)
    {
        // Force collection and clear pending finalizers so the baseline is stable.
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        var bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var sw = Stopwatch.StartNew();

        await action();

        sw.Stop();
        var bytesAfter = GC.GetTotalAllocatedBytes(precise: true);
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);

        var stats = new MeasureStats(
            bytesAfter - bytesBefore,
            gen0After - gen0Before,
            gen1After - gen1Before,
            gen2After - gen2Before,
            sw.Elapsed);

        _output.WriteLine($"=== {label} ===");
        _output.WriteLine($"  Allocated      : {stats.AllocatedBytes:N0} B  ({stats.AllocatedBytes / 1024.0 / 1024.0:F2} MB)");
        _output.WriteLine($"  GC gen0/gen1/gen2: {stats.Gen0Collections} / {stats.Gen1Collections} / {stats.Gen2Collections}");
        _output.WriteLine($"  Elapsed        : {stats.Elapsed.TotalMilliseconds:F1} ms");
        return stats;
    }

    private void ReportRate(MeasureStats stats, int ops, string opName)
    {
        var bytesPerOp = (double)stats.AllocatedBytes / ops;
        var opsPerSec = ops / stats.Elapsed.TotalSeconds;
        var bytesPerSec = stats.AllocatedBytes / stats.Elapsed.TotalSeconds;
        _output.WriteLine($"  Per {opName,-22}: {bytesPerOp,12:N0} B  ({opsPerSec,12:N0} {opName}/s, {bytesPerSec / 1024.0 / 1024.0,6:F1} MB/s alloc)");
        _output.WriteLine("");
    }
}
