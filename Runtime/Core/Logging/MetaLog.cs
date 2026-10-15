using System;

namespace SharedMeta.Core.Logging
{
    /// <summary>
    /// Static accessor for the global MetaLogger.
    /// Call MetaLog.SetLogger() at application startup before any SharedMeta operations.
    /// </summary>
    public static class MetaLog
    {
        private static volatile IMetaLogger _logger = NullMetaLogger.Instance;

        /// <summary>
        /// The current global logger instance.
        /// </summary>
        public static IMetaLogger Logger => _logger;

        /// <summary>
        /// Set the global logger. Call once at application startup.
        /// </summary>
        public static void SetLogger(IMetaLogger logger)
        {
            _logger = logger ?? NullMetaLogger.Instance;
        }

        public static void Debug(string message)
        {
            var l = _logger;
            if (l.IsEnabled(MetaLogLevel.Debug))
                l.Log(MetaLogLevel.Debug, message);
        }

        public static void Info(string message)
        {
            var l = _logger;
            if (l.IsEnabled(MetaLogLevel.Info))
                l.Log(MetaLogLevel.Info, message);
        }

        public static void Warning(string message)
        {
            var l = _logger;
            if (l.IsEnabled(MetaLogLevel.Warning))
                l.Log(MetaLogLevel.Warning, message);
        }

        public static void Error(string message)
        {
            _logger.Log(MetaLogLevel.Error, message);
        }

        public static void Error(string message, Exception exception)
        {
            _logger.Log(MetaLogLevel.Error, message, exception);
        }
    }
}
