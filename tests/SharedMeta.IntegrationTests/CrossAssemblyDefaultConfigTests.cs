using SharedMeta.Test.SplitConfig.Models;
using SharedMeta.Test.SplitConfig.Services.DependencyInjection;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.20.2 regression: <c>[MetaService(DefaultConfig = true)]</c> without explicit
/// <c>ConfigType</c> must resolve <c>[MetaConfig(Default = true)]</c> classes from
/// <strong>referenced assemblies</strong>, not just the consumer's own source. The
/// fixture splits config (<see cref="SplitConsumerConfig"/> in
/// <c>SharedMeta.Test.SplitConfig.Models</c>) from the consuming service
/// (<see cref="SharedMeta.Test.SplitConfig.Services.ISplitConsumerService"/>) — the
/// natural Models / Services project layout that triggered the bug in production.
///
/// Pre-fix symptom: <c>FindDefaultConfigType</c> only walked <c>compilation.SyntaxTrees</c>,
/// so the config type was invisible. Generated <c>MetaServiceConfig.ConfigType</c> stayed
/// null, <c>MetaServiceResolver.ResolveConfigAsync</c> short-circuited via
/// <c>if (config.ConfigType == null) return null;</c>, <c>Context.Config</c> became null,
/// and user code NRE'd at the first <c>Config.X</c> access — identical to the pre-0.17.0
/// silent zeroed-config bug despite that fail-loud guard being in place.
/// </summary>
public class CrossAssemblyDefaultConfigTests
{
    [Fact]
    public void DefaultConfig_ResolvedFromReferencedAssembly()
    {
        // Generated GetSplitConsumerServiceServiceConfig() is the materialized
        // MetaServiceConfig. If the generator's FindDefaultConfigType walk had failed (the
        // pre-0.20.2 bug), ConfigType would be null and ResolveConfigAsync would short-
        // circuit at runtime. The fact that ConfigType is set proves the cross-assembly
        // discovery path covered the [MetaConfig(Default = true)] class living in the
        // referenced Models project.
        var config = SplitConsumerServiceServiceExtensions.GetSplitConsumerServiceServiceConfig();

        Assert.NotNull(config.ConfigType);
        Assert.Equal(typeof(SplitConsumerConfig), config.ConfigType);
    }
}
