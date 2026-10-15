using System;

namespace SharedMeta.Core.Logging
{
    /// <summary>
    /// Log levels for client-side logging.
    /// </summary>
    public enum MetaLogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// Lightweight logging interface for SharedMeta client-side code.
    /// Implement this for your target platform (Console, Unity, etc.).
    /// </summary>
    public interface IMetaLogger
    {
        /// <summary>
        /// Whether the given log level is enabled.
        /// Use this to skip expensive string formatting.
        /// </summary>
        bool IsEnabled(MetaLogLevel level);

        /// <summary>
        /// Log a message at the specified level.
        /// </summary>
        void Log(MetaLogLevel level, string message);

        /// <summary>
        /// Log a message with an exception at the specified level.
        /// </summary>
        void Log(MetaLogLevel level, string message, Exception exception);
    }
}
