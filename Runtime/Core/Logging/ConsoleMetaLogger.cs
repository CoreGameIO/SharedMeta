using System;

namespace SharedMeta.Core.Logging
{
    /// <summary>
    /// Console-based logger for console applications and testing.
    /// </summary>
    public class ConsoleMetaLogger : IMetaLogger
    {
        private readonly MetaLogLevel _minLevel;

        public ConsoleMetaLogger(MetaLogLevel minLevel = MetaLogLevel.Debug)
        {
            _minLevel = minLevel;
        }

        public bool IsEnabled(MetaLogLevel level) => level >= _minLevel;

        public void Log(MetaLogLevel level, string message)
        {
            if (!IsEnabled(level)) return;
            Console.WriteLine($"[{LevelTag(level)}] {message}");
        }

        public void Log(MetaLogLevel level, string message, Exception exception)
        {
            if (!IsEnabled(level)) return;
            Console.WriteLine($"[{LevelTag(level)}] {message}: {exception}");
        }

        private static string LevelTag(MetaLogLevel level) => level switch
        {
            MetaLogLevel.Debug => "DBG",
            MetaLogLevel.Info => "INF",
            MetaLogLevel.Warning => "WRN",
            MetaLogLevel.Error => "ERR",
            _ => "???"
        };
    }
}
