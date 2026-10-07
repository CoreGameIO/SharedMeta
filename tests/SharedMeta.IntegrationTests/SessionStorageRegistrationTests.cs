using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using SharedMeta.Server.Core.Session;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The session-record provider falls back to "Default" unless the host registered its own,
/// regardless of whether the host registers before or after the framework.
/// </summary>
public class SessionStorageRegistrationTests
{
    [Fact]
    public void Unregistered_FallsBackToDefault()
    {
        var defaultStorage = new FakeStorage();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGrainStorage>("Default", defaultStorage);
        services.AddSharedMetaSessionStorage();

        using var sp = services.BuildServiceProvider();
        Assert.Same(defaultStorage, sp.GetRequiredKeyedService<IGrainStorage>(SessionStorage.ProviderName));
    }

    [Fact]
    public void HostRegisteredBefore_Wins()
    {
        var hostStorage = new FakeStorage();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGrainStorage>("Default", new FakeStorage());
        services.AddKeyedSingleton<IGrainStorage>(SessionStorage.ProviderName, hostStorage);
        services.AddSharedMetaSessionStorage();

        using var sp = services.BuildServiceProvider();
        Assert.Same(hostStorage, sp.GetRequiredKeyedService<IGrainStorage>(SessionStorage.ProviderName));
    }

    [Fact]
    public void HostRegisteredAfter_Wins()
    {
        var hostStorage = new FakeStorage();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IGrainStorage>("Default", new FakeStorage());
        services.AddSharedMetaSessionStorage();
        services.AddKeyedSingleton<IGrainStorage>(SessionStorage.ProviderName, hostStorage);

        using var sp = services.BuildServiceProvider();
        Assert.Same(hostStorage, sp.GetRequiredKeyedService<IGrainStorage>(SessionStorage.ProviderName));
    }

    private sealed class FakeStorage : IGrainStorage
    {
        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;
        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;
        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => Task.CompletedTask;
    }
}
