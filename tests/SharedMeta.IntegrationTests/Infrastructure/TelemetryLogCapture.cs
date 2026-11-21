using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Logger provider that records every entry together with the structured fields of its active
/// scopes, while armed. Registered on the silo and on the transport handler's logger factory
/// (both run in the test process), so a test can follow one call id across all hops.
/// </summary>
// Deliberately not ISupportExternalScope: one instance serves two logger factories (silo and
// handler), and an external scope provider is per factory — the second would replace the first.
// Without it each factory forwards BeginScope here, into one AsyncLocal stack.
public sealed class TelemetryLogCapture : ILoggerProvider
{
    public static TelemetryLogCapture Instance { get; } = new();

    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Scope);

    private readonly ConcurrentQueue<Entry> _entries = new();
    private readonly LoggerExternalScopeProvider _scopes = new();
    private volatile bool _armed;

    /// <summary>Starts recording; dispose to stop and clear.</summary>
    public IDisposable Arm()
    {
        _entries.Clear();
        _armed = true;
        return new Disarm(this);
    }

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Disarm(TelemetryLogCapture owner) : IDisposable
    {
        public void Dispose()
        {
            owner._armed = false;
            owner._entries.Clear();
        }
    }

    private sealed class Logger(TelemetryLogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => owner._armed;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!owner._armed) return;
            var scope = new Dictionary<string, object?>();
            owner._scopes.ForEachScope((s, acc) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object?>> fields)
                    foreach (var kv in fields) acc[kv.Key] = kv.Value;
            }, scope);
            owner._entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), scope));
        }
    }
}
