using System;
using System.Collections.Generic;
using System.Linq;
using SharedMeta.Client;
using SharedMeta.Client.Network;
using SharedMeta.Core.Logging;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Coverage for <c>MetaRef&lt;T&gt;</c> — the connection-safe handle a DI container can hold for
/// the process lifetime.
///
/// - Reports a miss before the entity is subscribed, without subscribing or throwing.
/// - Hands back the live client once resolved, and keeps the reader allocation-free.
/// - Survives a disconnect: the same handle reports dead, then resolves the NEW client after a
///   re-subscribe. A handle caching the instance would still be pointing at the disposed one.
/// - A PlayerId-bound handle follows the player instead of pinning the id it was built with.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class MetaRefTests
{
    private readonly TestClusterFixture _fixture;

    public MetaRefTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task MetaRef_BeforeResolve_IsNotAlive()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-miss-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var handle = new MetaRef<PartyServiceApiClient>(resolver, playerId);

        Assert.False(handle.IsAlive);
        Assert.Null(handle.Current);
        Assert.False(handle.TryGet(out var api));
        Assert.Null(api);
    }

    [Fact(Timeout = 60_000)]
    public async Task MetaRef_AfterResolve_ReturnsLiveClient()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-hit-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var handle = new MetaRef<PartyServiceApiClient>(resolver, playerId);
        var resolved = await handle.GetAsync();

        Assert.Same(resolved, handle.Current);
        Assert.Same(resolved, await resolver.GetServiceAsync<PartyServiceApiClient>(playerId));
        Assert.True(handle.IsAlive);
    }

    /// <summary>
    /// The reason the handle re-resolves instead of caching. A disconnect disposes the API client
    /// and drops the connection; the same handle must then report dead and, after a re-subscribe,
    /// hand back the newly created client rather than the disposed one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MetaRef_SurvivesDisconnectAndRepointsAtTheNewClient()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-recycle-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var handle = new MetaRef<PartyServiceApiClient>(resolver, playerId);
        var first = await handle.GetAsync();

        await resolver.DisconnectAsync(playerId);

        Assert.False(handle.IsAlive);
        Assert.Null(handle.Current);

        var second = await handle.GetAsync();

        Assert.NotSame(first, second);
        Assert.Same(second, handle.Current);
    }

    [Fact(Timeout = 60_000)]
    public async Task MetaRef_DisconnectRaisesConnectionInvalidated()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-event-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        var invalidated = new List<(string EntityId, Type StateType)>();
        resolver.ConnectionInvalidated += (id, stateType) => invalidated.Add((id, stateType));

        await resolver.DisconnectAsync(playerId);

        var entry = Assert.Single(invalidated);
        Assert.Equal(playerId, entry.EntityId);
        Assert.NotNull(entry.StateType);
    }

    /// <summary>
    /// A handle built before login has no entity yet. It must stay silent rather than throw from
    /// the accessor a DI-held object gets polled on.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MetaRef_WithDynamicEntityId_FollowsTheIdAndToleratesItBeingUnset()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-dynamic-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        string? currentId = null;
        var handle = new MetaRef<PartyServiceApiClient>(resolver, () => currentId);

        // Pre-login: no id, no throw, no subscribe.
        Assert.Null(handle.EntityId);
        Assert.False(handle.IsAlive);
        Assert.Null(handle.Current);
        Assert.Throws<InvalidOperationException>(() => handle.GetAsync());

        currentId = playerId;
        var resolved = await handle.GetAsync();

        Assert.Equal(playerId, handle.EntityId);
        Assert.Same(resolved, handle.Current);
    }

    [Fact(Timeout = 60_000)]
    public async Task MetaRef_ReaderIsAllocationFreeOnTheHotPath()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-alloc-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var handle = new MetaRef<PartyServiceApiClient>(resolver, playerId);
        await handle.GetAsync();

        for (int i = 0; i < 1_000; i++)
            handle.TryGet(out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100_000; i++)
        {
            handle.TryGet(out var api);
            if (api == null) throw new InvalidOperationException("unexpected null on cached hot path");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// The hazard that makes the handle worth having. Disposing the connection only detaches the
    /// broadcast subscription — the dispatcher underneath is shared and still alive — so a captured
    /// client used to keep sending successfully while never receiving again, freezing its state
    /// mirror at the moment of the disconnect with every call still reporting success.
    /// <para>
    /// This covers the awaited modes, where the failure reaches the caller.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CapturedApiClient_AfterDisconnect_ThrowsOnAnAwaitedCall()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-stale-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();

        // AddValue is declared Server mode — the caller awaits the wire call, so a dead connection
        // has someone to report to.
        var captured = await resolver.GetServiceAsync<CounterServiceApiClient>(playerId);
        await captured.AddValueAsync(1, 1);

        await resolver.DisconnectAsync(playerId);

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => captured.AddValueAsync(1, 2));
        Assert.Contains(playerId, ex.Message);

        // The handle is the supported way through: it resolves a live client on the same entity.
        var handle = new MetaRef<CounterServiceApiClient>(resolver, playerId);
        var revived = await handle.GetAsync();
        Assert.NotSame(captured, revived);
        await revived.AddValueAsync(1, 2);
    }

    /// <summary>
    /// Optimistic mutates local state first and sends afterwards, so a guard on the way to the
    /// wire would leave the client holding a mutation it could never deliver. The refusal has to
    /// happen before the method body runs: the call throws AND local state is untouched.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task CapturedApiClient_AfterDisconnect_RefusesOptimisticBeforeMutatingLocalState()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-stale-opt-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var captured = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);
        await captured.AddHeroAsync(1, "Aragorn", level: 5);
        Assert.Equal(5, captured.TotalLevelsSync());

        await resolver.DisconnectAsync(playerId);

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => captured.AddHeroAsync(2, "Legolas", level: 6));
        Assert.Contains("AddHero", ex.Message);
        Assert.Contains(playerId, ex.Message);

        // The mutation never ran: a guard placed at the send would have left 11 here.
        Assert.Equal(5, captured.TotalLevelsSync());
    }

    /// <summary>
    /// A send can still fail after the local apply for reasons that have nothing to do with a
    /// disconnect — the transport dropping, the server going away mid-call. Those faults used to
    /// be dropped on the floor: the continuation handled only the success case and nothing read
    /// the task's exception, so it could resurface at best as an unattributed
    /// UnobservedTaskException.
    /// </summary>
    [Fact]
    public void FailedOptimisticSend_IsReportedWithItsServiceAndMethod()
    {
        var logger = new CapturingLogger();
        var previousLogger = MetaLog.Logger;
        MetaLog.SetLogger(logger);
        try
        {
            var failed = Task.FromException(new InvalidOperationException("transport went away"));

            OptimisticSend.ReportIfFailed(failed, "IPartyService", "AddHero");

            var message = logger.Find("[Optimistic]");
            Assert.NotNull(message);
            Assert.Contains("IPartyService", message!);
            Assert.Contains("AddHero", message!);
            Assert.Contains("transport went away", message!);

            // Reading the exception is also what keeps it from becoming unobserved.
            Assert.True(failed.Exception!.InnerException is InvalidOperationException);

            // A completed send says nothing.
            logger.Clear();
            OptimisticSend.ReportIfFailed(Task.CompletedTask, "IPartyService", "AddHero");
            Assert.Null(logger.Find("[Optimistic]"));
        }
        finally
        {
            MetaLog.SetLogger(previousLogger);
        }
    }

    private sealed class CapturingLogger : IMetaLogger
    {
        private readonly List<string> _errors = new();

        public string? Find(string fragment)
        {
            lock (_errors)
                return _errors.Find(m => m.Contains(fragment));
        }

        public void Clear()
        {
            lock (_errors) _errors.Clear();
        }

        public bool IsEnabled(MetaLogLevel level) => true;

        public void Log(MetaLogLevel level, string message)
        {
            if (level != MetaLogLevel.Error) return;
            lock (_errors) _errors.Add(message);
        }

        public void Log(MetaLogLevel level, string message, Exception exception) => Log(level, message);
    }

    /// <summary>
    /// The DI shape. A container resolving a constructor parameter holds a <c>Type</c>, not a
    /// generic argument, so it has to be able to answer one without reflection — that is the
    /// problem PR #9 set out to solve. The generated container already built every handle, so the
    /// hook is a dictionary hit and no service is named anywhere in the composition root.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MetaRefs_AnswersAContainerHoldingOnlyAType()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-di-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var refs = new MetaRefs(client.MetaClient);

        // What a container hook does: resolve the constructor parameter type it was handed.
        Assert.True(refs.TryGet(typeof(MetaRefFactory<PartyServiceApiClient>), out var handle));
        Assert.Same(refs.PartyService, handle);

        // Types it knows nothing about are declined, not thrown at — a container asks about
        // everything it is wired for.
        Assert.False(refs.TryGet(typeof(string), out _));
        Assert.False(refs.TryGet(null!, out _));

        // The bind-up-front form: every handle, no service named by hand.
        Assert.Contains(typeof(MetaRefFactory<PartyServiceApiClient>), refs.All.Keys);
        Assert.Equal(refs.All.Count, refs.All.Values.Distinct().Count());

        // And the handle a container hands out actually resolves.
        var party = ((MetaRefFactory<PartyServiceApiClient>)handle).For(playerId);
        var api = await party.GetAsync();
        Assert.Same(api, party.Current);
    }

    /// <summary>
    /// The fully-typed registration path. Every handle arrives with its API client type as a real
    /// generic argument, so a container binder written against it never touches <c>Type</c> or
    /// <c>object</c> — and a service added later is visited without editing the composition root.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MetaRefs_VisitsEveryServiceWithItsTypeAsAGenericArgument()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-visit-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var refs = new MetaRefs(client.MetaClient);
        var binder = new RecordingBinder();

        refs.Accept(binder);

        // Every handle was visited, and each landed on the overload matching how its entity is
        // addressed rather than being flattened to one untyped callback.
        Assert.Equal(refs.All.Count, binder.Handles.Count + binder.Factories.Count);
        Assert.Contains(typeof(PartyServiceApiClient), binder.Factories);

        // The binder captured a usable, correctly-typed handle — no cast anywhere above.
        var party = Assert.IsType<MetaRefFactory<PartyServiceApiClient>>(binder.Bound[typeof(PartyServiceApiClient)]);
        Assert.Same(refs.PartyService, party);

        Assert.Throws<ArgumentNullException>(() => refs.Accept(null!));
    }

    /// <summary>Stands in for a container binder written against the typed bind API.</summary>
    private sealed class RecordingBinder : IMetaRefVisitor
    {
        public readonly List<Type> Handles = new();
        public readonly List<Type> Factories = new();
        public readonly Dictionary<Type, object> Bound = new();

        public void Visit<TApiClient>(MetaRef<TApiClient> handle) where TApiClient : class
        {
            Handles.Add(typeof(TApiClient));
            Bound[typeof(TApiClient)] = handle;
        }

        public void Visit<TApiClient>(MetaRefFactory<TApiClient> factory) where TApiClient : class
        {
            Factories.Add(typeof(TApiClient));
            Bound[typeof(TApiClient)] = factory;
        }
    }

    /// <summary>
    /// A composition root normally runs before the meta client exists, and a host that
    /// re-bootstraps hands out a different client afterwards. A container built from the accessor
    /// must survive both: report a miss while there is no client, then follow the current one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task MetaRefs_BuiltBeforeTheClientExists_FollowsWhicheverClientIsCurrent()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-late-" + Guid.NewGuid().ToString("N")[..8];

        TestClientSetup? current = null;
        // Registered at composition-root time: there is no client yet.
        var refs = new MetaRefs(() => current?.MetaClient);

        Assert.False(refs.PartyService.For(playerId).IsAlive);
        Assert.Null(refs.PartyService.For(playerId).Current);

        await using (var first = new TestClientSetup(server, playerId))
        {
            current = first;
            await first.ConnectAsync();
            first.CreateResolver();

            var handle = refs.PartyService.For(playerId);
            var api = await handle.GetAsync();
            Assert.Same(api, handle.Current);
        }

        // The host dropped its client — the same container reports a miss rather than throwing.
        current = null;
        Assert.False(refs.PartyService.For(playerId).IsAlive);

        // Re-bootstrap with a different client: the container follows it without being rebuilt.
        await using var second = new TestClientSetup(server, playerId);
        current = second;
        await second.ConnectAsync();
        second.CreateResolver();

        var revived = await refs.PartyService.For(playerId).GetAsync();
        Assert.NotNull(revived);
    }

    /// <summary>
    /// State has to be readable without a throw, for the same reason a client does. Anything
    /// polling it on a loop — an ECS barrier comparing a version each tick — would otherwise have
    /// to guard with a separate liveness check, which either duplicates the lookup or races it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TryGetState_ReportsAMissInsteadOfThrowingWhenNotSubscribed()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-state-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // Before subscribing: a miss, where GetState throws.
        Assert.False(resolver.TryGetState<PartyState>(playerId, out var missing));
        Assert.Null(missing);
        Assert.Throws<InvalidOperationException>(() => resolver.GetState<PartyState>(playerId));

        await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        Assert.True(resolver.TryGetState<PartyState>(playerId, out var state));
        Assert.Same(resolver.GetState<PartyState>(playerId), state);

        // After the connection is torn down it goes back to a miss — this is the window an
        // ECS-style poller lives in, and it must not throw there.
        await resolver.DisconnectAsync(playerId);
        Assert.False(resolver.TryGetState<PartyState>(playerId, out _));

        // An unknown entity is a miss too, not an argument error.
        Assert.False(resolver.TryGetState<PartyState>("", out _));
    }

    [Fact(Timeout = 60_000)]
    public async Task MetaRefFactory_BindsHandlesPerEntity()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var playerId = "ref-factory-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var factory = new MetaRefFactory<PartyServiceApiClient>(resolver);
        var handle = factory.For(playerId);

        Assert.Equal(playerId, handle.EntityId);
        Assert.False(handle.IsAlive);

        var resolved = await handle.GetAsync();
        Assert.Same(resolved, factory.For(playerId).Current);

        Assert.Throws<ArgumentException>(() => factory.For(""));
    }
}
