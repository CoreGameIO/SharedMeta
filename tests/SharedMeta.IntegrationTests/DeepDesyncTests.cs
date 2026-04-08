using SharedMeta.Core.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for deep desync detection.
/// Tests that patch-level CRC comparison detects state-level desyncs
/// even when return values match.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class DeepDesyncTests
{
    private readonly TestClusterFixture _fixture;

    public DeepDesyncTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task DeterministicMethod_NoPatchDesync()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        await using var client = new TestClientSetup(server, "deep-desync-ok-" + Guid.NewGuid().ToString("N")[..8],
            diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = client.PlayerId;
        var api = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        // Deterministic operations — should not trigger patch desync
        await api.AddAsync(10);
        await api.AddAsync(20);
        await api.SetLabelAsync("hello");

        Assert.Empty(diagnostics.PatchDesyncs);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task NonDeterministicMethod_DetectsPatchDesync()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        await using var client = new TestClientSetup(server, "deep-desync-bad-" + Guid.NewGuid().ToString("N")[..8],
            diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = client.PlayerId;
        var api = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        // Non-deterministic — System.Random causes different state on server vs client
        // Return value is always 0, so result-level check passes.
        // But patch CRC should differ because state.Value diverges.
        var result = await api.AddNonDeterministicAsync();

        // Wait a bit for async validation in Optimistic mode
        await Task.Delay(100);

        // Deep desync should have been detected
        Assert.NotEmpty(diagnostics.PatchDesyncs);
        var desync = diagnostics.PatchDesyncs[0];
        Assert.Equal("IDesyncTestService", desync.ServiceName);
        Assert.Equal("AddNonDeterministic", desync.MethodName);
        Assert.NotEqual(desync.ServerCrc, desync.LocalCrc);
    }

    /// <summary>
    /// Stress test for the server-side request reordering gate (stash + drain in
    /// <c>SessionManagerGrain</c>). Fires many deterministic Optimistic calls so the
    /// threadpool is free to deliver them out of submission order to the in-process
    /// transport. With <c>SessionManagerOptions.EnforceRpcOrder = true</c> (set by the
    /// fixture) the session manager must stash out-of-order requests and drain them in
    /// monotonic <c>RequestId</c> order before forwarding to the entity grain, so all
    /// patch CRCs match and no patch desync is reported.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ConcurrentDeterministicCalls_NoPatchDesync_StressGate()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        await using var client = new TestClientSetup(server, "deep-desync-stress-" + Guid.NewGuid().ToString("N")[..8],
            diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = client.PlayerId;
        var api = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        // 50 sequential awaits — each schedules an Optimistic call but the local
        // continuation runs without preserving submission order across the threadpool.
        // Without server-side reordering this trips on the very first ~3 calls.
        const int callCount = 50;
        for (int i = 1; i <= callCount; i++)
        {
            await api.AddAsync(i);
        }

        // Wait for all fire-and-forget Optimistic continuations to drain
        await Task.Delay(300);

        Assert.Empty(diagnostics.PatchDesyncs);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task MixedMethods_OnlyNonDeterministicDesyncs()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        await using var client = new TestClientSetup(server, "deep-desync-mix-" + Guid.NewGuid().ToString("N")[..8],
            diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = client.PlayerId;
        var api = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        // Deterministic
        await api.AddAsync(5);
        Assert.Empty(diagnostics.PatchDesyncs);

        // Non-deterministic
        await api.AddNonDeterministicAsync();
        await Task.Delay(100); // Wait for Optimistic ContinueWith callback
        Assert.Single(diagnostics.PatchDesyncs);

        // Deterministic again — state is already diverged,
        // but this individual operation should still produce matching patches
        // (both sides add the same amount to their respective diverged states)
        diagnostics.PatchDesyncs.Clear();
        await api.AddAsync(10);
        // This Add(10) on both sides adds 10 — patch says "Value changed" with same delta,
        // but the actual serialized value differs because base state diverged.
        // So patch CRC may or may not match depending on implementation.
        // We just verify no crash.
    }

    /// <summary>
    /// Collects desync diagnostics for test assertions.
    /// </summary>
    private class DesyncCollector : IDesyncDiagnostics
    {
        public List<PatchDesyncInfo> PatchDesyncs { get; } = new();
        public List<string> ResultMismatches { get; } = new();

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
        {
            ResultMismatches.Add($"{serviceName}.{methodName}: server={serverResult}, local={localResult}");
        }

        public void OnCrossEntityResult(string entityId, string serviceName, string methodName, byte[]? resultBytes) { }

        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta) { }

        public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc)
        {
            PatchDesyncs.Add(new PatchDesyncInfo(serviceName, methodName, serverCrc, localCrc));
        }

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }

    public record PatchDesyncInfo(string ServiceName, string MethodName, uint ServerCrc, uint LocalCrc);
}
