using System;

namespace SharedMeta.Core.Logging
{
    /// <summary>
    /// No-op logger. Default before any logger is configured.
    /// </summary>
    public sealed class NullMetaLogger : IMetaLogger
    {
        public static readonly NullMetaLogger Instance = new();

        private NullMetaLogger() { }

        public bool IsEnabled(MetaLogLevel level) => false;
        public void Log(MetaLogLevel level, string message) { }
        public void Log(MetaLogLevel level, string message, Exception exception) { }
    }
}
