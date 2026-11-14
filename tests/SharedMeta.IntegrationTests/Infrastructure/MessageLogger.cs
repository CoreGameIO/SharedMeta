using Microsoft.Extensions.Logging;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Test logger that hands each rendered message to <see cref="Write"/> — the shape the capturing
/// loggers in tests actually need, without each one re-implementing <see cref="ILogger"/>.
/// </summary>
internal abstract class MessageLogger : ILogger
{
    protected abstract void Write(LogLevel level, string message, Exception? exception);

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Write(logLevel, formatter(state, exception), exception);
}
