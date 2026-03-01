using System;
using Microsoft.Extensions.Logging;
using SharedMeta.Core.Logging;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Adapter that bridges Microsoft.Extensions.Logging.ILogger to IMetaLogger.
    /// Automatically includes EntityId in log messages.
    /// </summary>
    internal class MetaLoggerAdapter : IMetaLogger
    {
        private readonly ILogger _logger;
        private readonly string _entityId;

        public MetaLoggerAdapter(ILogger logger, string entityId)
        {
            _logger = logger;
            _entityId = entityId;
        }

        public bool IsEnabled(MetaLogLevel level) => _logger.IsEnabled(ToLogLevel(level));

        public void Log(MetaLogLevel level, string message)
            => _logger.Log(ToLogLevel(level), "[{EntityId}] {Message}", _entityId, message);

        public void Log(MetaLogLevel level, string message, Exception exception)
            => _logger.Log(ToLogLevel(level), exception, "[{EntityId}] {Message}", _entityId, message);

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
