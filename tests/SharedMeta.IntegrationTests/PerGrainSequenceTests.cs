using SharedMeta.Client;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Two state types under one entityId are two grains with independent sequences. The client used
/// to keep one running maximum per entityId, so a Resume claimed the busier grain's number for
/// both — a spurious Refreshed at best, a wrong Continued when the numbers happened to line up.
/// Ops now carry the state-type id and the client tracks each grain separately.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class PerGrainSequenceTests
{
    private readonly TestClusterFixture _fixture;

    public PerGrainSequenceTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task StateTypesSharingEntityId_TrackIndependentSequences()
    {
        var entityId = $"per_grain_seq_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        var aliceCounter = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var aliceDesync = await alice.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(entityId);
        var bobCounter = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var bobDesync = await bob.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        var dispatcher = (ClientDispatcher)alice.MetaClient.Dispatcher;
        var counterBase = dispatcher.GetLastKnownEntitySequence(entityId, typeof(CounterState).FullName!);
        var desyncBase = dispatcher.GetLastKnownEntitySequence(entityId, typeof(DesyncTestState).FullName!);

        await bobCounter.AddValueAsync(1, 1);
        await bobCounter.AddValueAsync(1, 2);
        await bobCounter.AddValueAsync(1, 3);
        await bobDesync.AddAsync(7);
        await WaitUntilAsync(() => aliceCounter.State.Sum == 3 && aliceDesync.State.Value == 7);

        Assert.Equal(counterBase + 3, dispatcher.GetLastKnownEntitySequence(entityId, typeof(CounterState).FullName!));
        Assert.Equal(desyncBase + 1, dispatcher.GetLastKnownEntitySequence(entityId, typeof(DesyncTestState).FullName!));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached in 5s.");
            await Task.Delay(20);
        }
    }
}
