using SharedMeta.Core;
using SharedMeta.Test.SplitConfig.Models;

namespace SharedMeta.Test.SplitConfig.Services
{
    /// <summary>
    /// Declares <c>DefaultConfig = true</c> WITHOUT explicit <c>ConfigType</c> — relies on
    /// the generator finding <see cref="SplitConsumerConfig"/> via the
    /// <c>[MetaConfig(Default = true)]</c> attribute. The config lives in a referenced
    /// assembly, so this exercises the cross-assembly discovery path that 0.20.2 fixes.
    /// </summary>
    [MetaService(StateType = typeof(SplitConsumerState), DefaultConfig = true)]
    public interface ISplitConsumerService : IMetaService
    {
        [MetaMethod(Alias = "Bump", Mode = ExecutionMode.Server)]
        int Bump();
    }
}
