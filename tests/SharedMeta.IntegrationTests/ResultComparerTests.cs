using SharedMeta.Core.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration coverage for the <see cref="IMetaResultComparer{T}"/> opt-in:
/// when a comparer is registered for a method's return type, the generated ApiClient must
/// call <c>AreEqual</c> in place of byte-level equality both for the Server and Optimistic
/// execution paths.
///
/// Two scenarios — symmetric to each other — confirm the comparer overrides byte equality
/// in both directions:
/// 1. Comparer returns <c>true</c> ⇒ no <c>OnResultMismatch</c> even when the bytes differ
///    (forced by <c>System.Random</c> in the impl).
/// 2. Comparer returns <c>false</c> ⇒ <c>OnResultMismatch</c> fires even when client and
///    server return identical bytes.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ResultComparerTests
{
    private readonly TestClusterFixture _fixture;

    public ResultComparerTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task AcceptingComparer_SuppressesResultMismatch_DespiteByteDivergence()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new ResultMismatchCollector();

        var playerId = "rc-accept-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<AcceptingComparerServiceApiClient>(playerId);

        // Roll uses System.Random — server and client produce different ints, so
        // serialized bytes will differ. Without the comparer the byte-comparison would
        // fire OnResultMismatch.
        await api.RollAsync();

        // Wait for fire-and-forget Optimistic continuation to land.
        await Task.Delay(150);

        Assert.Empty(diagnostics.Mismatches);
    }

    [Fact(Timeout = 60_000)]
    public async Task RejectingComparer_FiresResultMismatch_DespiteIdenticalBytes()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new ResultMismatchCollector();

        var playerId = "rc-reject-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<RejectingComparerServiceApiClient>(playerId);

        // Echo is deterministic — server and client return identical RejectingPayload
        // instances and therefore identical bytes. With the rejecting comparer, the
        // generated client must still surface OnResultMismatch.
        await api.EchoAsync(42);

        await Task.Delay(150);

        Assert.NotEmpty(diagnostics.Mismatches);
        var (svc, method) = diagnostics.Mismatches[0];
        Assert.Equal("IRejectingComparerService", svc);
        Assert.Equal("Echo", method);
    }

    private sealed class ResultMismatchCollector : IDesyncDiagnostics
    {
        public List<(string Service, string Method)> Mismatches { get; } = new();

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
            => Mismatches.Add((serviceName, methodName));

        public void OnCrossEntityResult(string entityId, string serviceName, string methodName, byte[]? resultBytes) { }
        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta) { }
        public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc) { }
        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
