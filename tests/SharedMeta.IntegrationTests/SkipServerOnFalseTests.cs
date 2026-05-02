using System.Linq;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Coverage for <c>[MetaMethod(Mode = ExecutionMode.Optimistic, SkipServerOnFalse = true)]</c>.
///
/// Contract under test:
///   - When the local impl returns <c>true</c>, the generated client wraps the call in
///     <c>if (!EqualityComparer&lt;T&gt;.Default.Equals(localResult, default))</c> and fires
///     the server RPC fire-and-forget — server-side observer records the call.
///   - When the local impl returns <c>false</c> (the validation-failed branch), the
///     wrapper short-circuits: server never receives the RPC, no traffic, no replay.
///   - Pre-0.17.0 the SimplifiedApiClientGenerator silently ignored the flag and always
///     fired the RPC — this test guards against the regression.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SkipServerOnFalseTests
{
    private readonly TestClusterFixture _fixture;

    public SkipServerOnFalseTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task TryAdd_ReturnsTrue_ServerReceivesRpc()
    {
        CounterService.SkipServerOnFalseLog.Clear();

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "skip-true-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(playerId);

        // Local returns true → generated wrapper sends the RPC to the server.
        var result = await api.TryAddAsync(42);
        Assert.True(result);

        // Barrier: any Server-mode call on the same entity is serialised after the prior
        // TryAdd RPC by Orleans grain single-threading; once it returns, we know the server
        // dispatched TryAdd as well. PingAsync is the standard barrier elsewhere in the
        // suite (see NamedRandomTests).
        await api.PingAsync();

        var ours = CounterService.SkipServerOnFalseLog
            .Where(e => e.CallerId == playerId)
            .ToList();
        Assert.Single(ours);
        Assert.Equal(42, ours[0].Amount);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task TryAdd_ReturnsFalse_ServerSkipped()
    {
        CounterService.SkipServerOnFalseLog.Clear();

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "skip-false-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(playerId);

        // amount <= 0 → impl returns false → server RPC is short-circuited.
        var negative = await api.TryAddAsync(-5);
        var zero = await api.TryAddAsync(0);
        Assert.False(negative);
        Assert.False(zero);

        // Drain any in-flight continuations and force a server round-trip so the test
        // doesn't race against an as-yet-unsent RPC. PingAsync's RPC is independent of TryAdd
        // — its successful return doesn't imply TryAdd was sent, only that anything that
        // *was* sent is now serialised behind it. So if the server-side log still contains
        // no TryAdd entries, the wrapper genuinely skipped them.
        await api.PingAsync();

        var ours = CounterService.SkipServerOnFalseLog
            .Where(e => e.CallerId == playerId)
            .ToList();
        Assert.Empty(ours);
        Assert.Empty(client.DetectedIssues);
    }

    [Fact(Timeout = 60_000)]
    public async Task TryAdd_MixedTrueFalse_OnlyTrueReachesServer()
    {
        CounterService.SkipServerOnFalseLog.Clear();

        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "skip-mix-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(playerId);

        // Interleave: true(10) → false(0) → true(20) → false(-1) → true(5)
        // Three of five should reach the server; two should be skipped client-side.
        Assert.True(await api.TryAddAsync(10));
        Assert.False(await api.TryAddAsync(0));
        Assert.True(await api.TryAddAsync(20));
        Assert.False(await api.TryAddAsync(-1));
        Assert.True(await api.TryAddAsync(5));

        await api.PingAsync();

        var ours = CounterService.SkipServerOnFalseLog
            .Where(e => e.CallerId == playerId)
            .OrderBy(e => e.Amount)
            .ToList();
        Assert.Equal(3, ours.Count);
        Assert.Equal(new[] { 5, 10, 20 }, ours.Select(e => e.Amount).ToArray());
        Assert.Empty(client.DetectedIssues);
    }
}
