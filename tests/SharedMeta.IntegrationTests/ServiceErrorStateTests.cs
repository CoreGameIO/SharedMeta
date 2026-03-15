using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Tests framework-level service error handling:
/// - Exceptions set HasError on the API client
/// - Error state blocks further method calls
/// - ClearError() resumes normal operation
/// - OnServiceError event fires
/// - RefreshState clears error
/// - MetaLog.Error is called
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ServiceErrorStateTests
{
    private readonly TestClusterFixture _fixture;

    public ServiceErrorStateTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task Exception_SetsHasError()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"err_has_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        Assert.False(api.HasError);
        Assert.Null(api.ErrorException);

        // Act — ThrowIfNegative with negative value should throw
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.ThrowIfNegativeAsync(-1));

        // Assert
        Assert.True(api.HasError);
        Assert.NotNull(api.ErrorException);
        Assert.IsType<InvalidOperationException>(api.ErrorException);
        Assert.Contains("Negative", api.ErrorException!.Message);
    }

    [Fact(Timeout = 60_000)]
    public async Task ErrorState_BlocksFurtherCalls()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"err_block_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Put in error state
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.ThrowIfNegativeAsync(-1));
        Assert.True(api.HasError);

        // Act — any subsequent call should throw ServiceErrorStateException
        var ex = await Assert.ThrowsAsync<ServiceErrorStateException>(
            () => api.AddValueAsync(10, 1));

        Assert.Equal("ICounterService", ex.ServiceName);
        Assert.IsType<InvalidOperationException>(ex.OriginalException);
    }

    [Fact(Timeout = 60_000)]
    public async Task ClearError_ResumesCalls()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"err_clear_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Put in error state
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.ThrowIfNegativeAsync(-1));
        Assert.True(api.HasError);

        // Act — clear error
        api.ClearError();

        // Assert — can call methods again
        Assert.False(api.HasError);
        Assert.Null(api.ErrorException);

        // Positive value should succeed
        await api.ThrowIfNegativeAsync(42);
        var state = resolver.GetState<CounterState>(entityId);
        Assert.Equal(42, state.Sum);
    }

    [Fact(Timeout = 60_000)]
    public async Task OnServiceError_FiresOnException()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"err_event_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        string? firedServiceName = null;
        Exception? firedException = null;
        api.OnServiceError += (svc, ex) =>
        {
            firedServiceName = svc;
            firedException = ex;
        };

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.ThrowIfNegativeAsync(-1));

        // Assert
        Assert.Equal("ICounterService", firedServiceName);
        Assert.NotNull(firedException);
        Assert.IsType<InvalidOperationException>(firedException);
    }

    [Fact(Timeout = 60_000)]
    public async Task RefreshState_ClearsError()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var entityId = $"err_refresh_{Guid.NewGuid():N}";
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        // Put in error state
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.ThrowIfNegativeAsync(-1));
        Assert.True(api.HasError);

        // Act — RefreshState simulates reconnect
        api.RefreshState(new CounterState(), null);

        // Assert — error should be cleared
        Assert.False(api.HasError);
        Assert.Null(api.ErrorException);
    }

    [Fact(Timeout = 60_000)]
    public async Task MetaLogError_CalledOnException()
    {
        // Set up a custom logger to capture error calls
        var testLogger = new TestMetaLogger();
        var previousLogger = MetaLog.Logger;
        MetaLog.SetLogger(testLogger);

        try
        {
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var client = new TestClientSetup(server);
            await client.ConnectAsync();

            var resolver = client.CreateResolver();
            var entityId = $"err_log_{Guid.NewGuid():N}";
            var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => api.ThrowIfNegativeAsync(-1));

            // Assert — MetaLog.Error was called
            Assert.True(testLogger.ErrorCount > 0, "MetaLog.Error should have been called");
            Assert.Contains("ICounterService", testLogger.LastErrorMessage!);
            Assert.Contains("ThrowIfNegative", testLogger.LastErrorMessage!);
            Assert.NotNull(testLogger.LastException);
        }
        finally
        {
            // Restore previous logger
            MetaLog.SetLogger(previousLogger);
        }
    }

    private class TestMetaLogger : IMetaLogger
    {
        public int ErrorCount { get; private set; }
        public string? LastErrorMessage { get; private set; }
        public Exception? LastException { get; private set; }

        public bool IsEnabled(MetaLogLevel level) => true;

        public void Log(MetaLogLevel level, string message)
        {
            if (level == MetaLogLevel.Error)
            {
                ErrorCount++;
                LastErrorMessage = message;
            }
        }

        public void Log(MetaLogLevel level, string message, Exception exception)
        {
            if (level == MetaLogLevel.Error)
            {
                ErrorCount++;
                LastErrorMessage = message;
                LastException = exception;
            }
        }
    }
}
