using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharedMeta.Core.Logging
{
    /// <summary>
    /// Static accessor for the global framework logger.
    /// Call MetaLog.SetLogger() at application startup before any SharedMeta operations.
    /// </summary>
    public static class MetaLog
    {
        private static volatile ILogger _logger = NullLogger.Instance;

        // Messages here are already rendered (interpolated strings, desync dumps with braces), so
        // they go through as plain state with an identity formatter — never as a message template,
        // which would misparse "{" in the payload.
        private static readonly Func<string, Exception?, string> PlainMessage = (message, _) => message;

        /// <summary>
        /// The current global logger instance.
        /// </summary>
        public static ILogger Logger => _logger;

        /// <summary>
        /// Set the global logger. Call once at application startup.
        /// </summary>
        public static void SetLogger(ILogger? logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message, null);

        public static void Info(string message) => Write(LogLevel.Information, message, null);

        public static void Warning(string message) => Write(LogLevel.Warning, message, null);

        public static void Error(string message) => Write(LogLevel.Error, message, null);

        public static void Error(string message, Exception exception) => Write(LogLevel.Error, message, exception);

        private static void Write(LogLevel level, string message, Exception? exception)
        {
            var l = _logger;
            if (l.IsEnabled(level))
                l.Log(level, default, message, exception, PlainMessage);
        }
    }
}
