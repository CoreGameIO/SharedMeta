using System;
using Microsoft.Extensions.Logging;

namespace SharedMeta.Core.Logging
{
    /// <summary>
    /// Routes SharedMeta logging into a Microsoft.Extensions.Logging <see cref="ILogger"/>,
    /// so any logger built on it (console, ZLogger, Serilog, ...) receives SharedMeta output.
    /// </summary>
    public sealed class LoggerAdapter : IMetaLogger
    {
        private readonly ILogger _logger;

        public LoggerAdapter(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsEnabled(MetaLogLevel level) => _logger.IsEnabled(ToLogLevel(level));

        // The message goes in as state with an identity formatter rather than as a message template:
        // SharedMeta messages carry braces (state dumps, ids), and a template would parse them as holes.
        public void Log(MetaLogLevel level, string message)
            => _logger.Log(ToLogLevel(level), default, message, null, (state, _) => state);

        public void Log(MetaLogLevel level, string message, Exception exception)
            => _logger.Log(ToLogLevel(level), default, message, exception, (state, _) => state);

        private static LogLevel ToLogLevel(MetaLogLevel level) => level switch
        {
            MetaLogLevel.Debug => LogLevel.Debug,
            MetaLogLevel.Info => LogLevel.Information,
            MetaLogLevel.Warning => LogLevel.Warning,
            MetaLogLevel.Error => LogLevel.Error,
            _ => LogLevel.Information
        };
    }
}
