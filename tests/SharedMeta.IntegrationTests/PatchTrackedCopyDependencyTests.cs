using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// A state with a ServerPatch method runs its calls through the generated <c>_PatchTracked</c>
/// copy whenever patch tracking is on (ServerPatch, force-patch, deep desync — forced in this
/// cluster). The copy must resolve the same dependencies and accessors as the raw impl.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class PatchTrackedCopyDependencyTests
{
    private readonly TestClusterFixture _fixture;

    public PatchTrackedCopyDependencyTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerPatchThroughPatchState_AndBridgeDependency_WorkInTheCopy()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var entityId = $"patch_copy_{Guid.NewGuid():N}";
        var api = await client.CreateResolver().GetServiceAsync<PatchCopyServiceApiClient>(entityId);

        await api.BumpAsync(3);
        var looked = await api.LookupAndStoreAsync(4);

        Assert.Equal(40, looked);
        Assert.Equal(3, api.State.Value);
        Assert.Equal(40, api.State.Looked);
        Assert.Empty(client.DetectedIssues);
    }
}
