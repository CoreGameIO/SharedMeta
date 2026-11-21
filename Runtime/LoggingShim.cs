#if !HAS_MEL

// Minimal subset of Microsoft.Extensions.Logging.Abstractions so SharedMeta and user code
// compile in Unity without the package. Every member mirrors the real signature: code written
// against this subset must compile unchanged once the real package is installed and HAS_MEL is
// defined. Do not add anything the real package does not have.

namespace Microsoft.Extensions.Logging
{
    using System;
    using System.Collections;
    using System.Globalization;
    using System.Text;

    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Critical = 5,
        None = 6,
    }

    public readonly struct EventId
    {
        public EventId(int id, string? name = null)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }
        public string? Name { get; }

        public static implicit operator EventId(int i) => new EventId(i);

        public override string ToString() => Name ?? Id.ToString(CultureInfo.InvariantCulture);
    }

    public interface ILogger
    {
        void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter);
        bool IsEnabled(LogLevel logLevel);
        IDisposable? BeginScope<TState>(TState state) where TState : notnull;
    }

    public static class LoggerExtensions
    {
        public static void LogTrace(this ILogger logger, string? message, params object?[] args) => Write(logger, LogLevel.Trace, null, message, args);
        public static void LogTrace(this ILogger logger, Exception? exception, string? message, params object?[] args) => Write(logger, LogLevel.Trace, exception, message, args);

        public static void LogDebug(this ILogger logger, string? message, params object?[] args) => Write(logger, LogLevel.Debug, null, message, args);
        public static void LogDebug(this ILogger logger, Exception? exception, string? message, params object?[] args) => Write(logger, LogLevel.Debug, exception, message, args);

        public static void LogInformation(this ILogger logger, string? message, params object?[] args) => Write(logger, LogLevel.Information, null, message, args);
        public static void LogInformation(this ILogger logger, Exception? exception, string? message, params object?[] args) => Write(logger, LogLevel.Information, exception, message, args);

        public static void LogWarning(this ILogger logger, string? message, params object?[] args) => Write(logger, LogLevel.Warning, null, message, args);
        public static void LogWarning(this ILogger logger, Exception? exception, string? message, params object?[] args) => Write(logger, LogLevel.Warning, exception, message, args);

        public static void LogError(this ILogger logger, string? message, params object?[] args) => Write(logger, LogLevel.Error, null, message, args);
        public static void LogError(this ILogger logger, Exception? exception, string? message, params object?[] args) => Write(logger, LogLevel.Error, exception, message, args);

        public static void LogCritical(this ILogger logger, string? message, params object?[] args) => Write(logger, LogLevel.Critical, null, message, args);
        public static void LogCritical(this ILogger logger, Exception? exception, string? message, params object?[] args) => Write(logger, LogLevel.Critical, exception, message, args);

        public static void Log(this ILogger logger, LogLevel logLevel, string? message, params object?[] args) => Write(logger, logLevel, null, message, args);
        public static void Log(this ILogger logger, LogLevel logLevel, Exception? exception, string? message, params object?[] args) => Write(logger, logLevel, exception, message, args);

        private static readonly Func<FormattedLogValues, Exception?, string> Formatter = (state, _) => state.ToString();

        private static void Write(ILogger logger, LogLevel logLevel, Exception? exception, string? message, object?[] args)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            logger.Log(logLevel, default, new FormattedLogValues(message, args), exception, Formatter);
        }

        // Positional message-template rendering, as the real package does: "{Name}" / "{0}" take
        // args in order, "{Value:N2}" applies a format, "{{" / "}}" escape, null prints "(null)",
        // a non-string sequence prints comma-joined.
        private readonly struct FormattedLogValues
        {
            private readonly string? _format;
            private readonly object?[]? _values;

            public FormattedLogValues(string? format, object?[]? values)
            {
                _format = format;
                _values = values;
            }

            public override string ToString()
            {
                if (_format == null) return "[null]";
                if (_values == null || _values.Length == 0) return _format;

                var sb = new StringBuilder(_format.Length + 16);
                var argIndex = 0;
                for (var i = 0; i < _format.Length; i++)
                {
                    var c = _format[i];
                    if (c == '{')
                    {
                        if (i + 1 < _format.Length && _format[i + 1] == '{') { sb.Append('{'); i++; continue; }
                        var end = _format.IndexOf('}', i + 1);
                        if (end < 0) { sb.Append(_format, i, _format.Length - i); break; }
                        var hole = _format.Substring(i + 1, end - i - 1);
                        var colon = hole.IndexOf(':');
                        var fmt = colon >= 0 ? hole.Substring(colon + 1) : null;
                        sb.Append(argIndex < _values.Length ? Render(_values[argIndex], fmt) : "{" + hole + "}");
                        argIndex++;
                        i = end;
                        continue;
                    }
                    if (c == '}' && i + 1 < _format.Length && _format[i + 1] == '}') { sb.Append('}'); i++; continue; }
                    sb.Append(c);
                }
                return sb.ToString();
            }

            private static string Render(object? value, string? format)
            {
                if (value == null) return "(null)";
                if (format != null && value is IFormattable formattable)
                    return formattable.ToString(format, CultureInfo.InvariantCulture);
                if (value is IEnumerable sequence && !(value is string))
                {
                    var sb = new StringBuilder();
                    foreach (var item in sequence)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(item ?? "(null)");
                    }
                    return sb.ToString();
                }
                return value.ToString() ?? string.Empty;
            }
        }
    }
}

namespace Microsoft.Extensions.Logging.Abstractions
{
    using System;

    public class NullLogger : ILogger
    {
        public static NullLogger Instance { get; } = new NullLogger();

        private NullLogger() { }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}

#endif
