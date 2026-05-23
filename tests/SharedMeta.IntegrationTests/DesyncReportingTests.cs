using SharedMeta.Core.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for the deep desync reporting flow:
/// 1. Server caches recent patches per (entity, service, method)
/// 2. Client detects CRC mismatch via OnPatchDesync
/// 3. Client sends DesyncReport with its local patch bytes
/// 4. Server diffs patches via PatchNodeDiffer and stores DeepDesyncReport in DesyncReportGrain
/// </summary>
[Collection(TestClusterCollection.Name)]
public class DesyncReportingTests
{
    private readonly TestClusterFixture _fixture;

    public DesyncReportingTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task DesyncReporting_Disabled_NoReportsStored()
    {
        // No transportOptions => DesyncReportingEnabled = false
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        var playerId = "report-disabled-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(playerId);

        await api.AddNonDeterministicAsync();
        await Task.Delay(150); // Optimistic continuation

        // OnPatchDesync should still fire (CRC comparison is independent of reporting)
        Assert.NotEmpty(diagnostics.PatchDesyncs);

        // But no report should be stored on the server (reporting disabled)
        var grain = _fixture.GrainFactory.GetGrain<IDesyncReportGrain>(playerId);
        var reports = await grain.GetRecentAsync(10);
        Assert.Empty(reports);
    }

    [Fact(Timeout = 60_000)]
    public async Task DesyncReporting_Enabled_StoresReportInGrain()
    {
        var transportOptions = new MetaTransportOptions
        {
            DesyncReportingEnabled = true,
            DesyncReportPatchCacheSize = 16
        };
        var server = new InProcessServer(_fixture.CreateHandlerFactory(transportOptions));
        var diagnostics = new DesyncCollector();

        var playerId = "report-enabled-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(playerId);

        // Trigger a desync via System.Random method
        await api.AddNonDeterministicAsync();

        // Wait for Optimistic mode ContinueWith + fire-and-forget desync report round-trip
        await Task.Delay(300);

        Assert.NotEmpty(diagnostics.PatchDesyncs);

        // Check that the server stored a report for this player
        var grain = _fixture.GrainFactory.GetGrain<IDesyncReportGrain>(playerId);
        var reports = await grain.GetRecentAsync(10);
        Assert.NotEmpty(reports);

        var report = reports[0];
        Assert.Equal(playerId, report.PlayerId);
        Assert.Equal("IDesyncTestService", report.ServiceName);
        Assert.Equal("AddNonDeterministic", report.MethodName);
        Assert.NotNull(report.ServerPatchBytes);
        Assert.NotNull(report.ClientPatchBytes);
        Assert.NotEqual(report.ServerPatchCrc, report.ClientPatchCrc);
        Assert.NotNull(report.Diff);
        Assert.NotEmpty(report.Diff);
        Assert.NotEmpty(report.TextDiff);
    }

    [Fact(Timeout = 60_000)]
    public async Task DesyncReporting_DeterministicMethod_NoReport()
    {
        var transportOptions = new MetaTransportOptions
        {
            DesyncReportingEnabled = true
        };
        var server = new InProcessServer(_fixture.CreateHandlerFactory(transportOptions));
        var diagnostics = new DesyncCollector();

        var playerId = "report-clean-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(playerId);

        // Deterministic operations only
        await api.AddAsync(10);
        await api.AddAsync(20);
        await api.SetLabelAsync("hello");
        await Task.Delay(150);

        // No desyncs detected
        Assert.Empty(diagnostics.PatchDesyncs);

        // No reports stored
        var grain = _fixture.GrainFactory.GetGrain<IDesyncReportGrain>(playerId);
        var reports = await grain.GetRecentAsync(10);
        Assert.Empty(reports);
    }

    private class DesyncCollector : IDesyncDiagnostics
    {
        public List<(string Service, string Method, uint ServerCrc, uint LocalCrc)> PatchDesyncs { get; } = new();

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult) { }
        public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes) { }
        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta) { }

        public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc)
        {
            PatchDesyncs.Add((serviceName, methodName, serverCrc, localCrc));
        }

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
