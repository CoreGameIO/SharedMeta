using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// A <c>ValueTask</c>-returning service method has to behave exactly like a <c>Task</c> one.
///
/// The server dispatcher classified return types by string and recognised only "Task" and
/// "Task&lt;". A <c>ValueTask</c> method fell through to the "synchronous T" branch: the body was
/// never awaited, and the <c>ValueTask</c> struct itself was handed to
/// <c>PackForExternalUsage</c>. It compiled and it failed at runtime — and no test project
/// declared such a method, so the suite stayed green through the whole 0.40.0 cycle that
/// explicitly taught every other emitter about <c>ValueTask</c>.
///
/// These run Server mode, so the assertions are about what the authoritative server computed,
/// not about a local optimistic guess.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ValueTaskDispatchTests
{
    private readonly TestClusterFixture _fixture;

    public ValueTaskDispatchTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Plain <c>ValueTask</c> — the void-ish shape.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ValueTaskMethod_MutatesServerState()
    {
        var entityId = $"vt_void_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var aux = await client.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);

        await aux.AuxValueTaskAddAsync(7);

        Assert.Equal(7, aux.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// <c>ValueTask&lt;T&gt;</c> — the result must be the awaited <c>int</c>. Under the old
    /// classification this returned a serialized <c>ValueTask</c> struct.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ValueTaskOfTMethod_ReturnsAwaitedValue()
    {
        var entityId = $"vt_result_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var aux = await client.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);

        var result = await aux.AuxValueTaskAddReturningAsync(5);

        Assert.Equal(5, result);
        Assert.Equal(5, aux.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// The impl yields before mutating, so the dispatcher's <c>IsCompletedSuccessfully</c> check
    /// fails and the call must travel through the generated async tail — the one path where the
    /// <c>ValueTask</c> is actually converted. Without this the sync-completion fast path would
    /// cover both other tests and a broken tail would go unnoticed.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SuspendingValueTaskMethod_CompletesThroughAsyncTail()
    {
        var entityId = $"vt_susp_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var aux = await client.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);

        var result = await aux.AuxValueTaskSuspendAsync(9);

        Assert.Equal(9, result);
        Assert.Equal(9, aux.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Several calls in a row: the awaited body must run exactly once each. A never-awaited
    /// ValueTask could plausibly pass a single-call assertion by accident; accumulation pins that
    /// each dispatch actually completed before the next one started.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RepeatedValueTaskCalls_AccumulateExactlyOnce()
    {
        var entityId = $"vt_repeat_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var aux = await client.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);

        await aux.AuxValueTaskAddAsync(1);
        await aux.AuxValueTaskAddReturningAsync(2);
        await aux.AuxValueTaskSuspendAsync(3);

        Assert.Equal(6, aux.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }
}
