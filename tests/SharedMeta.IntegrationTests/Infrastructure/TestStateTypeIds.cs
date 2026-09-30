namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Client state-type ids of the test build, for tests that hand-build wire requests. The id is the
/// state type's position in the generated client signature — what the dispatcher sends.
/// </summary>
public static class TestStateTypeIds
{
    public static ushort Of<TState>()
    {
        var index = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ClientSignature.KnownStateTypes
            .IndexOf(typeof(TState).FullName!);
        if (index < 0)
            throw new InvalidOperationException($"{typeof(TState).FullName} is not in the Test.Meta1 client signature.");
        return (ushort)index;
    }
}
