using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// <c>[MetaMethod(ReplayEvents = ...)]</c> — client-side UI observation points around an incoming
/// broadcast. <c>On{Method}_Replaying</c> fires before the broadcast touches local state,
/// <c>On{Method}_Replayed</c> after, so a view can read a before/after delta and animate it.
///
/// The "before" half is the one with a trap in it. Handlers on <c>INetwork.OnBroadcast</c> run in
/// subscription order, and the entity-level handler — which applies <c>StateBytes</c> and
/// <c>PatchBytes</c> — is wired when the connection is created, ahead of any API client. A pre-hook
/// raised from the API client's own dispatch would therefore read pre-change state for a replayed
/// body and post-change state under ServerPatch / ServerReplace: the same subscription silently
/// meaning two different things depending on a mode the UI author does not control. It is raised
/// from <c>OnBroadcastPre</c> instead, ahead of every application path.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ReplayEventsTests
{
    private readonly TestClusterFixture _fixture;

    public ReplayEventsTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Replayed body path: the observer must straddle the mutation.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Replaying_ReadsPreChangeState_OnReplayedBody()
    {
        var (entityId, api1, api2, resolver2, c1, c2) = await TwoClientsAsync("re_replay");
        await using var _1 = c1;
        await using var _2 = c2;

        long? sumAtReplaying = null;
        long? sumAtReplayed = null;
        api2.OnAddValue_Replaying += _ => sumAtReplaying = resolver2.GetState<CounterState>(entityId).Sum;
        api2.OnAddValue_Replayed += _ => sumAtReplayed = resolver2.GetState<CounterState>(entityId).Sum;

        await api1.AddValueAsync(10, 1);
        await Task.Delay(200);

        Assert.Equal(0, sumAtReplaying);
        Assert.Equal(10, sumAtReplayed);
    }

    /// <summary>
    /// The case the extra event exists for. Under ServerPatch the state arrives as a diff applied
    /// by the entity-level handler, not by a replayed body — so an observer raised from the API
    /// client's own dispatch would already be too late.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Replaying_ReadsPreChangeState_UnderServerPatch()
    {
        _fixture.ExecutionModeProvider.SetMode(
            global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_Add_v0,
            ExecutionMode.ServerPatch);
        try
        {
            var (entityId, api1, api2, resolver2, c1, c2) = await TwoClientsAsync("re_patch");
            await using var _1 = c1;
            await using var _2 = c2;

            long? sumAtReplaying = null;
            long? sumAtReplayed = null;
            api2.OnAddValue_Replaying += _ => sumAtReplaying = resolver2.GetState<CounterState>(entityId).Sum;
            api2.OnAddValue_Replayed += _ => sumAtReplayed = resolver2.GetState<CounterState>(entityId).Sum;

            await api1.AddValueAsync(25, 1);
            await Task.Delay(200);

            Assert.Equal(0, sumAtReplaying);
            Assert.Equal(25, sumAtReplayed);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    /// <summary>Arguments reach both halves, typed.</summary>
    [Fact(Timeout = 60_000)]
    public async Task BothEvents_CarryTypedArguments()
    {
        var (_, api1, api2, _, c1, c2) = await TwoClientsAsync("re_args");
        await using var _1 = c1;
        await using var _2 = c2;

        (int value, int seq)? beforeArgs = null;
        (int value, int seq)? afterArgs = null;
        api2.OnAddValue_Replaying += a => beforeArgs = a;
        api2.OnAddValue_Replayed += a => afterArgs = a;

        await api1.AddValueAsync(7, 3);
        await Task.Delay(200);

        Assert.Equal((7, 3), beforeArgs);
        Assert.Equal((7, 3), afterArgs);
    }

    /// <summary>
    /// Opt-in is the whole point: a method that declares no <c>ReplayEvents</c> must produce no
    /// event fields at all, and a Signal method must produce none even if it asks, because it can
    /// never reach the replay path.
    /// </summary>
    [Fact]
    public void EventsAreOptIn()
    {
        var t = typeof(CounterServiceApiClient);

        // AddValue: ReplayEvents.Both
        Assert.NotNull(t.GetEvent("OnAddValue_Replaying"));
        Assert.NotNull(t.GetEvent("OnAddValue_Replayed"));

        // AddClamped: ReplayEvents.After only
        Assert.Null(t.GetEvent("OnAddClamped_Replaying"));
        Assert.NotNull(t.GetEvent("OnAddClamped_Replayed"));

        // Reset: unannotated
        Assert.Null(t.GetEvent("OnReset_Replaying"));
        Assert.Null(t.GetEvent("OnReset_Replayed"));

        // NotifyHeartbeat is a Signal — never broadcasts, so never gets events.
        Assert.Null(t.GetEvent("OnNotifyHeartbeat_Replaying"));
        Assert.Null(t.GetEvent("OnNotifyHeartbeat_Replayed"));
    }

    /// <summary>
    /// A throwing UI handler must not take the broadcast pipeline down with it — the pre-hook runs
    /// before anything has been applied, so propagating would leave the state un-updated.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ThrowingReplayingHandler_DoesNotBlockDelivery()
    {
        var (entityId, api1, api2, resolver2, c1, c2) = await TwoClientsAsync("re_throw");
        await using var _1 = c1;
        await using var _2 = c2;

        api2.OnAddValue_Replaying += _ => throw new InvalidOperationException("UI handler blew up");

        await api1.AddValueAsync(4, 1);
        await Task.Delay(200);

        Assert.Equal(4, resolver2.GetState<CounterState>(entityId).Sum);
    }

    private async Task<(string entityId, CounterServiceApiClient api1, CounterServiceApiClient api2,
        SharedMeta.Client.MetaServiceResolver resolver2, TestClientSetup c1, TestClientSetup c2)>
        TwoClientsAsync(string prefix)
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var c1 = new TestClientSetup(server, prefix + "_a");
        var c2 = new TestClientSetup(server, prefix + "_b");
        await c1.ConnectAsync();
        await c2.ConnectAsync();

        var entityId = $"{prefix}_{Guid.NewGuid():N}";
        var resolver1 = c1.CreateResolver();
        var resolver2 = c2.CreateResolver();
        var api1 = await resolver1.GetServiceAsync<CounterServiceApiClient>(entityId);
        var api2 = await resolver2.GetServiceAsync<CounterServiceApiClient>(entityId);
        return (entityId, api1, api2, resolver2, c1, c2);
    }
}
