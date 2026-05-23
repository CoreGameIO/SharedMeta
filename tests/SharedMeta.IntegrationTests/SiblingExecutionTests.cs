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
/// 0.20.0 sibling-execution coverage. Each test exercises the sibling-bypass path
/// (calling another service hosted on the same entity's TState) under a different
/// execution mode or a different framework integration point — patch tracking,
/// change tracking ([Tracked]), random scrolls, recursion, mixing with real
/// cross-grain RPC. The tests share <see cref="ICounterService"/>+<see cref="ICounterAuxService"/>
/// on <see cref="CounterState"/> as the sibling pair.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SiblingExecutionTests
{
    private readonly TestClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SiblingExecutionTests(TestClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Baseline: outer ICounterService.SiblingAuxAdd dispatches via the implicit
    /// <c>GetICounterAuxService(self)</c> path. Server's self-detect routes to the
    /// cached sibling impl directly (no grain RPC, no serialization). The mutation
    /// applied by AuxAdd is reflected in the caller's state.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingAuxAdd_ImplicitGetter_AppliesMutation()
    {
        var entityId = $"sibling_implicit_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var sumAfter = await counter.SiblingAuxAddAsync(42);

        Assert.Equal(42, sumAfter);
        Assert.Equal(42, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Explicit <c>Get{Iface}SiblingAsync()</c> accessor: returns the original
    /// <see cref="ICounterAuxService"/> interface, not the async EntityCaller wrapper.
    /// The await resolves the callee's typed Config through its provider (async).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingAuxAdd_ExplicitSiblingAsync_AppliesMutation()
    {
        var entityId = $"sibling_explicit_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var sumAfter = await counter.SiblingAuxAddExplicitAsync(15);

        Assert.Equal(15, sumAfter);
        Assert.Equal(15, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// ServerPatch outer: the inner sibling-call's mutations must flow into the same
    /// PatchWrapper, so the broadcast carries one PatchBytes covering both. Without
    /// this guarantee, the client-side patch applier would miss the inner sibling's
    /// state changes and diverge from the server.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingAuxAdd_ServerPatchOuter_PatchCoversInnerMutation()
    {
        _fixture.ExecutionModeProvider.SetMode(global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_SiblingAuxAdd_v0, ExecutionMode.ServerPatch);
        try
        {
            var entityId = $"sibling_patch_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var alice = new TestClientSetup(server, "alice");
            await using var bob = new TestClientSetup(server, "bob");
            await alice.ConnectAsync();
            await bob.ConnectAsync();

            // Bob holds the calling client; Alice subscribes only to observe broadcasts.
            var aliceCounter = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
            var bobCounter = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            var sumAfter = await bobCounter.SiblingAuxAddAsync(100);

            await Task.Delay(300);

            Assert.Equal(100, sumAfter);
            // Bob saw the local optimistic apply (or server response patch). Alice received
            // the broadcast PatchBytes and applied — both should be at 100.
            Assert.Equal(100, bobCounter.State.Sum);
            Assert.Equal(100, aliceCounter.State.Sum);
            Assert.Empty(alice.DetectedIssues);
            Assert.Empty(bob.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    /// <summary>
    /// ServerReplace outer: the inner sibling-call's mutations must be present in
    /// the full StateBytes the server ships back. Client replaces wholesale state
    /// and arrives at the same Sum.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingAuxAdd_ServerReplaceOuter_StateBytesCoverInnerMutation()
    {
        _fixture.ExecutionModeProvider.SetMode(global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_SiblingAuxAdd_v0, ExecutionMode.ServerReplace);
        try
        {
            var entityId = $"sibling_replace_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var alice = new TestClientSetup(server, "alice");
            await using var bob = new TestClientSetup(server, "bob");
            await alice.ConnectAsync();
            await bob.ConnectAsync();

            var aliceCounter = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
            var bobCounter = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            var sumAfter = await bobCounter.SiblingAuxAddAsync(77);

            await Task.Delay(300);

            Assert.Equal(77, sumAfter);
            Assert.Equal(77, bobCounter.State.Sum);
            Assert.Equal(77, aliceCounter.State.Sum);
            Assert.Empty(alice.DetectedIssues);
            Assert.Empty(bob.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    /// <summary>
    /// CrossOptimistic outer: client also runs the outer locally before server confirms.
    /// The sibling-bypass on client must dispatch the inner sibling on a transient impl
    /// bound to the same Context, so the local mirror sees the mutation immediately.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingAuxAdd_CrossOptimisticOuter_LocalMirrorMutates()
    {
        _fixture.ExecutionModeProvider.SetMode(global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_SiblingAuxAdd_v0, ExecutionMode.CrossOptimistic);
        try
        {
            var entityId = $"sibling_crossopt_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server, "alice");
            await client.ConnectAsync();
            var resolver = client.CreateResolver();

            var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            var sumAfter = await counter.SiblingAuxAddAsync(33);

            Assert.Equal(33, sumAfter);
            Assert.Equal(33, counter.State.Sum);
            Assert.Empty(client.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    /// <summary>
    /// Sibling mutates a [Tracked] field — outer's ChangeTracker collects the change
    /// and (server-side) the broadcast carries it; client picks up via the [Tracked]
    /// field setter. Verifies sibling-call mutations flow through the change-tracking
    /// pipeline the same way private-helper mutations do.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingTrackedBump_ServerOuter_ReactiveCounterAdvances()
    {
        var entityId = $"sibling_tracked_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        var initialReactive = counter.State.ReactiveCounter;

        await counter.SiblingTrackedBumpAsync(7);

        Assert.Equal(initialReactive + 7, counter.State.ReactiveCounter);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Sibling pulls from a NamedRandom stream (Combat). The shared <c>_optimisticRandom</c>
    /// for that named slot must advance, and client-side replay must produce the same value
    /// as the server. Otherwise client and server diverge on subsequent reads.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingDrawCombat_OptimisticOuter_RandomAdvancesAndMatches()
    {
        var entityId = $"sibling_random_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var first = await counter.SiblingDrawCombatAsync(1000);
        var second = await counter.SiblingDrawCombatAsync(1000);

        // Random is deterministic — two consecutive draws must differ to confirm the scroll
        // actually advanced (otherwise we'd be reading the same first value twice).
        Assert.NotEqual(first, second);
        Assert.InRange(first, 0, 999);
        Assert.InRange(second, 0, 999);
        // No desync issues — client and server agreed on every value.
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Recursive sibling: A → B → A. Inner call lands back on the original service via the
    /// runtime <c>SiblingServiceResolver</c> (server: provider's cached impl; client: a
    /// transient impl built by the generated <see cref="MetaServiceConfig.ClientSiblingFactory"/>
    /// bound to the calling client-side MetaContext). Verifies the resolution works in both
    /// directions of the cycle and that nested mutations land in the same outer state.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingRecursive_AppliesNestedMutation()
    {
        var entityId = $"sibling_recursive_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // SiblingRecursive(50) → AuxService.CallBackToCounter(50) → CounterService.AddValue(50, -2)
        // → state.Sum += 50.
        var result = await counter.SiblingRecursiveAsync(50);

        Assert.Equal(50, result);
        Assert.Equal(50, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Sibling A → sibling B → real cross-entity to a DIFFERENT entity. Verifies that
    /// sibling-bypass and real grain-RPC routing coexist: the inner cross-entity hop
    /// (otherEntityId != self) uses a real RPC even though the outer call sat inside a
    /// sibling. Target entity's state mutates; the calling entity's state does not.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingThenCrossEntity_TargetEntityMutatesIndependently()
    {
        var entityIdA = $"sibling_xent_a_{Guid.NewGuid():N}";
        var entityIdB = $"sibling_xent_b_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // Both entities subscribed so the cross-entity hop has a target to land on.
        var counterA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityIdA);
        var counterB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityIdB);

        var resultAtB = await counterA.SiblingThenCrossEntityAsync(entityIdB, 25);

        await Task.Delay(200);

        Assert.Equal(25, resultAtB);
        Assert.Equal(25, counterB.State.Sum);
        Assert.Equal(0, counterA.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Multi-config sibling: outer service uses <see cref="CounterConfig"/> (primary,
    /// MaxValue=1000), sibling uses a different typed config <see cref="CounterAltConfig"/>
    /// (MaxValue=7777). Verifies that <c>Get{Iface}SiblingAsync()</c>'s async config-resolution
    /// picks up the sibling's own typed config — not the caller's — by writing AltConfig.MaxValue
    /// into shared state and asserting the result. ServerReplace mode keeps the test simple
    /// (no client-side replay path executes the impl, so client doesn't need DI access to the
    /// AltConfig provider).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingMultiConfig_SiblingSeesItsOwnTypedConfig()
    {
        // Server-side mode override is required so the server actually emits StateBytes.
        // Without it the server runs the method optimistically and skips state-bytes —
        // the client then falls back to local replay, which on multi-config siblings would
        // need DI access to IMetaConfigProvider<CounterAltConfig> (Server.Core type, not
        // referenceable from the shared Test.Meta1 assembly that compiles for both sides).
        _fixture.ExecutionModeProvider.SetMode(global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_SiblingMultiConfig_v0, ExecutionMode.ServerReplace);
        try
        {
            var entityId = $"sibling_multicfg_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server, "alice");
            await client.ConnectAsync();
            var resolver = client.CreateResolver();

            var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            var sumAfter = await counter.SiblingMultiConfigAsync();

            // 7777 is CounterAltConfig.MaxValue — proves the sibling's Config was the alt
            // config, not CounterService's primary CounterConfig (MaxValue=1000).
            Assert.Equal(7777, sumAfter);
            Assert.Equal(7777, counter.State.Sum);
            Assert.Empty(client.DetectedIssues);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    /// <summary>
    /// Multiple sibling calls in one outer method. Cumulative mutations: 5 calls of 100 each
    /// produces state.Sum = 500. Exercises sibling-resolver caching (same instance reused),
    /// PatchWrapper accumulation across calls, and async-await flow through repeated
    /// invocations.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingAuxAddTimes_MultipleCalls_CumulateOnState()
    {
        var entityId = $"sibling_multi_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var sumAfter = await counter.SiblingAuxAddTimesAsync(100, 5);

        Assert.Equal(500, sumAfter);
        Assert.Equal(500, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Sibling throws after a partial state mutation. Outer catches and returns a sentinel.
    /// Verifies:
    ///   • The exception crosses the sibling-call boundary (outer's catch fires).
    ///   • Framework doesn't crash the grain or corrupt the broadcast pipeline (no desync).
    ///   • Documented behaviour: sibling-bypass shares the outer's mutation pipeline by design,
    ///     so the partial mutation IS observable (no implicit rollback). The state.Sum will
    ///     contain the partial value.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingThrowsCaught_OuterRecoversAndStateContainsPartial()
    {
        var entityId = $"sibling_throw_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var sentinel = await counter.SiblingThrowsCaughtAsync(42);

        // Sentinel from outer's catch block — proves the throw crossed the sibling boundary.
        Assert.Equal(999, sentinel);
        // Partial mutation is observable — sibling-bypass doesn't roll back state. This is
        // the documented 0.20.0 behaviour; user code that needs rollback must use try/catch
        // around explicit snapshots.
        Assert.Equal(42, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Sibling returns a typed complex collection (List of MemoryPackable). The explicit
    /// <see cref="ICounterAuxService.AuxSnapshotOps"/> is invoked through SiblingCaller's
    /// typed pass-through — no serialization in sibling-bypass, so the returned list is
    /// the same reference (or at least identical contents).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingReturnsOps_ComplexReturnType_PassedThrough()
    {
        var entityId = $"sibling_complexret_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var opsCount = await counter.SiblingReturnsOpsAsync(7);

        // Outer staged 1 op before asking sibling for a snapshot — sibling returned that 1 op.
        Assert.Equal(1, opsCount);
        Assert.Single(counter.State.Operations);
        Assert.Equal(7, counter.State.Operations[0].Value);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Sibling consumes <c>Context.ServerRandom</c>. Server records the value, broadcast
    /// payload carries the recording, client replays the SAME value. If sibling-bypass
    /// leaked a separate recording context, client and server would diverge → desync issue.
    /// Test asserts the client-side state (via response) matches what the server recorded.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingServerRandom_RecordReplaySymmetry()
    {
        var entityId = $"sibling_srvrnd_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var first = await counter.SiblingServerRandomAsync(1000);
        var second = await counter.SiblingServerRandomAsync(1000);

        // ServerRandom is deterministic but advances across calls — two consecutive draws
        // must differ to confirm the underlying stream actually moved.
        Assert.NotEqual(first, second);
        Assert.InRange(first, 0, 999);
        Assert.InRange(second, 0, 999);
        // No desync: server and client agreed on every draw via the recorded payload.
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Sibling-bypass passes objects BY REFERENCE — typed in-process call, no serialization
    /// boundary, no Box/Unbox. The caller observes mutations the sibling made to the passed
    /// instance. This is the same reason argument transformers don't apply on the sibling
    /// path: <c>[Transformer]</c>'s Box runs on serialization-write and Unbox on
    /// serialization-read, both of which are skipped when sibling-bypass takes the typed
    /// direct call path.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingByReference_PassesObjectByReference_NoSerialization()
    {
        var entityId = $"sibling_byref_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var ticksAfter = await counter.SiblingByReferenceAsync();

        // 999 = the value AuxMutateOpInPlace wrote into the caller's CounterOperation.
        // The caller's local `op` instance was modified by sibling — proving by-reference
        // pass-through (no serialization round-trip would lose the mutation).
        Assert.Equal(999, ticksAfter);
        Assert.Empty(client.DetectedIssues);
    }
}
