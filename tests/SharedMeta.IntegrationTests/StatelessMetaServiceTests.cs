using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Server.Core;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// StatelessMetaService — no entity/state, resolution requires only materializing the linked
/// [MetaConfig]. These tests cover the two new primitives directly:
/// <see cref="MetaServiceResolver.ResolveConfigAsync{TConfig}"/> (client-side, Path 2 — resolve
/// via MetaClient with no entity subscribe) and the generator-emitted
/// <c>GeneratedStatelessConfigVersionSource</c> (server-side, Path 2's version-resolve RPC).
/// <para>
/// Path 1 (sibling-style <c>GetI{Iface}Async()</c> from another [MetaServiceImpl], see
/// <see cref="SharedMeta.Test.Meta1.StatelessConsumerService"/>) is exercised at compile time
/// only here: its <c>#if SHAREDMETA_SERVER</c> branch only has a real body when the declaring
/// assembly itself defines that symbol — the same constraint the existing multi-config sibling
/// feature already has (see <c>SiblingExecutionTests.SiblingMultiConfig_SiblingSeesItsOwnTypedConfig</c>),
/// so it isn't re-tested for real execution here.
/// </para>
/// </summary>
public class StatelessMetaServiceTests
{
    [Fact]
    public async Task ResolveConfigAsync_ReturnsRegisteredStaticConfig()
    {
        var resolver = new MetaServiceResolver(
            (_, _) => throw new NotSupportedException("network not used by ResolveConfigAsync"),
            new SharedMeta.Serialization.MemoryPack.MemoryPackMetaSerializer(),
            new ExecutionModeProvider());

        resolver.RegisterConfigProvider(new StaticConfigProvider<PricingConfig>(new PricingConfig { BaseCost = 99 }));

        var config = await resolver.ResolveConfigAsync<PricingConfig>(default);

        Assert.NotNull(config);
        Assert.Equal(99, config!.BaseCost);
    }

    [Fact]
    public async Task ResolveConfigAsync_ThrowsWhenNoProviderRegistered()
    {
        var resolver = new MetaServiceResolver(
            (_, _) => throw new NotSupportedException("network not used by ResolveConfigAsync"),
            new SharedMeta.Serialization.MemoryPack.MemoryPackMetaSerializer(),
            new ExecutionModeProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveConfigAsync<PricingConfig>(default));
    }

    [Fact]
    public void GeneratedStatelessConfigVersionSource_ResolvesRegisteredConfigType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetaConfigProvider<PricingConfig>>(new FakePricingConfigProvider());
        using var sp = services.BuildServiceProvider();

        var source = new GeneratedStatelessConfigVersionSource(sp);

        var version = source.ResolveVersion("PricingConfig", "1.0.0");

        // PricingConfig declares no [MetaConfigVersion] rules — ResolveForClient's documented
        // fallback is default(MetaConfigVersion) (0.0.0), not a throw.
        Assert.NotNull(version);
        Assert.Equal(new MetaConfigVersion(0, 0, 0), version!.Value);
    }

    [Fact]
    public void GeneratedStatelessConfigVersionSource_ReturnsNullForUnknownConfigType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetaConfigProvider<PricingConfig>>(new FakePricingConfigProvider());
        using var sp = services.BuildServiceProvider();

        var source = new GeneratedStatelessConfigVersionSource(sp);

        Assert.Null(source.ResolveVersion("SomeOtherConfig", "1.0.0"));
    }

    [Fact]
    public void GeneratedStatelessConfigVersionSource_ReturnsNullWhenProviderNotRegistered()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var source = new GeneratedStatelessConfigVersionSource(sp);

        Assert.Null(source.ResolveVersion("PricingConfig", "1.0.0"));
    }

    private sealed class FakePricingConfigProvider : IMetaConfigProvider<PricingConfig>
    {
        public PricingConfig GetConfig(MetaConfigVersion version) => new PricingConfig();
    }
}
