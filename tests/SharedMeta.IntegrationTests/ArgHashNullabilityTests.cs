using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The argument hash detects wire-shape drift between client and server builds. A nullable
/// reference annotation is not on the wire — flipping &lt;Nullable&gt; or annotating a parameter
/// must not tell every client its signature drifted. <c>Nullable&lt;T&gt;</c> is a different wire
/// shape and must still count.
/// </summary>
public class ArgHashNullabilityTests
{
    [Fact]
    public void NullableReferenceAnnotation_DoesNotChangeArgHash()
        => Assert.Equal(ArgHashOf("Echo"), ArgHashOf("EchoAnnotated"));

    [Fact]
    public void NullableValueType_ChangesArgHash()
        => Assert.NotEqual(ArgHashOf("Count"), ArgHashOf("CountNullable"));

    private static ulong ArgHashOf(string alias)
        => GameServiceDiscoveryBase.ClientSignature.KnownMethods
            .Single(m => m.ServiceName == nameof(INullabilityHashFixtureService) && m.Alias == alias)
            .ArgHash;
}
