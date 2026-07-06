using SharedMeta.Core;
using SharedMeta.Test.SplitConfig.Models;

namespace SharedMeta.Test.SplitConfig.Services
{
    /// <summary>
    /// Declares <c>DefaultConfig = true</c> WITHOUT explicit <c>ConfigType</c> — relies on
    /// the generator finding <see cref="SplitConsumerConfig"/> via the
    /// <c>[MetaConfig(Default = true)]</c> attribute. The config lives in a referenced
    /// assembly, so this exercises the cross-assembly discovery path that 0.20.2 fixes.
    /// Deliberately NOT migrated to [ServiceConfig] — [ServiceConfig] always takes an
    /// explicit typeof(TConfig) by design (no "search assemblies for the default" magic,
    /// exactly the kind of ambiguity that caused a real DefaultConfig collision bug this
    /// session). This test is permanent coverage for the legacy cross-assembly path, not a
    /// migration gap.
    /// </summary>
#pragma warning disable CS0618
    [MetaService(StateType = typeof(SplitConsumerState), DefaultConfig = true)]
#pragma warning restore CS0618
    public interface ISplitConsumerService : IMetaService
    {
        [MetaMethod(Alias = "Bump", Mode = ExecutionMode.Server)]
        int Bump();
    }
}
