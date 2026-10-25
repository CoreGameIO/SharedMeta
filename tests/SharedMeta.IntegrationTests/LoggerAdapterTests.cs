using Microsoft.Extensions.Logging;
using SharedMeta.Core.Logging;
using Xunit;

namespace SharedMeta.IntegrationTests;

public class LoggerAdapterTests
{
    [Fact]
    public void ForwardsLevelAndException_KeepsBracesVerbatim()
    {
        var logger = new RecordingLogger();
        var adapter = new LoggerAdapter(logger);
        var error = new InvalidOperationException("boom");

        adapter.Log(MetaLogLevel.Warning, "state {\"gold\":5} for {player}", error);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("state {\"gold\":5} for {player}", entry.Message);
        Assert.Same(error, entry.Exception);
        Assert.False(adapter.IsEnabled(MetaLogLevel.Debug));
        Assert.True(adapter.IsEnabled(MetaLogLevel.Info));
    }

    private sealed class RecordingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
