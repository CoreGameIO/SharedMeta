using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.SplitConfig.Models
{
    /// <summary>
    /// Repro fixture for the 0.20.2 cross-assembly DefaultConfig discovery bug. Lives in a
    /// separate assembly from <see cref="ISplitConsumerService"/>, mimicking the typical
    /// Models / Services project split that a real product uses (config in Models.csproj,
    /// services in Services.csproj). Prior to 0.20.2, the generator's
    /// <c>FindDefaultConfigType</c> only walked <c>compilation.SyntaxTrees</c> — this class
    /// was invisible to the consumer's compilation, so the generated
    /// <c>*ServiceExtensions.g.cs</c> silently omitted both <c>ConfigType</c> and the
    /// auto-default <c>TryRegisterConfigProvider</c> emission.
    /// </summary>
    [MetaConfig(Default = true)]
    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class SplitConsumerConfig
    {
        [MemoryPackOrder(0)] public int Threshold { get; set; } = 42;
    }
}
